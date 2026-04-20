namespace CodeFlow.Persistence;

public interface ISecretProtector
{
    byte[] Protect(string plaintext);

    string Unprotect(byte[] ciphertext);
}
