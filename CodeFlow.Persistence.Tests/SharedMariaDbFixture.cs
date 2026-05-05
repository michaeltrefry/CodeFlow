using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

/// <summary>
/// xunit collection-scoped fixture that owns ONE shared MariaDB Testcontainer for the
/// entire <c>CodeFlow.Persistence.Tests</c> assembly (sc-699 Phase A2). Replaces the
/// per-class <c>MariaDbBuilder</c> + <c>MariaDbContainer</c> pattern that was costing
/// ~5–10s of container churn × 20 test classes = ~150s of wall-time-per-suite-run for
/// no actual test work.
///
/// Each test class still gets a clean schema by calling
/// <see cref="EnsureDatabaseAsync"/> with a unique name (the class type name is the
/// natural choice — stable across runs, distinct across classes). Migrations are run
/// per-DB in the test class's <c>InitializeAsync</c>; <c>DisposeAsync</c> drops the
/// database so re-runs of the same class start clean.
///
/// Tests inside the collection run **serially** by default; Phase B re-introduces
/// parallelism within the collection by giving each test (rather than each class) its
/// own DB name.
/// </summary>
public sealed class SharedMariaDbFixture : IAsyncLifetime
{
    // The testcontainers-created application user has privileges only on its own database;
    // EnsureDatabaseAsync needs CREATE / DROP / GRANT, which are root-only. We pin a known
    // root password via env so the fixture can open a server-scoped admin connection. Tests
    // receive a per-class connection string from EnsureDatabaseAsync (root-scoped, but
    // pointed at their own database name) so the application code under test exercises real
    // SQL while still being isolated from sibling test classes.
    private const string RootPassword = "codeflow_test_root";
    private const string TemplateDatabaseName = "_codeflow_template";

    public MariaDbContainer Container { get; } = new MariaDbBuilder("mariadb:11.4")
        .WithEnvironment("MARIADB_ROOT_PASSWORD", RootPassword)
        .Build();

    /// <summary>
    /// Cached <c>CREATE TABLE</c> statements captured once from the template database at
    /// fixture startup. EnsureDatabaseAsync executes these against each per-class DB to
    /// rebuild the schema in milliseconds — versus re-running EF migrations which is
    /// ~3–5s per database. We use <c>SHOW CREATE TABLE</c> rather than
    /// <c>CREATE TABLE ... LIKE</c> because the latter does not copy foreign-key
    /// constraints (MariaDB documented behaviour), and at least one repository test
    /// asserts on FK cascade delete.
    /// </summary>
    private IReadOnlyList<TemplateTable> templateTables = Array.Empty<TemplateTable>();

