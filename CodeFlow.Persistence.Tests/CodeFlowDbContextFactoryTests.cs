using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence.Tests;

public sealed class CodeFlowDbContextFactoryTests
{
    [Fact]
    public void CreateDbContext_ShouldUseMariaDbProvider_WithLocalDefaults()
    {
        Environment.SetEnvironmentVariable(
            CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable,
            null);

        var factory = new CodeFlowDbContextFactory();

        using var dbContext = factory.CreateDbContext([]);

        dbContext.Database.ProviderName.Should().Be("Pomelo.EntityFrameworkCore.MySql");
    }

    [Fact]
    public void CreateDbContext_ShouldRespectConnectionStringEnvironmentVariable()
    {
        const string customConnectionString =
            "Server=127.0.0.1;Port=3307;Database=codeflow_test;User=custom;Password=secret;";

        Environment.SetEnvironmentVariable(
            CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable,
            customConnectionString);

        var factory = new CodeFlowDbContextFactory();

        using var dbContext = factory.CreateDbContext([]);
        var resolvedConnectionString = dbContext.Database.GetConnectionString();

        resolvedConnectionString.Should().Contain("Port=3307");
        resolvedConnectionString.Should().Contain("Database=codeflow_test");
        resolvedConnectionString.Should().Contain("User ID=custom");
        resolvedConnectionString.Should().Contain("Password=secret");

        Environment.SetEnvironmentVariable(
            CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable,
            null);
    }
}
