using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace CodeFlow.Persistence;

public sealed class CodeFlowDbContextFactory : IDesignTimeDbContextFactory<CodeFlowDbContext>
{
    public CodeFlowDbContext CreateDbContext(string[] args)
    {
        var connectionString = ResolveConnectionString();
        var builder = new DbContextOptionsBuilder<CodeFlowDbContext>();

        CodeFlowDbContextOptions.Configure(builder, connectionString);

        return new CodeFlowDbContext(builder.Options);
    }

    private static string ResolveConnectionString()
    {
        var configuredConnectionString = Environment.GetEnvironmentVariable(
            CodeFlowPersistenceDefaults.ConnectionStringEnvironmentVariable);

        return string.IsNullOrWhiteSpace(configuredConnectionString)
            ? CodeFlowPersistenceDefaults.LocalDevelopmentConnectionString
            : configuredConnectionString;
    }
}
