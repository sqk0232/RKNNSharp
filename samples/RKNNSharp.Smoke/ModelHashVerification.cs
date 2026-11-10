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
        using (var warm = new RknnPipeline(ModelType.RawTensor, modelPath)) { }
        var counts = new List<int>();
        for (int cycle = 0; cycle < 3; cycle++)
        {
            try { using var rejected = new RknnPipeline(ModelType.RawTensor, modelPath, options: new RknnModelOptions { CoreMask = NpuCoreMask.Core0, ExpectedModelSha256 = new string('0', 64) });
                throw new InvalidOperationException("Incorrect model digest was accepted."); }
            catch (InvalidDataException) { }
            using (var lower = new RknnPipeline(ModelType.RawTensor, modelPath, options: new RknnModelOptions { CoreMask = NpuCoreMask.Core0, ExpectedModelSha256 = digest.ToLowerInvariant() })) { }
            using (var upper = new RknnPipeline(ModelType.RawTensor, modelPath, options: new RknnModelOptions { CoreMask = NpuCoreMask.Core0, ExpectedModelSha256 = digest })) { }
            using (var disabled = new RknnPipeline(ModelType.RawTensor, modelPath, options: new RknnModelOptions { CoreMask = NpuCoreMask.Core0 })) { }
            using (var defaults = new RknnPipeline(ModelType.RawTensor, modelPath)) { }
            counts.Add(Directory.GetFiles("/proc/self/fd").Length);
        }
        if (counts.Distinct().Count() != 1) { throw new InvalidOperationException("Repeated model hash/load cycles did not release descriptors."); }
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "model-hash-verification", digest, lowerUpperAccepted = true,
            badHashRejected = true, nullAndLegacyAccepted = true, recovery = true, cycles = 3, fdCounts = counts }));
        return 0;
    }
}
