using System.Security.Cryptography;
using CodeFlow.Persistence;
using FluentAssertions;

namespace CodeFlow.Persistence.Tests.Security;

public sealed class AesGcmSecretProtectorTests
{
    private static readonly byte[] MasterKey = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] OtherKey = Enumerable.Range(32, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void Protect_then_Unprotect_round_trips_plaintext()
    {
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));

        var cipher = protector.Protect("bearer-token-abc123");
        var plain = protector.Unprotect(cipher);

        plain.Should().Be("bearer-token-abc123");
    }

    [Fact]
    public void Protect_produces_distinct_ciphertexts_for_same_plaintext()
    {
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));

        var cipherA = protector.Protect("identical-secret");
        var cipherB = protector.Protect("identical-secret");

        cipherA.Should().NotEqual(cipherB);
    }

    [Fact]
    public void Unprotect_rejects_tampered_ciphertext()
    {
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));

        var cipher = protector.Protect("bearer-token");
        cipher[cipher.Length - 1] ^= 0xFF;

        Assert.Throws<AuthenticationTagMismatchException>(() => protector.Unprotect(cipher));
    }

    [Fact]
    public void Unprotect_rejects_ciphertext_created_with_different_key()
    {
        byte[] cipher;
        using (var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey)))
        {
            cipher = protector.Protect("bearer-token");
        }

        using var otherProtector = new AesGcmSecretProtector(new SecretsOptions(OtherKey));

        Assert.Throws<AuthenticationTagMismatchException>(() => otherProtector.Unprotect(cipher));
    }

    [Fact]
    public void Unprotect_rejects_ciphertext_shorter_than_nonce_plus_tag()
    {
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));

        Assert.Throws<CryptographicException>(() => protector.Unprotect(new byte[16]));
    }

    [Fact]
    public void Constructor_rejects_wrong_length_master_key()
    {
        var act = () => new AesGcmSecretProtector(new SecretsOptions(new byte[16]));

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Protect_and_Unprotect_round_trip_under_concurrent_load()
    {
        using var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));

        const int workerCount = 32;
        const int iterationsPerWorker = 200;

        var failures = new List<Exception>();
        var failureLock = new object();

        var tasks = Enumerable.Range(0, workerCount).Select(workerId => Task.Run(() =>
        {
            for (var i = 0; i < iterationsPerWorker; i++)
            {
                var plaintext = $"worker-{workerId}-iteration-{i}-payload";
                try
                {
                    var cipher = protector.Protect(plaintext);
                    var round = protector.Unprotect(cipher);
                    if (round != plaintext)
                    {
                        lock (failureLock)
                        {
                            failures.Add(new InvalidOperationException(
                                $"round-trip mismatch: expected '{plaintext}', got '{round}'"));
                        }
                    }
                }
                catch (Exception ex)
                {
                    lock (failureLock)
                    {
                        failures.Add(ex);
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        failures.Should().BeEmpty(
            "concurrent Protect/Unprotect must round-trip cleanly because AesGcm instance members are not documented as thread-safe");
    }

    [Fact]
    public void Protect_throws_after_Dispose()
    {
        var protector = new AesGcmSecretProtector(new SecretsOptions(MasterKey));
        protector.Dispose();

        var act = () => protector.Protect("anything");

        act.Should().Throw<ObjectDisposedException>();
    }
}
