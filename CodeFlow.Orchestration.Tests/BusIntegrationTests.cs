using CodeFlow.Contracts;
using CodeFlow.Host;
using CodeFlow.Persistence;
using CodeFlow.Runtime;
using FluentAssertions;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Text;
using Testcontainers.MariaDb;
using Testcontainers.RabbitMq;

namespace CodeFlow.Orchestration.Tests;

[Collection("Bus integration")]
public sealed class BusIntegrationTests : IAsyncLifetime
{
    private readonly MariaDbContainer mariaDbContainer = new MariaDbBuilder("mariadb:11.4")
        .WithDatabase("codeflow_bus_tests")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly RabbitMqContainer rabbitMqContainer = new RabbitMqBuilder("rabbitmq:4.0-management")
        .WithUsername("codeflow")
        .WithPassword("codeflow_dev")
        .Build();

    private readonly string artifactRoot = Path.Combine(
        Path.GetTempPath(),
        "codeflow-bus-tests",
        Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(artifactRoot);
        await mariaDbContainer.StartAsync();
        await rabbitMqContainer.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (Directory.Exists(artifactRoot))
        {
            Directory.Delete(artifactRoot, recursive: true);
        }

        await rabbitMqContainer.DisposeAsync();
        await mariaDbContainer.DisposeAsync();
    }

