using CodeFlow.Runtime.Mcp;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace CodeFlow.Persistence;

public sealed class McpServerRepository(CodeFlowDbContext dbContext, ISecretProtector secretProtector) : IMcpServerRepository
{
    public async Task<IReadOnlyList<McpServer>> ListAsync(bool includeArchived, CancellationToken cancellationToken = default)
    {
        var query = dbContext.McpServers.AsNoTracking();

        if (!includeArchived)
        {
            query = query.Where(server => !server.IsArchived);
        }

        var entities = await query
            .OrderBy(server => server.Key)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    public async Task<McpServer?> GetAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.McpServers
            .AsNoTracking()
            .SingleOrDefaultAsync(server => server.Id == id, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<McpServer?> GetByKeyAsync(string key, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(key);

        var entity = await dbContext.McpServers
            .AsNoTracking()
            .SingleOrDefaultAsync(server => server.Key == normalized, cancellationToken);

        return entity is null ? null : Map(entity);
    }

    public async Task<long> CreateAsync(McpServerCreate create, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(create);

        var now = DateTime.UtcNow;
        var entity = new McpServerEntity
        {
            Key = NormalizeKey(create.Key),
            DisplayName = Require(create.DisplayName, nameof(create.DisplayName)),
            Transport = create.Transport,
            EndpointUrl = Require(create.EndpointUrl, nameof(create.EndpointUrl)),
            BearerTokenCipher = EncryptIfPresent(create.BearerTokenPlaintext),
            HealthStatus = McpServerHealthStatus.Unverified,
            CreatedAtUtc = now,
            CreatedBy = Trim(create.CreatedBy),
            UpdatedAtUtc = now,
            UpdatedBy = Trim(create.CreatedBy),
        };

        dbContext.McpServers.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return entity.Id;
    }

    public async Task UpdateAsync(long id, McpServerUpdate update, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);

        var entity = await dbContext.McpServers
            .SingleOrDefaultAsync(server => server.Id == id, cancellationToken)
            ?? throw new McpServerNotFoundException(id);

        entity.DisplayName = Require(update.DisplayName, nameof(update.DisplayName));
        entity.Transport = update.Transport;
        entity.EndpointUrl = Require(update.EndpointUrl, nameof(update.EndpointUrl));
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedBy = Trim(update.UpdatedBy);

        entity.BearerTokenCipher = update.BearerToken.Action switch
        {
            BearerTokenAction.Preserve => entity.BearerTokenCipher,
            BearerTokenAction.Clear => null,
            BearerTokenAction.Replace => EncryptIfPresent(update.BearerToken.NewPlaintext),
            _ => entity.BearerTokenCipher,
        };

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ArchiveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.McpServers
            .SingleOrDefaultAsync(server => server.Id == id, cancellationToken)
            ?? throw new McpServerNotFoundException(id);

        if (!entity.IsArchived)
        {
            entity.IsArchived = true;
            entity.UpdatedAtUtc = DateTime.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task UpdateHealthAsync(
        long id,
        McpServerHealthStatus status,
        DateTime? lastVerifiedAtUtc,
        string? lastError,
        CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.McpServers
            .SingleOrDefaultAsync(server => server.Id == id, cancellationToken)
            ?? throw new McpServerNotFoundException(id);

        entity.HealthStatus = status;
        entity.LastVerifiedAtUtc = lastVerifiedAtUtc;
        entity.LastVerificationError = Trim(lastError);
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task ReplaceToolsAsync(
        long id,
        IReadOnlyList<McpServerToolWrite> tools,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var serverExists = await dbContext.McpServers
            .AsNoTracking()
            .AnyAsync(s => s.Id == id, cancellationToken);
        if (!serverExists) throw new McpServerNotFoundException(id);

        var strategy = dbContext.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using var transaction = await dbContext.Database.BeginTransactionAsync(
                IsolationLevel.Serializable,
                cancellationToken);

            var existing = await dbContext.McpServerTools
                .Where(tool => tool.ServerId == id)
                .ToListAsync(cancellationToken);

            dbContext.McpServerTools.RemoveRange(existing);

            var now = DateTime.UtcNow;
            foreach (var tool in tools)
            {
                dbContext.McpServerTools.Add(new McpServerToolEntity
                {
                    ServerId = id,
                    ToolName = Require(tool.ToolName, nameof(tool.ToolName)),
                    Description = tool.Description,
                    ParametersJson = tool.ParametersJson,
                    IsMutating = tool.IsMutating,
                    SyncedAtUtc = now,
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });
    }

    public async Task<IReadOnlyList<McpServerTool>> GetToolsAsync(long id, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.McpServerTools
            .AsNoTracking()
            .Where(tool => tool.ServerId == id)
            .OrderBy(tool => tool.ToolName)
            .ToListAsync(cancellationToken);

        return entities.Select(MapTool).ToArray();
    }

    public async Task<McpServerConnectionInfo?> GetConnectionInfoAsync(string serverKey, CancellationToken cancellationToken = default)
    {
        var normalized = NormalizeKey(serverKey);

        var entity = await dbContext.McpServers
            .AsNoTracking()
            .SingleOrDefaultAsync(server => server.Key == normalized && !server.IsArchived, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var token = entity.BearerTokenCipher is null ? null : secretProtector.Unprotect(entity.BearerTokenCipher);

        return new McpServerConnectionInfo(
            Key: entity.Key,
            Endpoint: new Uri(entity.EndpointUrl),
            Transport: entity.Transport,
            BearerToken: token);
    }

    private byte[]? EncryptIfPresent(string? plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return null;
        }

        return secretProtector.Protect(plaintext);
    }

    private static McpServer Map(McpServerEntity entity) => new(
        Id: entity.Id,
        Key: entity.Key,
        DisplayName: entity.DisplayName,
        Transport: entity.Transport,
        EndpointUrl: entity.EndpointUrl,
        HasBearerToken: entity.BearerTokenCipher is { Length: > 0 },
        HealthStatus: entity.HealthStatus,
        LastVerifiedAtUtc: entity.LastVerifiedAtUtc is DateTime lv ? DateTime.SpecifyKind(lv, DateTimeKind.Utc) : null,
        LastVerificationError: entity.LastVerificationError,
        CreatedAtUtc: DateTime.SpecifyKind(entity.CreatedAtUtc, DateTimeKind.Utc),
        CreatedBy: entity.CreatedBy,
        UpdatedAtUtc: DateTime.SpecifyKind(entity.UpdatedAtUtc, DateTimeKind.Utc),
        UpdatedBy: entity.UpdatedBy,
        IsArchived: entity.IsArchived);

    private static McpServerTool MapTool(McpServerToolEntity entity) => new(
        Id: entity.Id,
        ServerId: entity.ServerId,
        ToolName: entity.ToolName,
        Description: entity.Description,
        ParametersJson: entity.ParametersJson,
        IsMutating: entity.IsMutating,
        SyncedAtUtc: DateTime.SpecifyKind(entity.SyncedAtUtc, DateTimeKind.Utc));

    private static string NormalizeKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return key.Trim();
    }

    private static string Require(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        return value.Trim();
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
