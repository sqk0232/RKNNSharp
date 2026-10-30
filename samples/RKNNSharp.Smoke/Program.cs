using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using RKNNSharp;
using RKNNSharp.Smoke;

if (args.Length == 1 && args[0] is "--help" or "-h")
{
    Console.WriteLine(SingleFrameSample.Help);
    return 0;
}

if (args.Length < 2 || (args.Length < 3 && args[1] != "--verify-model-hash"))
{
    Console.Error.WriteLine("Usage: RKNNSharp.Smoke MODEL (--file MP4|--file-unpaced MP4|--rtsp-file PRIVATE_URL_FILE) [frames=5] [codec=h264] [confidence=.25] [colorspace=auto|601limit|601full|709limit] [cycles=1] [warmup=0] [mode=detect|run|compare|pipeline|replay] [core=Auto|Core0|Core1|Core2|All] [sessionsPerCore=1|2] [inFlight=0(auto)] [workers=0(auto)|1|3|6]");
    return 2;
}

try
{
    if (args[1] == "--single-frame") { return SingleFrameSample.Run(args); }
    if (args[1] == "--verify-model-hash") { return ModelHashVerification.Run(args[0]); }
    if (args[1] == "--contract-compare")
    {
        if (args.Length < 4) { throw new ArgumentException("Contract comparison requires a fixture and baseline DLL."); }
        return ContractComparison.Run(args[0], args[2], args[3], args.Length > 4 ? int.Parse(args[4], CultureInfo.InvariantCulture) : 12,
            args.Length > 5 ? args[5] : null);
    }
    bool rtsp = args[1] switch { "--file" or "--file-unpaced" => false, "--rtsp-file" => true, _ => throw new ArgumentException("Unknown source mode.") };
    bool fileUnpaced = args[1] == "--file-unpaced";
    string source = rtsp ? File.ReadAllText(args[2]).Trim() : args[2];
    int frames = args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 5;
    string codec = args.Length > 4 ? args[4] : "h264";
    float confidence = args.Length > 5 ? float.Parse(args[5], CultureInfo.InvariantCulture) : 0.25f;
    ColorSpace? colorSpace = args.Length > 6 ? args[6] switch
    {
        "601limit" => ColorSpace.Bt601Limited, "601full" => ColorSpace.Bt601Full,
        "709limit" => ColorSpace.Bt709Limited, "auto" => null, _ => throw new ArgumentException("Unknown color space.")
    } : null;
    int cycles = args.Length > 7 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 1;
    int warmup = args.Length > 8 ? int.Parse(args[8], CultureInfo.InvariantCulture) : 0;
    string mode = args.Length > 9 ? args[9] : "detect";
    NpuCoreMask coreMask = args.Length > 10 && Enum.TryParse<NpuCoreMask>(args[10], true, out var parsedMask) ? parsedMask : NpuCoreMask.Auto;
    if (args.Length > 10 && !Enum.TryParse<NpuCoreMask>(args[10], true, out _)) { throw new ArgumentException("Unknown NPU core mask."); }
    if (frames < 1 || frames > (fileUnpaced ? 600 : 300)) { throw new ArgumentOutOfRangeException(nameof(frames)); }
    if (cycles is < 1 or > 5) { throw new ArgumentOutOfRangeException(nameof(cycles)); }
    if (warmup is < 0 or > 30) { throw new ArgumentOutOfRangeException(nameof(warmup)); }
    if (mode is not ("run" or "detect" or "compare" or "pipeline" or "replay")) { throw new ArgumentException("Unknown inference mode."); }
    if (fileUnpaced && mode != "pipeline") { throw new ArgumentException("Unpaced finite files require pipeline mode."); }
    var configuration = new PipelineConfiguration(
        args.Length > 11 ? int.Parse(args[11], CultureInfo.InvariantCulture) : 1,
        args.Length > 12 ? int.Parse(args[12], CultureInfo.InvariantCulture) : 0,
        args.Length > 13 ? int.Parse(args[13], CultureInfo.InvariantCulture) : 0);
    if (mode == "pipeline")
    {
        return await ParallelSmoke.RunAsync(args[0], source, codec, rtsp, frames, warmup, colorSpace, cycles, confidence, configuration, fileUnpaced);
    }
    if (mode == "replay")
    {
        return await ParallelSmoke.RunReplayAsync(args[0], source, codec, rtsp, frames, warmup, colorSpace, confidence, configuration);
    }
    var profile = YoloV5Profile.CreateOfficial640Example(confidence);
    int baselineFds = CountFds();
    long baselineRss = Process.GetCurrentProcess().WorkingSet64;
    int count = 0, totalDetections = 0;
    var totals = new List<double>();
    var metrics = new List<FrameMetrics>();
    var cycleFds = new List<FdSnapshot>();
    for (int cycle = 0; cycle < cycles; cycle++)
    {
      {
        using var model = new RknnModel(args[0], coreMask);
        profile.ValidateModel(model.Inputs, model.Outputs);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "model", versions = model.RuntimeVersions, model.CoreMask, inputs = model.Inputs, outputs = model.Outputs }));
        using var capture = new GStreamerCapture(source, codec, rtsp);
        DmaImage? annotated = null;
        try
        {
            for (int index = 0; index < frames + warmup; index++)
            {
                GstFrame sample;
                try { sample = capture.Pull(); }
                catch (EndOfStreamException) { break; }
                try
                {
                    var frame = GStreamerCapture.Borrow(in sample, colorSpace);
                    annotated ??= DmaImage.Allocate(frame.Width, frame.Height);
                    long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
                    int gen0 = GC.CollectionCount(0), gen1 = GC.CollectionCount(1), gen2 = GC.CollectionCount(2);
                    long totalStart = Stopwatch.GetTimestamp();
                    InferenceResult? raw = null;
                    IReadOnlyList<Detection> detections;
                    RunTimings timings;
                    LetterboxTransform transform;
                    double decodeMs;
                    object? comparison = null;
                    if (mode == "run")
                    {
                        raw = model.Run(frame);
                        long postprocessStart = Stopwatch.GetTimestamp();
                        detections = profile.Decode(raw.Outputs, raw.Transform);
                        decodeMs = Stopwatch.GetElapsedTime(postprocessStart).TotalMilliseconds;
                        timings = raw.Timings; transform = raw.Transform;
                    }
                    else
                    {
                        var detected = model.Detect(frame, profile);
                        detections = detected.Detections;
                        decodeMs = detected.PostprocessMs;
                        timings = detected.Timings; transform = detected.Transform;
                        if (mode == "compare")
                        {
                            raw = model.Run(frame);
                            var reference = profile.Decode(raw.Outputs, raw.Transform);
                            comparison = CompareDetections(reference, detections);
                        }
                    }
                    long copyStart = Stopwatch.GetTimestamp();
                    annotated.CopyFrom(frame);
                    double copyMs = Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                    long drawStart = Stopwatch.GetTimestamp();
                    annotated.DrawRectangles(detections);
                    double drawMs = Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;
                    double totalMs = Stopwatch.GetElapsedTime(totalStart).TotalMilliseconds;
                    var measurement = new FrameMetrics(timings.PreprocessMs, timings.InferenceMs,
                        timings.RuntimeOutputsMs, timings.ManagedOutputsMs, decodeMs, copyMs, drawMs, totalMs,
                        GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                        GC.CollectionCount(0) - gen0, GC.CollectionCount(1) - gen1, GC.CollectionCount(2) - gen2);
                    bool isWarmup = index < warmup;
                    if (!isWarmup)
                    {
                        totals.Add(totalMs); metrics.Add(measurement);
                        totalDetections += detections.Count;
                        count++;
                    }
                    var stats = raw?.Outputs.Select(output => new
                    {
                        index = output.Info.Index, minimum = Minimum(output.Values.Span), maximum = Maximum(output.Values.Span),
                        negatives = CountNegative(output.Values.Span), nonFinite = CountNonFinite(output.Values.Span)
                    });
                    Console.WriteLine(JsonSerializer.Serialize(new
                    {
                        kind = "frame", cycle, index, isWarmup, sample.PtsNs, frame.Width, frame.Height,
                        frame.FileDescriptor, frame.RowStrideBytes, frame.HeightStride, frame.SizeBytes,
                        sample.Fd1, sample.Offset1, capsColorSpace = sample.ColorSpace, selectedColorSpace = frame.ColorSpace,
                        transform, timings, comparison,
                        postprocessMs = decodeMs, totalMs, measurement,
                        detections, outputStats = stats
                    }));
                }
                finally { GStreamerCapture.Release(ref sample); }
            }
            if (annotated is not null)
            {
                using var raceStart = new Barrier(2);
                var operation = Task.Run(() =>
                {
                    raceStart.SignalAndWait();
                    try { model.Run(annotated); return "completed-before-dispose"; }
                    catch (ObjectDisposedException) { return "rejected-after-dispose"; }
                });
                raceStart.SignalAndWait();
                model.Dispose();
                string raceOutcome = await operation;
                annotated.WithFrame(frame =>
                {
                    try { model.Run(frame); }
                    catch (ObjectDisposedException) { return 0; }
                    throw new InvalidOperationException("Disposed model accepted a new operation.");
                });
                annotated.Dispose();
                annotated.Dispose();
                try
                {
                    annotated.WithFrame(_ => 0);
                    throw new InvalidOperationException("Disposed DMA image accepted a new borrow.");
                }
                catch (ObjectDisposedException) { }
                Console.WriteLine(JsonSerializer.Serialize(new { kind = "lifecycle", cycle, raceOutcome, postDisposeRejected = true, doubleDisposeSafe = true }));
            }
        }
        finally { annotated?.Dispose(); }
      }
      GC.Collect(); GC.WaitForPendingFinalizers();
      cycleFds.Add(SnapshotFds());
    }
    GC.Collect(); GC.WaitForPendingFinalizers();
    int finalFds = CountFds();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        kind = "summary", frames = count, totalDetections, confidence, warmup, mode, coreMask, cycleFds,
        meanMs = totals.Count > 0 ? totals.Average() : 0,
        minMs = totals.Count > 0 ? totals.Min() : 0, maxMs = totals.Count > 0 ? totals.Max() : 0,
        phases = new
        {
            preprocess = Summarize(metrics.Select(item => item.PreprocessMs)),
            inference = Summarize(metrics.Select(item => item.InferenceMs)),
            runtimeOutputs = Summarize(metrics.Select(item => item.RuntimeOutputsMs)),
            managedOutputs = Summarize(metrics.Select(item => item.ManagedOutputsMs)),
            decode = Summarize(metrics.Select(item => item.DecodeMs)),
            copy = Summarize(metrics.Select(item => item.CopyMs)),
            draw = Summarize(metrics.Select(item => item.DrawMs)),
            total = Summarize(metrics.Select(item => item.TotalMs)),
            allocatedBytes = Summarize(metrics.Select(item => (double)item.AllocatedBytes)),
            gen0 = metrics.Sum(item => item.Gen0), gen1 = metrics.Sum(item => item.Gen1), gen2 = metrics.Sum(item => item.Gen2)
        },
        baselineFds, finalFds, baselineRss, finalRss = Process.GetCurrentProcess().WorkingSet64,
        anchors = "explicit official640 example assumption; labels unavailable",
        source = rtsp ? "rtsp-private-file" : "local-mp4"
    }));
    return count > 0 ? 0 : 1;
}
catch (Exception exception) when (exception is IOException or ArgumentException or RknnException or
    InvalidOperationException or OperationCanceledException or TimeoutException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
{
    // Never log source, command arguments, exception.ToString() or credential-file contents.
    Console.Error.WriteLine(exception is RknnException ? $"Smoke failed: {exception.Message}" : $"Smoke failed: {exception.GetType().Name}.");
    return 1;
}

static int CountFds() => SnapshotFds().Total;
static object CompareDetections(IReadOnlyList<Detection> reference, IReadOnlyList<Detection> optimized)
{
    if (reference.Count != optimized.Count) { throw new RknnException("Float/raw detection count mismatch on the same DMA frame."); }
    float maximumScoreDifference = 0, maximumCoordinateDifference = 0;
    for (int i = 0; i < reference.Count; i++)
    {
        var a = reference[i]; var b = optimized[i];
        if (a.ClassId != b.ClassId) { throw new RknnException("Float/raw class/order mismatch on the same DMA frame."); }
        maximumScoreDifference = MathF.Max(maximumScoreDifference, MathF.Abs(a.Confidence - b.Confidence));
        maximumCoordinateDifference = MathF.Max(maximumCoordinateDifference,
            MathF.Max(MathF.Max(MathF.Abs(a.Box.Left - b.Box.Left), MathF.Abs(a.Box.Top - b.Box.Top)),
                MathF.Max(MathF.Abs(a.Box.Right - b.Box.Right), MathF.Abs(a.Box.Bottom - b.Box.Bottom))));
    }
    if (maximumScoreDifference > 0.00001f || maximumCoordinateDifference > 0.01f)
    {
        throw new RknnException("Float/raw detection numeric difference exceeds score 1e-5 / source-coordinate 0.01 pixel tolerance.");
    }
    return new { matched = true, count = reference.Count, maximumScoreDifference, maximumCoordinateDifference };
}
static object Summarize(IEnumerable<double> values)
{
    var sorted = values.Order().ToArray();
    if (sorted.Length == 0) { return new { count = 0, average = 0d, p50 = 0d, p95 = 0d }; }
    return new { count = sorted.Length, average = sorted.Average(), p50 = sorted[(int)Math.Ceiling(sorted.Length * 0.5) - 1], p95 = sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1] };
}
static FdSnapshot SnapshotFds()
{
    if (!OperatingSystem.IsLinux()) { return new(0, 0, 0, 0, 0); }
    string[] descriptors = Directory.GetFiles("/proc/self/fd");
    int dma = 0, devices = 0, sockets = 0, pipes = 0;
    foreach (string descriptor in descriptors)
    {
        string target;
        try { target = new FileInfo(descriptor).LinkTarget ?? ""; }
        catch (IOException) { continue; }
        if (target.Contains("dmabuf", StringComparison.OrdinalIgnoreCase) || target.Contains("dma_buf", StringComparison.OrdinalIgnoreCase)) { dma++; }
        else if (target.StartsWith("/dev/", StringComparison.Ordinal)) { devices++; }
        else if (target.StartsWith("socket:", StringComparison.Ordinal)) { sockets++; }
        else if (target.StartsWith("pipe:", StringComparison.Ordinal)) { pipes++; }
    }
    return new(descriptors.Length, dma, devices, sockets, pipes);
}
static int CountNegative(ReadOnlySpan<float> values) { int count = 0; foreach (float value in values) { if (value < 0) { count++; } } return count; }
static int CountNonFinite(ReadOnlySpan<float> values) { int count = 0; foreach (float value in values) { if (!float.IsFinite(value)) { count++; } } return count; }
static float Minimum(ReadOnlySpan<float> values) { float result = float.PositiveInfinity; foreach (float value in values) { result = MathF.Min(result, value); } return result; }
static float Maximum(ReadOnlySpan<float> values) { float result = float.NegativeInfinity; foreach (float value in values) { result = MathF.Max(result, value); } return result; }

internal sealed record FdSnapshot(int Total, int DmaBuffers, int Devices, int Sockets, int Pipes);
