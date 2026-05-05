using CodeFlow.Persistence;
using Microsoft.EntityFrameworkCore;
using Testcontainers.MariaDb;

namespace CodeFlow.Api.Tests.WorkflowTemplates;

/// <summary>
/// xunit class-scoped fixture that owns ONE MariaDB Testcontainer for the entire
/// <see cref="WorkflowTemplateMaterializerTests"/> class (sc-699 Phase B Api). Replaces the
/// previous per-test <c>IAsyncLifetime</c> on the test class itself, which was creating a
/// fresh container for every test method (~5s × 28 tests = ~140s of pure container churn
/// for this one file). Tests use Guid-prefixed keys so they don't collide on the shared DB
/// and the materializer doesn't read state across tests, so per-test isolation isn't
/// required.
///
/// Migrations run once at fixture initialization. Schema state is reused across all 28
/// tests in the class.
/// </summary>
public sealed class WorkflowTemplateMariaDbFixture : IAsyncLifetime
{
    private readonly MariaDbContainer container = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_template_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ConnectionString = container.GetConnectionString();

        var optionsBuilder = new DbContextOptionsBuilder<CodeFlowDbContext>();
        CodeFlowDbContextOptions.Configure(optionsBuilder, ConnectionString);
        await using var ctx = new CodeFlowDbContext(optionsBuilder.Options);
        await ctx.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await container.DisposeAsync();
    }
}
