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

    public MariaDbContainer Container { get; } = new MariaDbBuilder("mariadb:11.4")
        .WithEnvironment("MARIADB_ROOT_PASSWORD", RootPassword)
        .Build();

    public async Task InitializeAsync()
    {
        await Container.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await Container.DisposeAsync();
    }

    /// <summary>
    /// Creates the named database on the shared server (idempotent — IF NOT EXISTS) and
    /// returns a connection string scoped to it. Callers run their migrations against the
    /// returned string. Pair with <see cref="DropDatabaseAsync"/> in <c>DisposeAsync</c>
    /// so re-runs of the same class don't carry over state from the prior run.
    /// </summary>
    public async Task<string> EnsureDatabaseAsync(string databaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseName);
        ValidateIdentifier(databaseName);

        await ExecuteServerSqlAsync($"CREATE DATABASE IF NOT EXISTS `{databaseName}`;");
        return BuildConnectionStringForDatabase(databaseName);
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
