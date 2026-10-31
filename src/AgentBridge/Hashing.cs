using System.Security.Cryptography;

namespace AgentBridge;

public static class Hashing
{
    /// <summary>Lowercase hex SHA-256. (Convert.ToHexStringLower is .NET 9+, so it is not used.)</summary>
    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
