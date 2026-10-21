using System.Security.Cryptography;
using System.Text;

namespace ReconAI.Core.Importing;

public static class Hashing
{
    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA256.HashData(data));

    public static string Sha256Hex(string text) => Sha256Hex(Encoding.UTF8.GetBytes(text));
}
