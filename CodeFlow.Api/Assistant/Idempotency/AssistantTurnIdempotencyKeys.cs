using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace CodeFlow.Api.Assistant.Idempotency;

/// <summary>
/// sc-525 — Helpers for the request-side of idempotency: header parsing/validation and the
/// canonical hash of <see cref="SendMessageRequest"/> used to detect "same key, different
/// body" collisions.
/// </summary>
public static class AssistantTurnIdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";

    public const int MinKeyLength = 8;
    public const int MaxKeyLength = 128;

    private static readonly JsonSerializerOptions HashJsonOptions = new(JsonSerializerDefaults.Web)
    {
        // Stable shape: ordering follows the record's declared property order. We avoid
        // pretty-printing and reuse Web defaults so the hash matches what
        // System.Text.Json emits server-side for the same DTO across versions.
        WriteIndented = false,
    };

    public enum KeyValidation
    {
        Absent,
        Valid,
        Malformed,
    }

    /// <summary>
    /// Returns <see cref="KeyValidation.Valid"/> with the trimmed key string when the
    /// caller supplied a syntactically acceptable Idempotency-Key. Empty / missing →
    /// <see cref="KeyValidation.Absent"/>; everything else →
    /// <see cref="KeyValidation.Malformed"/>.
    /// </summary>
    public static KeyValidation TryRead(HttpContext httpContext, out string? key)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        key = null;

        if (!httpContext.Request.Headers.TryGetValue(HeaderName, out var values) || values.Count == 0)
        {
            return KeyValidation.Absent;
        }

        var raw = values[0];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return KeyValidation.Absent;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length < MinKeyLength || trimmed.Length > MaxKeyLength)
        {
            return KeyValidation.Malformed;
        }

        foreach (var ch in trimmed)
        {
            if (!IsAllowed(ch))
            {
                return KeyValidation.Malformed;
            }
        }

        key = trimmed;
        return KeyValidation.Valid;
    }

    private static bool IsAllowed(char ch) =>
        (ch >= 'a' && ch <= 'z')
        || (ch >= 'A' && ch <= 'Z')
        || (ch >= '0' && ch <= '9')
        || ch == '-'
        || ch == '_';

    /// <summary>
    /// Hashes the canonical JSON form of <paramref name="request"/> with SHA-256 and returns
    /// the hex digest. Stable across retries since System.Text.Json record serialization
    /// follows declared property order and Web defaults are deterministic for the value
    /// shapes we use here.
    /// </summary>
    public static string ComputeRequestHash(object request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var json = JsonSerializer.Serialize(request, request.GetType(), HashJsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        var hash = SHA256.HashData(bytes);

        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }
}
