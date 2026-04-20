using FluentAssertions;
using MySqlConnector;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Persistence.Tests;

public sealed class WorkflowSagaSchemaTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_saga_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private string? connectionString;

    public async Task InitializeAsync()
    {
        await mariaDbContainer.StartAsync();
        connectionString = mariaDbContainer.GetConnectionString();

        await using var dbContext = CreateDbContext();
        await dbContext.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task MigrateAsync_ShouldCreateWorkflowSagasTableWithExpectedColumns()
    {
        await using var connection = new MySqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = DATABASE()
              AND table_name = 'workflow_sagas'
            ORDER BY ordinal_position;
            """;

        await using var reader = await command.ExecuteReaderAsync();

        var columns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        while (await reader.ReadAsync())
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        columns.Should().ContainKey("correlation_id");
        columns.Should().ContainKey("trace_id");
        columns.Should().ContainKey("current_state");
        columns.Should().ContainKey("current_agent_key");
        columns.Should().ContainKey("current_round_id");
        columns.Should().ContainKey("round_count");
        columns.Should().ContainKey("agent_versions_json");
        columns.Should().ContainKey("decision_history_json");
        columns.Should().ContainKey("workflow_key");
        columns.Should().ContainKey("workflow_version");
        columns.Should().ContainKey("created_at");
        columns.Should().ContainKey("updated_at");
        columns.Should().ContainKey("version");

        columns["agent_versions_json"].Should().Be("longtext");
        columns["decision_history_json"].Should().Be("longtext");
        columns["version"].Should().Be("int");
    }

    private CodeFlowDbContext CreateDbContext()
    {
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(builder, connectionString!);
        return new CodeFlowDbContext(builder.Options);
    }
}
