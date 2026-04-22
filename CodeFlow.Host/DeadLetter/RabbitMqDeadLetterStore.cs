using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace CodeFlow.Host.DeadLetter;

public sealed class RabbitMqDeadLetterStore : IDeadLetterStore
{
    private readonly HttpClient httpClient;
    private readonly DeadLetterOptions options;
    private readonly IDlqRetryTransport retryTransport;
    private readonly ILogger<RabbitMqDeadLetterStore> logger;

    public RabbitMqDeadLetterStore(
        HttpClient httpClient,
        IOptions<DeadLetterOptions> options,
        IDlqRetryTransport retryTransport,
        ILogger<RabbitMqDeadLetterStore> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(retryTransport);
        ArgumentNullException.ThrowIfNull(logger);

        this.options = options.Value;
        this.retryTransport = retryTransport;
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

        // Resolve the republish target by peeking once (non-destructive; ack_requeue_true above).
        // We need the original-input-address header from the target message to know where to
        // republish it — the dead-letter queue name alone would only round-trip it to itself.
        var snapshot = await PeekQueueAsync(queueName, cancellationToken);
        var targetMessage = snapshot.FirstOrDefault(m => string.Equals(m.MessageId, messageId, StringComparison.Ordinal));

        if (targetMessage is null)
        {
            return new DeadLetterRetryResult(
                false,
                null,
                "Message not found in the first peek window; it may have already been retried, drained, or exceeded the peek limit.");
        }

        var republishTarget = ResolveRepublishTarget(targetMessage, queueName);

        try
        {
            var transfer = await retryTransport.TransferAsync(
                sourceQueue: queueName,
                targetQueue: republishTarget,
                targetMessageId: messageId,
                cancellationToken);

            return transfer.Outcome switch
            {
                DlqTransferOutcome.Transferred => new DeadLetterRetryResult(true, republishTarget, null),
                DlqTransferOutcome.NotFound => new DeadLetterRetryResult(
                    false,
                    null,
                    transfer.ErrorMessage ?? "Target message id was not found during the AMQP retry scan."),
                _ => new DeadLetterRetryResult(false, null, transfer.ErrorMessage ?? "Unknown AMQP retry failure.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "DLQ atomic retry failed for {Queue}/{MessageId} -> {Target}; source queue is unchanged",
                queueName,
                messageId,
                republishTarget);
            return new DeadLetterRetryResult(false, null, $"Atomic retry failed: {ex.Message}");
        }
    }

    private DeadLetterMessage MapMessage(string queueName, JsonNode messageNode)
    {
        var payload = messageNode["payload"]?.GetValue<string>() ?? string.Empty;
        var properties = messageNode["properties"] as JsonObject;
        var explicitMessageId = properties?["message_id"]?.GetValue<string?>();

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (properties?["headers"] is JsonObject headerObject)
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

        headers.TryGetValue("MT-Fault-InputAddress", out var originalInputAddress);
        headers.TryGetValue("MT-Fault-Message", out var faultException);
        headers.TryGetValue("MT-Fault-ExceptionType", out var faultExceptionType);

        DateTimeOffset? faultedAtUtc = null;
        if (headers.TryGetValue("MT-Fault-Timestamp", out var timestamp)
            && DateTimeOffset.TryParse(timestamp, out var parsed))
        {
            faultedAtUtc = parsed;
        }

        // Prefer the AMQP message_id property (MassTransit sets this per publish — unique per
        // message). Fall back to a composite hash including any per-fault identifiers so two
        // semantically-identical failures in the same queue don't collide.
        var messageId = !string.IsNullOrWhiteSpace(explicitMessageId)
            ? explicitMessageId!
            : ComputeFallbackMessageId(queueName, payload, headers);

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

    private static string ComputeFallbackMessageId(
        string queueName,
        string payload,
        IReadOnlyDictionary<string, string> headers)
    {
        var builder = new StringBuilder();
        builder.Append(queueName).Append('|').Append(payload);

        // Any one of these makes two otherwise-identical faults distinguishable. MT stamps the
        // fault timestamp per fault event and the activity/conversation ids per invocation, so
        // at least one of these is present in practice.
        AppendIfPresent(builder, headers, "MT-Fault-Timestamp");
        AppendIfPresent(builder, headers, "MT-Fault-ConsumerType");
        AppendIfPresent(builder, headers, "MT-Activity-Id");
        AppendIfPresent(builder, headers, "MT-ConversationId");

        var bytes = Encoding.UTF8.GetBytes(builder.ToString());
        var hash = SHA256.HashData(bytes);
        return "h-" + Convert.ToHexString(hash)[..24];
    }

    private static void AppendIfPresent(
        StringBuilder builder,
        IReadOnlyDictionary<string, string> headers,
        string headerName)
    {
        if (headers.TryGetValue(headerName, out var value))
        {
            builder.Append('|').Append(headerName).Append('=').Append(value);
        }
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
