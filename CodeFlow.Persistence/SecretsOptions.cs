namespace CodeFlow.Persistence;

public sealed record SecretsOptions(byte[] MasterKey)
{
    public const int ExpectedKeyLengthBytes = 32;

    public static SecretsOptions FromBase64(string masterKeyBase64)
    {
        if (string.IsNullOrWhiteSpace(masterKeyBase64))
        {
            throw new InvalidOperationException(
                $"Secrets master key missing. Configure '{SecretsConfigurationKeys.MasterKey}' with a base64-encoded {ExpectedKeyLengthBytes}-byte key.");
        }

        byte[] key;
        try
        {
            key = Convert.FromBase64String(masterKeyBase64);
        }
        catch (FormatException exception)
        {
            throw new InvalidOperationException(
                $"Secrets master key '{SecretsConfigurationKeys.MasterKey}' is not valid base64.",
                exception);
        }

        if (key.Length != ExpectedKeyLengthBytes)
        {
            throw new InvalidOperationException(
                $"Secrets master key must decode to exactly {ExpectedKeyLengthBytes} bytes; got {key.Length}.");
        }

        return new SecretsOptions(key);
    }
}

public static class SecretsConfigurationKeys
{
    public const string SectionName = "CodeFlow:Secrets";
    public const string MasterKey = "CodeFlow:Secrets:MasterKey";
}
