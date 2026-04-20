using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodeFlow.Host.DeadLetter;

public sealed class RabbitMqDeadLetterStore : IDeadLetterStore
{
    private readonly HttpClient httpClient;
    private readonly DeadLetterOptions options;
    private readonly ILogger<RabbitMqDeadLetterStore> logger;

    public RabbitMqDeadLetterStore(
        HttpClient httpClient,
        IOptions<DeadLetterOptions> options,
        ILogger<RabbitMqDeadLetterStore> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.logger = logger;
        this.httpClient = httpClient;

        ConfigureClient(httpClient, this.options);
    }

    public async Task<IReadOnlyList<DeadLetterQueueSummary>> ListQueuesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"api/queues/{Uri.EscapeDataString(options.VirtualHost)}?columns=name,messages",
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("RabbitMQ management returned an empty body for queues list.");

        var summaries = new List<DeadLetterQueueSummary>();
        foreach (var queueNode in payload)
        {
            if (queueNode is null)
            {
                continue;
            }

            var name = queueNode["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(options.ErrorQueueSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            var messages = queueNode["messages"]?.GetValue<int?>() ?? 0;
            summaries.Add(new DeadLetterQueueSummary(name, messages));
        }

        return summaries;
    }

    public async Task<IReadOnlyList<DeadLetterMessage>> ListMessagesAsync(CancellationToken cancellationToken = default)
    {
        var queues = await ListQueuesAsync(cancellationToken);
        var all = new List<DeadLetterMessage>();

        foreach (var queue in queues)
        {
            if (queue.MessageCount == 0)
            {
                continue;
            }

            var messages = await PeekQueueAsync(queue.QueueName, cancellationToken);
            all.AddRange(messages);
        }

        return all;
    }

    public async Task<IReadOnlyList<DeadLetterMessage>> PeekQueueAsync(string queueName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);

        var request = new JsonObject
        {
            ["count"] = options.MaxPeekPerQueue,
            ["ackmode"] = "ack_requeue_true",
            ["encoding"] = "auto",
            ["truncate"] = 4096
        };

        var path = $"api/queues/{Uri.EscapeDataString(options.VirtualHost)}/{Uri.EscapeDataString(queueName)}/get";
        using var response = await httpClient.PostAsJsonAsync(path, request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("DLQ peek for {Queue} failed: {StatusCode}", queueName, response.StatusCode);
            return Array.Empty<DeadLetterMessage>();
        }

