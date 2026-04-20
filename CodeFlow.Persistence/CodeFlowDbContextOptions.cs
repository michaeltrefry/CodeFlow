using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public static class CodeFlowDbContextOptions
{
    public static MariaDbServerVersion ServerVersion { get; } = new(new Version(11, 4, 0));

    public static DbContextOptionsBuilder Configure(
        DbContextOptionsBuilder builder,
        string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("A MariaDB connection string is required.", nameof(connectionString));
        }

        builder.UseMySql(connectionString, ServerVersion);
        return builder;
    }

    public static DbContextOptionsBuilder<CodeFlowDbContext> Configure(
        DbContextOptionsBuilder<CodeFlowDbContext> builder,
        string connectionString)
    {
        Configure((DbContextOptionsBuilder)builder, connectionString);
        return builder;
    }
}
