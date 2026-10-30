using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using RKNNSharp;

namespace RKNNSharp.Smoke;

// Diagnostic only: both assemblies consume the exact same retained DMA samples.
internal static class ContractComparison
{
    internal static int Run(string modelPath, string fixture, string baselineDll, int iterations, string? expectedHash = null)
    {
        if (iterations is < 6 or > 120) { throw new ArgumentOutOfRangeException(nameof(iterations)); }
        var sources = new List<NativeSampleLease>();
        int released = 0;
        using var capture = new GStreamerCapture(fixture, "h264", false, true);
        try
        {
            for (int id = 0; id < 6; id++)
            {
                var native = capture.Pull();
                try { sources.Add(new NativeSampleLease(ref native, id, ColorSpace.Bt601Full)); }
                finally { if (native.Token != 0) { GStreamerCapture.Release(ref native); } }
            }
            capture.Pause();
            using var baseline = new BaselineModel(baselineDll, modelPath);
            using var candidate = new RknnModel(modelPath, NpuCoreMask.Core0, expectedHash);
            using var annotated = DmaImage.Allocate(2560, 1440);
            var profile = YoloV5Profile.CreateOfficial640Example();
            for (int warmup = 0; warmup < 5; warmup++)
            {
                sources[0].WithFrame(frame => { baseline.ReadRun(frame); candidate.Run(frame); baseline.Detect(frame); candidate.Detect(frame, profile); return 0; });
            }
            var baselineCopies = new List<double>();
            var candidateCopies = new List<double>();
            var baselineAllocations = new List<long>();
            var candidateAllocations = new List<long>();
            InferenceResult? retained = null;
            float[][]? retainedBits = null;
            long elementsCompared = 0, detections = 0;
            for (int request = 0; request < iterations; request++)
            {
                sources[request % sources.Count].WithFrame(frame =>
                {
                    RawSnapshot before;
                    InferenceResult after;
                    // Alternate order rather than assigning the same cache/GC position to one version.
                    if ((request & 1) == 0) { before = baseline.ReadRun(frame); after = ReadCandidate(); }
                    else { after = ReadCandidate(); before = baseline.ReadRun(frame); }
                    if (before.Transform != after.Transform) { throw new InvalidOperationException("Baseline/candidate transform differs."); }
                    if (before.Values.Length != after.Outputs.Count) { throw new InvalidOperationException("Output count differs."); }
                    for (int head = 0; head < before.Values.Length; head++)
                    {
                        RequireBits(before.Values[head], after.Outputs[head].Values.Span);
                        elementsCompared += before.Values[head].Length;
                        if (retained is not null && MemoryMarshal.TryGetArray(retained.Outputs[head].Values, out var oldArray) &&
                            MemoryMarshal.TryGetArray(after.Outputs[head].Values, out var newArray) && ReferenceEquals(oldArray.Array, newArray.Array))
                        { throw new InvalidOperationException("Independent Run snapshots share an array."); }
                    }
                    var expected = baseline.Detect(frame);
                    var actual = candidate.Detect(frame, profile);
                    if (!expected.SequenceEqual(actual.Detections)) { throw new InvalidOperationException("Baseline/candidate Detection differs."); }
                    annotated.CopyFrom(frame); annotated.DrawRectangles(actual.Detections);
                    detections += actual.Detections.Count;
                    baselineCopies.Add(before.ManagedOutputsMs); candidateCopies.Add(after.Timings.ManagedOutputsMs);
                    baselineAllocations.Add(before.AllocatedBytes);
                    if (retained is null) { retained = after; retainedBits = after.Outputs.Select(output => output.Values.ToArray()).ToArray(); }
                    for (int head = 0; head < retained.Outputs.Count; head++) { RequireBits(retainedBits![head], retained.Outputs[head].Values.Span); }
                    return 0;

                    InferenceResult ReadCandidate()
                    {
                        long allocated = GC.GetAllocatedBytesForCurrentThread();
                        var result = candidate.Run(frame);
                        candidateAllocations.Add(GC.GetAllocatedBytesForCurrentThread() - allocated);
                        return result;
                    }
                });
            }
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "contract-comparison", iterations, sources = sources.Count,
                rawFloatElementsBitExact = elementsCompared, strictDetections = true, detections, oldSnapshotUnchanged = true,
                independentArrays = true, allDrawn = true,
                baselineManagedOutputs = Stats(baselineCopies), candidateManagedOutputs = Stats(candidateCopies),
                baselineAllocatedBytes = baselineAllocations.Average(), candidateAllocatedBytes = candidateAllocations.Average(),
                allocationScope = "baseline reflection adds small diagnostic allocation; core timing excludes reflection/comparison" }));
        }
        finally
        {
            foreach (var source in sources) { source.Dispose(); released++; }
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "contract-release", acquired = sources.Count, released }));
        }
        return 0;
    }

    private static void RequireBits(ReadOnlySpan<float> expected, ReadOnlySpan<float> actual)
    {
        if (!MemoryMarshal.AsBytes(expected).SequenceEqual(MemoryMarshal.AsBytes(actual)))
        { throw new InvalidOperationException("Float tensor bits or retained snapshot changed."); }
    }

    private static object Stats(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { average = sorted.Average(), p50 = sorted[(int)Math.Ceiling(sorted.Length * .5) - 1],
            p95 = sorted[(int)Math.Ceiling(sorted.Length * .95) - 1] };
    }

    private sealed record RawSnapshot(float[][] Values, LetterboxTransform Transform, double ManagedOutputsMs, long AllocatedBytes);

    private sealed class BaselineModel : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("accepted-rknn", isCollectible: true);
        private readonly object _model;
        private readonly object _profile;
        private readonly Type _frameType;
        private readonly MethodInfo _run;
        private readonly MethodInfo _detect;

        internal BaselineModel(string path, string modelPath)
        {
            var assembly = _context.LoadFromAssemblyPath(Path.GetFullPath(path));
            var modelType = assembly.GetType("RKNNSharp.RknnModel", true)!;
            var maskType = assembly.GetType("RKNNSharp.NpuCoreMask", true)!;
            _frameType = assembly.GetType("RKNNSharp.DmaFrame", true)!;
            var profileType = assembly.GetType("RKNNSharp.YoloV5Profile", true)!;
            _model = Activator.CreateInstance(modelType, [modelPath, Enum.ToObject(maskType, 1)])!;
            _profile = profileType.GetMethod("CreateOfficial640Example")!.Invoke(null, [.25f, .45f])!;
            _run = modelType.GetMethod("Run", [_frameType])!;
            _detect = modelType.GetMethod("Detect", [_frameType, profileType])!;
        }

        private object Frame(DmaFrame frame) => JsonSerializer.Deserialize(JsonSerializer.Serialize(frame), _frameType)!;
        internal RawSnapshot ReadRun(DmaFrame frame)
        {
            object input = Frame(frame);
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            object result = _run.Invoke(_model, [input])!;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            var tensors = ((IEnumerable)result.GetType().GetProperty("Outputs")!.GetValue(result)!).Cast<object>();
            var values = tensors.Select(output => ((ReadOnlyMemory<float>)output.GetType().GetProperty("Values")!.GetValue(output)!).ToArray()).ToArray();
            object transform = result.GetType().GetProperty("Transform")!.GetValue(result)!;
            object timings = result.GetType().GetProperty("Timings")!.GetValue(result)!;
            return new RawSnapshot(values, JsonSerializer.Deserialize<LetterboxTransform>(JsonSerializer.Serialize(transform)),
                (double)timings.GetType().GetProperty("ManagedOutputsMs")!.GetValue(timings)!, allocated);
        }

        internal Detection[] Detect(DmaFrame frame)
        {
            object result = _detect.Invoke(_model, [Frame(frame), _profile])!;
            object values = result.GetType().GetProperty("Detections")!.GetValue(result)!;
            return JsonSerializer.Deserialize<Detection[]>(JsonSerializer.Serialize(values))!;
        }

        public void Dispose() { ((IDisposable)_model).Dispose(); _context.Unload(); }
    }
}
