using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace CodeFlow.Persistence;

public sealed class TokenUsageRecordRepository(CodeFlowDbContext dbContext) : ITokenUsageRecordRepository
{
    private static readonly JsonSerializerOptions ScopeChainSerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task AddAsync(TokenUsageRecord record, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        var entity = new TokenUsageRecordEntity
        {
            Id = record.Id == Guid.Empty ? Guid.NewGuid() : record.Id,
            TraceId = record.TraceId,
            NodeId = record.NodeId,
            InvocationId = record.InvocationId,
            ScopeChainJson = SerializeScopeChain(record.ScopeChain),
            Provider = record.Provider,
            Model = record.Model,
            RecordedAtUtc = record.RecordedAtUtc,
            UsageJson = record.Usage.GetRawText()
        };

        dbContext.TokenUsageRecords.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TokenUsageRecord>> ListByTraceAsync(Guid traceId, CancellationToken cancellationToken = default)
    {
        var entities = await dbContext.TokenUsageRecords
            .AsNoTracking()
            .Where(r => r.TraceId == traceId)
            .OrderBy(r => r.RecordedAtUtc)
            .ThenBy(r => r.Id)
            .ToListAsync(cancellationToken);

        return entities.Select(Map).ToArray();
    }

    private static TokenUsageRecord Map(TokenUsageRecordEntity entity)
    {
        var scopeChain = DeserializeScopeChain(entity.ScopeChainJson);
        var usageDocument = JsonDocument.Parse(string.IsNullOrWhiteSpace(entity.UsageJson) ? "{}" : entity.UsageJson);

        return new TokenUsageRecord(
            Id: entity.Id,
            TraceId: entity.TraceId,
            NodeId: entity.NodeId,
            InvocationId: entity.InvocationId,
            ScopeChain: scopeChain,
            Provider: entity.Provider,
            Model: entity.Model,
            RecordedAtUtc: DateTime.SpecifyKind(entity.RecordedAtUtc, DateTimeKind.Utc),
            Usage: usageDocument.RootElement.Clone());
    }

    private static string SerializeScopeChain(IReadOnlyList<Guid> scopeChain)
    {
        if (scopeChain is null || scopeChain.Count == 0)
        {
            return "[]";
        }

        return JsonSerializer.Serialize(scopeChain, ScopeChainSerializerOptions);
    }

    private static IReadOnlyList<Guid> DeserializeScopeChain(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<Guid>();
        }

        return JsonSerializer.Deserialize<Guid[]>(json, ScopeChainSerializerOptions) ?? Array.Empty<Guid>();
    }
}
