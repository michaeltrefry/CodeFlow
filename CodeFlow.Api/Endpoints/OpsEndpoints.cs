using CodeFlow.Api.Dtos;
using CodeFlow.Host.DeadLetter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using System.Text;

namespace CodeFlow.Api.Endpoints;

public static class OpsEndpoints
{
    public static IEndpointRouteBuilder MapOpsEndpoints(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes.MapGroup("/api/ops");

        group.MapGet("/dlq", async (IDeadLetterStore store, CancellationToken cancellationToken) =>
        {
            var queues = await store.ListQueuesAsync(cancellationToken);
            var messages = await store.ListMessagesAsync(cancellationToken);

            var response = new DeadLetterListResponse(
                Queues: queues
                    .Select(q => new DeadLetterQueueDto(q.QueueName, q.MessageCount))
                    .ToArray(),
                Messages: messages
                    .Select(m => new DeadLetterMessageDto(
                        m.MessageId,
                        m.QueueName,
                        m.OriginalInputAddress,
                        m.FaultExceptionMessage,
                        m.FaultExceptionType,
                        m.FirstFaultedAtUtc,
                        m.PayloadPreview))
                    .ToArray());

            return Results.Ok(response);
        })
        .RequireAuthorization(CodeFlowApiDefaults.Policies.OpsRead);

        group.MapPost("/dlq/{queueName}/retry/{messageId}", async (
            string queueName,
            string messageId,
            IDeadLetterStore store,
            CancellationToken cancellationToken) =>
        {
            var result = await store.RetryAsync(queueName, messageId, cancellationToken);
            var dto = new DeadLetterRetryResponse(result.Success, result.RepublishedTo, result.ErrorMessage);

            return result.Success ? Results.Ok(dto) : Results.UnprocessableEntity(dto);
        })
        .RequireAuthorization(CodeFlowApiDefaults.Policies.OpsWrite);

        group.MapGet("/metrics", async (IDeadLetterStore store, CancellationToken cancellationToken) =>
        {
            var queues = await store.ListQueuesAsync(cancellationToken);

            var builder = new StringBuilder();
            builder.AppendLine("# HELP codeflow_dlq_messages Number of messages currently in the dead-letter queue.");
            builder.AppendLine("# TYPE codeflow_dlq_messages gauge");

            foreach (var queue in queues)
            {
                builder.Append("codeflow_dlq_messages{queue=\"")
                    .Append(EscapeLabel(queue.QueueName))
                    .Append("\"} ")
                    .Append(queue.MessageCount)
                    .AppendLine();
            }

            return Results.Text(builder.ToString(), "text/plain; version=0.0.4; charset=utf-8");
        })
        .RequireAuthorization(CodeFlowApiDefaults.Policies.OpsRead);

        return routes;
    }

    private static string EscapeLabel(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);
    }
}