        var payload = await response.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken)
            ?? new JsonArray();

        var messages = new List<DeadLetterMessage>();
        foreach (var messageNode in payload)
        {
            if (messageNode is null)
            {
                continue;
            }

            messages.Add(MapMessage(queueName, messageNode));
        }

        return messages;
    }

    public async Task<DeadLetterRetryResult> RetryAsync(
        string queueName,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueName);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var consumedCount = 0;
        var maxAttempts = Math.Max(1, options.MaxPeekPerQueue);
        while (consumedCount < maxAttempts)
        {
            consumedCount++;
            var getRequest = new JsonObject
            {
                ["count"] = 1,
                ["ackmode"] = "ack_requeue_false",
                ["encoding"] = "auto",
                ["truncate"] = 65536
            };

            var getPath = $"api/queues/{Uri.EscapeDataString(options.VirtualHost)}/{Uri.EscapeDataString(queueName)}/get";
            using var getResponse = await httpClient.PostAsJsonAsync(getPath, getRequest, cancellationToken);

            if (!getResponse.IsSuccessStatusCode)
            {
                return new DeadLetterRetryResult(false, null, $"Failed to fetch message: {getResponse.StatusCode}");
            }

            var body = await getResponse.Content.ReadFromJsonAsync<JsonArray>(cancellationToken: cancellationToken)
                ?? new JsonArray();

            if (body.Count == 0)
            {
                return new DeadLetterRetryResult(false, null, "Message not found; it may have already been retried or removed.");
            }

            var candidate = body[0] ?? throw new InvalidOperationException("Null RabbitMQ message entry.");
            var mapped = MapMessage(queueName, candidate);

            if (!string.Equals(mapped.MessageId, messageId, StringComparison.Ordinal))
            {
                // Not our target — we already consumed (ack_requeue_false). Republish it back unchanged.
                await PublishOriginalAsync(candidate, queueName, cancellationToken);
                continue;
            }

            var republishTarget = ResolveRepublishTarget(mapped, queueName);
            await PublishToQueueAsync(republishTarget, candidate, cancellationToken);
            return new DeadLetterRetryResult(true, republishTarget, null);
        }

        return new DeadLetterRetryResult(false, null, "Target message id was not found within the peek window.");
    }

    private async Task PublishOriginalAsync(JsonNode candidate, string queueName, CancellationToken cancellationToken)
    {
        // Put the rotated-past message back onto the same error queue.
        await PublishToQueueAsync(queueName, candidate, cancellationToken);
    }

    private async Task PublishToQueueAsync(string targetQueue, JsonNode candidate, CancellationToken cancellationToken)
    {
        var payload = candidate["payload"]?.GetValue<string>() ?? string.Empty;
        var payloadEncoding = candidate["payload_encoding"]?.GetValue<string>() ?? "string";
        var properties = candidate["properties"]?.DeepClone() ?? new JsonObject();

        var publishRequest = new JsonObject
        {
            ["properties"] = properties,
            ["routing_key"] = targetQueue,
            ["payload"] = payload,
            ["payload_encoding"] = payloadEncoding
        };

        // Publish to the default exchange ("") with the target queue as routing key — this is a direct-to-queue send.
        var path = $"api/exchanges/{Uri.EscapeDataString(options.VirtualHost)}/{Uri.EscapeDataString("amq.default")}/publish";
        using var response = await httpClient.PostAsJsonAsync(path, publishRequest, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private DeadLetterMessage MapMessage(string queueName, JsonNode messageNode)
    {
        var payload = messageNode["payload"]?.GetValue<string>() ?? string.Empty;
        var messageId = ComputeStableMessageId(queueName, payload);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (messageNode["properties"]?["headers"] is JsonObject headerObject)
        {
            foreach (var kvp in headerObject)
            {
                if (kvp.Value is null)
                {
                    continue;
                }

                headers[kvp.Key] = kvp.Value.ToJsonString().Trim('"');
            }
        }

        string? originalInputAddress = null;
        headers.TryGetValue("MT-Fault-InputAddress", out originalInputAddress);

        string? faultException = null;
        headers.TryGetValue("MT-Fault-Message", out faultException);

        string? faultExceptionType = null;
        headers.TryGetValue("MT-Fault-ExceptionType", out faultExceptionType);

        DateTimeOffset? faultedAtUtc = null;
        if (headers.TryGetValue("MT-Fault-Timestamp", out var timestamp)
            && DateTimeOffset.TryParse(timestamp, out var parsed))
        {
            faultedAtUtc = parsed;
        }

        var preview = payload.Length > 2048 ? payload[..2048] : payload;
        return new DeadLetterMessage(
            MessageId: messageId,
            QueueName: queueName,
            OriginalInputAddress: originalInputAddress,
            FaultExceptionMessage: faultException,
            FaultExceptionType: faultExceptionType,
            FirstFaultedAtUtc: faultedAtUtc,
            PayloadPreview: preview,
            Headers: headers);
    }

    private static string ComputeStableMessageId(string queueName, string payload)
    {
        var bytes = Encoding.UTF8.GetBytes(queueName + "|" + payload);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash)[..24];
    }

    private string ResolveRepublishTarget(DeadLetterMessage message, string queueName)
    {
        if (!string.IsNullOrWhiteSpace(message.OriginalInputAddress)
            && Uri.TryCreate(message.OriginalInputAddress, UriKind.Absolute, out var inputUri))
        {
            return inputUri.Segments.Last().Trim('/');
        }

        if (queueName.EndsWith(options.ErrorQueueSuffix, StringComparison.Ordinal))
        {
            return queueName[..^options.ErrorQueueSuffix.Length];
        }

        return queueName;
    }

    private static void ConfigureClient(HttpClient client, DeadLetterOptions options)
    {
        var scheme = options.UseHttps ? "https" : "http";
        client.BaseAddress = new Uri($"{scheme}://{options.ManagementHost}:{options.ManagementPort}/");

        var credentials = Encoding.UTF8.GetBytes($"{options.Username}:{options.Password}");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(credentials));
    }
}