    [Fact]
    public async Task HostPipeline_ShouldConsumePersistPublishAndSuppressDuplicateMessageReplay()
    {
        var completionMessages = new ConcurrentQueue<AgentInvocationCompleted>();
        var completionSignal = new TaskCompletionSource<AgentInvocationCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var faultSignal = new TaskCompletionSource<Fault<AgentInvokeRequested>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fakeAgentInvoker = new FakeEchoAgentInvoker();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable] = mariaDbContainer.GetConnectionString(),
                ["RabbitMq:Host"] = rabbitMqContainer.Hostname,
                ["RabbitMq:Port"] = rabbitMqContainer.GetMappedPublicPort(5672).ToString(),
                ["RabbitMq:VirtualHost"] = "/",
                ["RabbitMq:Username"] = "codeflow",
                ["RabbitMq:Password"] = "codeflow_dev",
                ["Artifacts:RootDirectory"] = artifactRoot,
                ["Secrets:MasterKey"] = TestSecrets.DeterministicMasterKeyBase64
            })
            .Build();

        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddCodeFlowHost(builder.Configuration);
        builder.Services.AddSingleton<IAgentInvoker>(fakeAgentInvoker);

        using var host = builder.Build();
        await host.ApplyDatabaseMigrationsAsync();
        await SeedAgentConfigurationAsync(host.Services);
        await host.StartAsync();

        HostReceiveEndpointHandle? completionEndpointHandle = null;

        try
        {
            var connector = host.Services.GetRequiredService<IReceiveEndpointConnector>();
            completionEndpointHandle = connector.ConnectReceiveEndpoint(
                $"test-completions-{Guid.NewGuid():N}",
                (_, cfg) =>
                {
                    cfg.Handler<AgentInvocationCompleted>(context =>
                    {
                        completionMessages.Enqueue(context.Message);
                        completionSignal.TrySetResult(context.Message);
                        return Task.CompletedTask;
                    });
                    cfg.Handler<Fault<AgentInvokeRequested>>(context =>
                    {
                        faultSignal.TrySetResult(context.Message);
                        return Task.CompletedTask;
                    });
                });
            await completionEndpointHandle.Ready;

            var inputRef = await WriteInputArtifactAsync(host.Services, "integration-test-input");
            var messageId = Guid.NewGuid();
            var startNodeId = await GetStartNodeIdAsync(host.Services, "article-flow", 1);
            var request = new AgentInvokeRequested(
                TraceId: Guid.NewGuid(),
                RoundId: Guid.NewGuid(),
                WorkflowKey: "article-flow",
                WorkflowVersion: 1,
                NodeId: startNodeId,
                AgentKey: "echo-agent",
                AgentVersion: 1,
                InputRef: inputRef,
                ContextInputs: new Dictionary<string, System.Text.Json.JsonElement>(),
                CorrelationHeaders: new Dictionary<string, string> { ["x-test"] = "phase-3" });

            var bus = host.Services.GetRequiredService<IBus>();
            await bus.Publish(request, publishContext => publishContext.MessageId = messageId);

            var completedTask = await Task.WhenAny(
                completionSignal.Task,
                faultSignal.Task,
                Task.Delay(TimeSpan.FromSeconds(30)));

            if (completedTask == faultSignal.Task)
            {
                var fault = await faultSignal.Task;
                throw new Xunit.Sdk.XunitException(
                    $"Agent invocation faulted: {string.Join(" | ", fault.Exceptions.Select(exception => exception.Message))}");
            }

            if (completedTask != completionSignal.Task)
            {
                throw new TimeoutException(
                    $"Timed out waiting for completion. InvocationCount={fakeAgentInvoker.InvocationCount}");
            }

            var completion = await completionSignal.Task;

            completion.TraceId.Should().Be(request.TraceId);
            completion.RoundId.Should().Be(request.RoundId);
            completion.AgentKey.Should().Be("echo-agent");
            completion.AgentVersion.Should().Be(1);
            completion.Decision.Should().Be(CodeFlow.Contracts.AgentDecisionKind.Completed);
            completion.TokenUsage.Should().BeEquivalentTo(new CodeFlow.Contracts.TokenUsage(5, 7, 12));
            fakeAgentInvoker.InvocationCount.Should().Be(1);

            var outputText = await ReadArtifactTextAsync(host.Services, completion.OutputRef);
            outputText.Should().Be("integration-test-input");

            (await TableExistsAsync(host.Services, "InboxState")).Should().BeTrue();
            (await TableExistsAsync(host.Services, "OutboxMessage")).Should().BeTrue();
            (await TableExistsAsync(host.Services, "OutboxState")).Should().BeTrue();
            (await CountRowsAsync(host.Services, "InboxState")).Should().BeGreaterThan(0);

            await bus.Publish(request, publishContext => publishContext.MessageId = messageId);
            await Task.Delay(TimeSpan.FromSeconds(3));

            fakeAgentInvoker.InvocationCount.Should().Be(1);
            completionMessages.Should().HaveCount(1);
        }
        finally
        {
            if (completionEndpointHandle is not null)
            {
                await completionEndpointHandle.StopAsync(CancellationToken.None);
            }

            await host.StopAsync();
        }
    }

    private async Task SeedAgentConfigurationAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAgentConfigRepository>();
        var configurationJson =
            """
            {
              "provider": "lmstudio",
              "model": "local-echo",
              "enableHostTools": false
            }
            """;

        var version = await repository.CreateNewVersionAsync("echo-agent", configurationJson, "codex");
        version.Should().Be(1);
    }

    private static async Task SeedWorkflowAsync(IServiceProvider services)
    {
        // Seeded so that the workflow saga registered in AddCodeFlowHost can cleanly terminate
        // the single-hop trace produced by this test without tripping on a missing workflow.
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        dbContext.Workflows.Add(new WorkflowEntity
        {
            Key = "article-flow",
            Version = 1,
            Name = "bus-integration-test",
            MaxRoundsPerRound = 5,
            CreatedAtUtc = DateTime.UtcNow,
            Nodes =
            [
                new WorkflowNodeEntity
                {
                    NodeId = Guid.NewGuid(),
                    Kind = WorkflowNodeKind.Start,
                    AgentKey = "echo-agent",
                    AgentVersion = 1,
                    OutputPortsJson = """["Completed","Approved","ApprovedWithActions","Rejected","Failed"]""",
                    LayoutX = 0,
                    LayoutY = 0
                }
            ]
        });
        await dbContext.SaveChangesAsync();
    }

    private static async Task<Guid> GetStartNodeIdAsync(IServiceProvider services, string workflowKey, int version)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var workflow = await dbContext.Workflows
            .Include(w => w.Nodes)
            .AsNoTracking()
            .SingleAsync(w => w.Key == workflowKey && w.Version == version);

        return workflow.Nodes.Single(n => n.Kind == WorkflowNodeKind.Start).NodeId;
    }

    private async Task<Uri> WriteInputArtifactAsync(IServiceProvider services, string content)
    {
        await using var scope = services.CreateAsyncScope();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(content));

        return await artifactStore.WriteAsync(
            stream,
            new ArtifactMetadata(
                TraceId: Guid.NewGuid(),
                RoundId: Guid.NewGuid(),
                ArtifactId: Guid.NewGuid(),
                ContentType: "text/plain",
                FileName: "input.txt"));
    }

    private static async Task<string> ReadArtifactTextAsync(IServiceProvider services, Uri uri)
    {
        await using var scope = services.CreateAsyncScope();
        var artifactStore = scope.ServiceProvider.GetRequiredService<IArtifactStore>();
        await using var stream = await artifactStore.ReadAsync(uri);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<bool> TableExistsAsync(IServiceProvider services, string tableName)
    {
        const string sql =
            """
            SELECT COUNT(*)
            FROM information_schema.tables
            WHERE table_schema = DATABASE()
              AND table_name = @tableName;
            """;

        return await ExecuteScalarAsync(services, sql, tableName) > 0;
    }

    private static async Task<long> CountRowsAsync(IServiceProvider services, string tableName)
    {
        var sql = $"SELECT COUNT(*) FROM `{tableName}`;";
        return await ExecuteScalarAsync(services, sql, tableName: null);
    }

    private static async Task<long> ExecuteScalarAsync(
        IServiceProvider services,
        string sql,
        string? tableName)
    {
        await using var scope = services.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<CodeFlowDbContext>();
        var connection = dbContext.Database.GetDbConnection();

        await EnsureOpenAsync(connection);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (tableName is not null)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = "@tableName";
            parameter.Value = tableName;
            command.Parameters.Add(parameter);
        }

        var result = await command.ExecuteScalarAsync();
        return Convert.ToInt64(result);
    }

    private static async Task EnsureOpenAsync(DbConnection connection)
    {
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }
    }

    private sealed class FakeEchoAgentInvoker : IAgentInvoker
    {
        private int invocationCount;

        public int InvocationCount => invocationCount;

        public Task<AgentInvocationResult> InvokeAsync(
            AgentInvocationConfiguration configuration,
            string? input,
            ResolvedAgentTools tools,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref invocationCount);

            return Task.FromResult(new AgentInvocationResult(
                Output: input ?? string.Empty,
                Decision: new CompletedDecision(),
                Transcript: [],
                TokenUsage: new Runtime.TokenUsage(5, 7, 12),
                ToolCallsExecuted: 0));
        }
    }
}
