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

        // MassTransit already applies its own receive retries around the EF inbox/outbox flow.
        // Enabling EF's execution-strategy retries here can replay the same DbContext after a
        // transient MariaDB deadlock, which leaves duplicate InboxState entries tracked and
        // wedges message consumption. Keep retries explicit at the call site instead.
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
