using System.Security.Cryptography;
using System.Text;

namespace QuickDrop.Core.Security;

public static class PairingSecurity
{
    public static string GenerateCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public static byte[] CreateSalt(int length = 16) => RandomNumberGenerator.GetBytes(length);

    public static string ComputeDiscoveryTag(Guid sessionId, ReadOnlySpan<byte> salt, string code)
    {
        var key = DeriveKey(code, salt);
        var value = HMACSHA256.HashData(key, sessionId.ToByteArray());
        return Convert.ToHexString(value.AsSpan(0, 12));
    }

    public static string ComputePairProof(
        Guid sessionId,
        ReadOnlySpan<byte> salt,
        string code,
        ReadOnlySpan<byte> clientNonce,
        string receiverInstanceId)
    {
        var sessionBytes = sessionId.ToByteArray();
        var receiverBytes = Encoding.UTF8.GetBytes(receiverInstanceId);
        var payload = new byte[sessionBytes.Length + clientNonce.Length + receiverBytes.Length];
        sessionBytes.CopyTo(payload, 0);
        clientNonce.CopyTo(payload.AsSpan(sessionBytes.Length));
        receiverBytes.CopyTo(payload, sessionBytes.Length + clientNonce.Length);
        return Convert.ToHexString(HMACSHA256.HashData(DeriveKey(code, salt), payload));
    }

    public static bool FixedTimeEqualsHex(string expected, string actual)
    {
        try
        {
            return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(expected), Convert.FromHexString(actual));
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] DeriveKey(string code, ReadOnlySpan<byte> salt)
    {
        return Rfc2898DeriveBytes.Pbkdf2(code, salt, 20_000, HashAlgorithmName.SHA256, 32);
    }
}