    private sealed record TemplateTable(string Name, string CreateStatement);

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
        await CreateTemplateSchemaAsync();
    }

    /// <summary>
    /// Build the schema once into a "template" database (Phase B): apply EF migrations
    /// against an empty DB and capture the resulting table list. Per-class databases
    /// then clone the structure via <c>CREATE TABLE ... LIKE</c> and inherit the
    /// migration-history rows so EF treats their schema as already-up-to-date.
    /// </summary>
    private async Task CreateTemplateSchemaAsync()
    {
        await ExecuteServerSqlAsync($"CREATE DATABASE IF NOT EXISTS `{TemplateDatabaseName}`;");

        var templateConnectionString = BuildConnectionStringForDatabase(TemplateDatabaseName);
        var optionsBuilder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(optionsBuilder, templateConnectionString);

        await using (var ctx = new CodeFlowDbContext(optionsBuilder.Options))
        {
            await ctx.Database.MigrateAsync();
        }

        // Capture the table names AND their CREATE statements. SHOW CREATE TABLE returns
        // the schema with foreign-key constraints intact (CREATE TABLE LIKE does NOT —
        // MariaDB docs explicitly call out that FK definitions are dropped). One of the
        // repository tests asserts on FK cascade delete, so we MUST preserve FKs.
        var names = new List<string>();
        await using (var connection = OpenRootConnection(TemplateDatabaseName))
        {
            await using var listCmd = connection.CreateCommand();
            listCmd.CommandText = "SHOW FULL TABLES WHERE Table_type = 'BASE TABLE';";
            await using var reader = await listCmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                names.Add(reader.GetString(0));
            }
        }

        var captured = new List<TemplateTable>();
        await using (var connection = OpenRootConnection(TemplateDatabaseName))
        {
            foreach (var name in names)
            {
                await using var showCmd = connection.CreateCommand();
                showCmd.CommandText = $"SHOW CREATE TABLE `{name}`;";
                await using var reader = await showCmd.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    // Column 0 is the table name, column 1 is the CREATE TABLE statement.
                    captured.Add(new TemplateTable(name, reader.GetString(1)));
                }
            }
        }

        templateTables = captured;
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }

    /// <summary>
    /// Creates the named database on the shared server (idempotent — IF NOT EXISTS),
    /// clones the schema from the template via <c>CREATE TABLE ... LIKE</c>, and copies
    /// the EF migration-history rows so subsequent <c>ctx.Database.MigrateAsync()</c>
    /// calls in the test class become a no-op. Returns a connection string scoped to the
    /// new database. Pair with <see cref="DropDatabaseAsync"/> in <c>DisposeAsync</c> so
    /// re-runs of the same class start clean.
    /// </summary>
    public async Task<string> EnsureDatabaseAsync(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ValidateIdentifier(databaseName);

        // Drop-and-recreate so a stale schema from a prior run with different migrations
        // doesn't carry over. Cheap on a per-class basis (~50ms vs ~5s for re-running
        // migrations from scratch).
        await ExecuteServerSqlAsync($"DROP DATABASE IF EXISTS `{databaseName}`;");
        await ExecuteServerSqlAsync($"CREATE DATABASE `{databaseName}`;");

        // Replay each captured CREATE TABLE statement against the new database. SHOW
        // CREATE TABLE preserves FK constraints (unlike CREATE TABLE LIKE), so the cloned
        // schema behaves identically to a freshly-migrated one — including cascade-delete
        // behaviour that at least one repository test asserts on. FK checks stay off
        // during the loop so cross-table reference order doesn't matter.
        if (templateTables.Count > 0)
        {
            await using var connection = OpenRootConnection(databaseName);
            await using (var disableFk = connection.CreateCommand())
            {
                disableFk.CommandText = "SET FOREIGN_KEY_CHECKS = 0;";
                await disableFk.ExecuteNonQueryAsync();
            }

            foreach (var table in templateTables)
            {
                await using var createCmd = connection.CreateCommand();
                createCmd.CommandText = table.CreateStatement;
                await createCmd.ExecuteNonQueryAsync();
            }

            // Only copy ROWS for the migration-history table — that's the only table the
            // template has seeded. Other tables stay empty so per-class tests start with a
            // clean slate.
            await using (var historyCopy = connection.CreateCommand())
            {
                historyCopy.CommandText =
                    $"INSERT INTO `__EFMigrationsHistory` SELECT * FROM `{TemplateDatabaseName}`.`__EFMigrationsHistory`;";
                await historyCopy.ExecuteNonQueryAsync();
            }

            await using (var enableFk = connection.CreateCommand())
            {
                enableFk.CommandText = "SET FOREIGN_KEY_CHECKS = 1;";
                await enableFk.ExecuteNonQueryAsync();
            }
        }

        return BuildConnectionStringForDatabase(databaseName);
    }

    private MySqlConnection OpenRootConnection(string database)
    {
        var appBuilder = new MySqlConnectionStringBuilder(Container.GetConnectionString());
        var rootBuilder = new MySqlConnectionStringBuilder
        {
            Server = appBuilder.Server,
            Port = appBuilder.Port,
            UserID = "root",
            Password = RootPassword,
            Database = database,
        };
        var connection = new MySqlConnection(rootBuilder.ConnectionString);
        connection.Open();
        return connection;
    }

    /// <summary>
    /// Drops the named database. Tolerates a missing DB so DisposeAsync is safe even if
    /// the test class never reached the EnsureDatabase step (e.g. a fixture-init failure).
    /// </summary>
    public async Task DropDatabaseAsync(string databaseName)
    {
        if (string.IsNullOrWhiteSpace(databaseName))
        {
            return;
        }

        ValidateIdentifier(databaseName);
        await ExecuteServerSqlAsync($"DROP DATABASE IF EXISTS `{databaseName}`;");
    }

    private string BuildConnectionStringForDatabase(string databaseName)
    {
        // Return a ROOT connection string so the test class can run migrations + the
        // application's repositories against the per-class database without needing an
        // explicit GRANT. The testcontainers application user is per-DB and would otherwise
        // hit "Access denied" on every per-class schema.
        var appBuilder = new MySqlConnectionStringBuilder(Container.GetConnectionString());
        var builder = new MySqlConnectionStringBuilder
        {
            Server = appBuilder.Server,
            Port = appBuilder.Port,
            UserID = "root",
            Password = RootPassword,
            Database = databaseName,
        };
        return builder.ConnectionString;
    }

    private async Task ExecuteServerSqlAsync(string sql)
    {
        // The application user (`mariadb` by default in testcontainers) only has privileges
        // on its own database, so CREATE / DROP DATABASE / GRANT all need root. The
        // testcontainers MariaDb image sets MARIADB_ROOT_PASSWORD to a fixed value
        // ("test"); we build a root-scoped connection string off the application's host +
        // port and use that for schema management.
        var appBuilder = new MySqlConnectionStringBuilder(Container.GetConnectionString());
        var rootBuilder = new MySqlConnectionStringBuilder
        {
            Server = appBuilder.Server,
            Port = appBuilder.Port,
            UserID = "root",
            Password = RootPassword,
            Database = string.Empty,
        };

        await using var connection = new MySqlConnection(rootBuilder.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Defence in depth: the database name is interpolated into a CREATE/DROP statement
    /// (via backticks, which MariaDB treats as quote-of-identifier), so a name with
    /// backticks or NUL bytes would break out. Reject anything that isn't ASCII
    /// alphanumeric + underscore — every callsite uses Type.Name or a Guid string anyway.
    /// </summary>
    private static void ValidateIdentifier(string identifier)
    {
        foreach (var c in identifier)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                throw new ArgumentException(
                    $"Database name '{identifier}' contains an unsupported character; "
                    + "use [A-Za-z0-9_] only.",
                    nameof(identifier));
            }
        }
    }
}

/// <summary>
/// xunit collection definition that wires every Persistence test class tagged with
/// <c>[Collection(PersistenceMariaDbCollection.Name)]</c> to the shared
/// <see cref="SharedMariaDbFixture"/>. Replaces the per-class container-fixture pattern
/// with a single MariaDB instance for the whole assembly.
/// </summary>
[CollectionDefinition(Name)]
public sealed class PersistenceMariaDbCollection : ICollectionFixture<SharedMariaDbFixture>
{
    public const string Name = "PersistenceMariaDb";
}
