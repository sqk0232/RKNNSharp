using System.Security.Cryptography;
using System.Text.Json;
using RKNNSharp;

namespace RKNNSharp.Smoke;

internal static class ModelHashVerification
{
    internal static int Run(string modelPath)
    {
        string digest;
        using (var stream = File.OpenRead(modelPath)) { digest = Convert.ToHexString(SHA256.HashData(stream)); }
        using (var warm = new RknnModel(modelPath)) { }
        var counts = new List<int>();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            try { using var rejected = new RknnModel(modelPath, NpuCoreMask.Core0, new string('0', 64));
                throw new InvalidOperationException("Incorrect model digest was accepted."); }
            catch (InvalidDataException) { }
            using (var lower = new RknnModel(modelPath, NpuCoreMask.Core0, digest.ToLowerInvariant())) { }
            using (var upper = new RknnModel(modelPath, NpuCoreMask.Core0, digest)) { }
            using (var disabled = new RknnModel(modelPath, NpuCoreMask.Core0, null)) { }
            using (var legacy = new RknnModel(modelPath)) { }
            counts.Add(Directory.GetFiles("/proc/self/fd").Length);
        }
        if (counts.Distinct().Count() != 1) { throw new InvalidOperationException("Repeated model hash/load cycles did not release descriptors."); }
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "model-hash-verification", digest, lowerUpperAccepted = true,
            badHashRejected = true, nullAndLegacyAccepted = true, recovery = true, cycles = 3, fdCounts = counts }));
        return 0;
    }
}
