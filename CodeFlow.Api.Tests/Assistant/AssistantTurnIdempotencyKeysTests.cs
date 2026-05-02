using CodeFlow.Api.Assistant.Idempotency;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace CodeFlow.Api.Tests.Assistant;

public sealed class AssistantTurnIdempotencyKeysTests
{
    [Fact]
    public void TryRead_returns_Absent_when_header_missing()
    {
        var ctx = new DefaultHttpContext();

        var result = AssistantTurnIdempotencyKeys.TryRead(ctx, out var key);

        result.Should().Be(AssistantTurnIdempotencyKeys.KeyValidation.Absent);
        key.Should().BeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void TryRead_returns_Absent_for_blank_header(string headerValue)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[AssistantTurnIdempotencyKeys.HeaderName] = headerValue;

        var result = AssistantTurnIdempotencyKeys.TryRead(ctx, out var key);

        result.Should().Be(AssistantTurnIdempotencyKeys.KeyValidation.Absent);
        key.Should().BeNull();
    }

    [Theory]
    [InlineData("short")] // < 8 chars
    [InlineData("has spaces in it")]
    [InlineData("has/slash")]
    [InlineData("has!bang")]
    public void TryRead_returns_Malformed_for_invalid_keys(string headerValue)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[AssistantTurnIdempotencyKeys.HeaderName] = headerValue;

        var result = AssistantTurnIdempotencyKeys.TryRead(ctx, out var key);

        result.Should().Be(AssistantTurnIdempotencyKeys.KeyValidation.Malformed);
        key.Should().BeNull();
    }

    [Fact]
    public void TryRead_rejects_keys_longer_than_max()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[AssistantTurnIdempotencyKeys.HeaderName] = new string('a', AssistantTurnIdempotencyKeys.MaxKeyLength + 1);

        var result = AssistantTurnIdempotencyKeys.TryRead(ctx, out _);

        result.Should().Be(AssistantTurnIdempotencyKeys.KeyValidation.Malformed);
    }

    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")] // UUID form
    [InlineData("turn_abc-123_xyz")]
    [InlineData("AbCdEfGh")]
    public void TryRead_accepts_valid_keys(string headerValue)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[AssistantTurnIdempotencyKeys.HeaderName] = headerValue;

        var result = AssistantTurnIdempotencyKeys.TryRead(ctx, out var key);

        result.Should().Be(AssistantTurnIdempotencyKeys.KeyValidation.Valid);
        key.Should().Be(headerValue);
    }

    [Fact]
    public void TryRead_trims_surrounding_whitespace()
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Headers[AssistantTurnIdempotencyKeys.HeaderName] = "  abcdefgh  ";

        var result = AssistantTurnIdempotencyKeys.TryRead(ctx, out var key);

        result.Should().Be(AssistantTurnIdempotencyKeys.KeyValidation.Valid);
        key.Should().Be("abcdefgh");
    }

    [Fact]
    public void ComputeRequestHash_is_stable_for_equal_records()
    {
        var a = new SampleRequest("hello", null, null);
        var b = new SampleRequest("hello", null, null);

        AssistantTurnIdempotencyKeys.ComputeRequestHash(a)
            .Should().Be(AssistantTurnIdempotencyKeys.ComputeRequestHash(b));
    }

    [Fact]
    public void ComputeRequestHash_changes_with_content()
    {
        var a = new SampleRequest("hello", null, null);
        var b = new SampleRequest("hello!", null, null);

        AssistantTurnIdempotencyKeys.ComputeRequestHash(a)
            .Should().NotBe(AssistantTurnIdempotencyKeys.ComputeRequestHash(b));
    }

    [Fact]
    public void ComputeRequestHash_returns_hex_sha256_length()
    {
        var hash = AssistantTurnIdempotencyKeys.ComputeRequestHash(new SampleRequest("x", null, null));

        hash.Should().HaveLength(64); // SHA-256 hex
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    private sealed record SampleRequest(string Content, string? Provider, string? Model);
}
