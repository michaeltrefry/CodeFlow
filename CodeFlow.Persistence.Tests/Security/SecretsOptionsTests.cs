using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Persistence.Tests.Security;

public sealed class SecretsOptionsTests
{
    [Fact]
    public void FromBase64_parses_valid_32_byte_base64()
    {
        var base64 = Convert.ToBase64String(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());

        var options = SecretsOptions.FromBase64(base64);

        options.MasterKey.Should().HaveCount(32);
    }

    [Fact]
    public void FromBase64_rejects_empty_value()
    {
        var act = () => SecretsOptions.FromBase64(string.Empty);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*master key missing*");
    }

    [Fact]
    public void FromBase64_rejects_malformed_base64()
    {
        var act = () => SecretsOptions.FromBase64("not-valid-base64!!!");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not valid base64*");
    }

    [Fact]
    public void FromBase64_rejects_wrong_length_key()
    {
        var shortKey = Convert.ToBase64String(new byte[16]);

        var act = () => SecretsOptions.FromBase64(shortKey);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*must decode to exactly 32 bytes*");
    }
}
