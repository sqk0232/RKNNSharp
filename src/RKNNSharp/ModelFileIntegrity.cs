using System.Security.Cryptography;

namespace RKNNSharp;

internal static class ModelFileIntegrity
{
    internal static void Verify(string modelPath, string? expectedModelSha256)
    {
        if (expectedModelSha256 is null) { return; }
        if (expectedModelSha256.Length != 64 || expectedModelSha256.Any(character => !char.IsAsciiHexDigit(character)))
        {
            throw new ArgumentException("Expected model SHA256 must contain exactly 64 hexadecimal characters.", nameof(expectedModelSha256));
        }
        // The stream closes before native path-based loading; this is a content precheck, not an atomic signed load.
        using var stream = new FileStream(modelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        string actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(expectedModelSha256, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Model SHA256 mismatch. Expected: {expectedModelSha256}; actual: {actual}.");
        }
    }
}
