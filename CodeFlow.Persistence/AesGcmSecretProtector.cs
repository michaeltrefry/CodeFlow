using System.Security.Cryptography;

namespace CodeFlow.Persistence;

public sealed class AesGcmSecretProtector : ISecretProtector, IDisposable
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    // AesGcm instance members are not documented as thread-safe, so we cannot share one
    // AesGcm across concurrent Protect/Unprotect callers. The protector is registered as a
    // singleton and hit on every MCP/Git-host token read/write path, so we hold the key bytes
    // and construct a fresh AesGcm per call (construction is cheap relative to a DB round-trip).
    private readonly byte[] masterKey;
    private bool disposed;

    public AesGcmSecretProtector(SecretsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.MasterKey);

        if (options.MasterKey.Length != SecretsOptions.ExpectedKeyLengthBytes)
        {
            throw new ArgumentException(
                $"Master key must be {SecretsOptions.ExpectedKeyLengthBytes} bytes; got {options.MasterKey.Length}.",
                nameof(options));
        }

        masterKey = (byte[])options.MasterKey.Clone();
    }

    public byte[] Protect(string plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ObjectDisposedException.ThrowIf(disposed, this);

        var plaintextBytes = System.Text.Encoding.UTF8.GetBytes(plaintext);
        var payload = new byte[NonceSize + plaintextBytes.Length + TagSize];

        var nonce = payload.AsSpan(0, NonceSize);
        var ciphertext = payload.AsSpan(NonceSize, plaintextBytes.Length);
        var tag = payload.AsSpan(NonceSize + plaintextBytes.Length, TagSize);

        RandomNumberGenerator.Fill(nonce);

        using var aesGcm = new AesGcm(masterKey, TagSize);
        aesGcm.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        return payload;
    }

    public string Unprotect(byte[] ciphertext)
    {
        ArgumentNullException.ThrowIfNull(ciphertext);
        ObjectDisposedException.ThrowIf(disposed, this);

        if (ciphertext.Length < NonceSize + TagSize)
        {
            throw new CryptographicException(
                $"Ciphertext payload too short: expected at least {NonceSize + TagSize} bytes, got {ciphertext.Length}.");
        }

        var nonce = ciphertext.AsSpan(0, NonceSize);
        var tag = ciphertext.AsSpan(ciphertext.Length - TagSize, TagSize);
        var encrypted = ciphertext.AsSpan(NonceSize, ciphertext.Length - NonceSize - TagSize);

        var plaintextBytes = new byte[encrypted.Length];

        using var aesGcm = new AesGcm(masterKey, TagSize);
        aesGcm.Decrypt(nonce, encrypted, tag, plaintextBytes);

        return System.Text.Encoding.UTF8.GetString(plaintextBytes);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(masterKey);
        disposed = true;
    }
}
