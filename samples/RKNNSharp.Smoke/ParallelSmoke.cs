using System.Diagnostics;
using System.Text.Json;
using RKNNSharp;

namespace RKNNSharp.Smoke;

internal static class ParallelSmoke
{
    internal static async Task<int> RunAsync(string modelPath, string source, string codec, bool rtsp,
        int frameCount, int warmup, ColorSpace? colorSpace, int cycles, float confidence, PipelineConfiguration configuration,
        bool fileUnpaced = false)
    {
        var fdCounts = new List<int>();
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            var sessions = new List<WorkerSession>();
            long acquired = 0, released = 0;
            try
            {
                for (int worker = 0; worker < configuration.WorkerCount; worker++)
                {
                    sessions.Add(new WorkerSession(modelPath, configuration.CoreForWorker(worker), confidence));
                }
                // Reopen finite unpaced files after warmup, so every configuration measures source indices from zero.
                if (warmup > 0 && fileUnpaced)
                {
                    using var warmupCapture = new GStreamerCapture(source, codec, false, true);
                    await WarmupAsync(warmupCapture, sessions, warmup, colorSpace).ConfigureAwait(false);
                    acquired++; released++;
                }
                using var capture = new GStreamerCapture(source, codec, rtsp, fileUnpaced);
                if (warmup > 0 && !fileUnpaced)
                {
                    await WarmupAsync(capture, sessions, warmup, colorSpace).ConfigureAwait(false);
                    acquired++; released++;
                }
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var measurements = new List<FrameResult>();
                var orderedLatencies = new List<double>();
                long expectedId = 0;
                ulong previousPts = 0;
                bool reachedEos = false;
                long totalDetections = 0;
                var watch = Stopwatch.StartNew();
                var counters = await BoundedOrderedPipeline.RunAsync<NativeSampleLease, FrameResult>(
                    configuration.WorkerCount, configuration.Capacity,
                    (id, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        if (id >= frameCount)
                        {
                            if (fileUnpaced)
                            {
                                // The requested finite fixture length must end at EOS, without dropping a decoded owner.
                                try { GstFrame extra = capture.Pull(); GStreamerCapture.Release(ref extra);
                                    throw new InvalidOperationException("Finite fixture has more frames than requested."); }
                                catch (EndOfStreamException) { reachedEos = true; }
                            }
                            return ValueTask.FromResult<NativeSampleLease?>(null);
                        }
                        GstFrame native;
                        try { native = capture.Pull(); }
                        catch (EndOfStreamException) { reachedEos = true; return ValueTask.FromResult<NativeSampleLease?>(null); }
                        try
                        {
                            var lease = new NativeSampleLease(ref native, id, colorSpace);
                            Interlocked.Increment(ref acquired);
                            return ValueTask.FromResult<NativeSampleLease?>(lease);
                        }
                        finally { if (native.Token != 0) { GStreamerCapture.Release(ref native); } }
                    },
                    (worker, lease) => sessions[worker].Process(lease),
                    lease => { lease.Dispose(); Interlocked.Increment(ref released); },
                    completion =>
                    {
                        if (completion.FrameId != expectedId++) { throw new InvalidOperationException("Pipeline completion order is invalid."); }
                        if (completion.Error is not null) { return; }
                        var result = completion.Result ?? throw new InvalidOperationException("Missing pipeline result.");
                        if (result.FrameId != completion.FrameId ||
                            (measurements.Count > 0 && result.PtsNs <= previousPts))
                        {
                            throw new InvalidOperationException("Pipeline frame/result/PTS pairing is invalid.");
                        }
                        previousPts = result.PtsNs;
                        measurements.Add(result);
                        double orderedLatencyMs = Stopwatch.GetElapsedTime(result.AdmittedAtTicks).TotalMilliseconds;
                        orderedLatencies.Add(orderedLatencyMs);
                        totalDetections += result.Detections.Count;
                    }, cancellation.Token).ConfigureAwait(false);
                watch.Stop();
                if (acquired != released || counters.Submitted != counters.Consumed || measurements.Count != counters.Consumed)
                {
                    throw new InvalidOperationException("Pipeline frame ownership or completion count mismatch.");
                }
                if (measurements.Count == 0) { throw new InvalidOperationException("Pipeline produced no frames."); }
                if (fileUnpaced && (!reachedEos || measurements.Count != frameCount))
                { throw new InvalidOperationException("Finite fixture frame count/EOS mismatch."); }
                for (int index = 0; index < measurements.Count; index++)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { kind = "pipeline-frame", cycle,
                        orderedLatencyMs = orderedLatencies[index], result = measurements[index] }));
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "pipeline-summary", cycle, frames = measurements.Count, totalDetections,
                    elapsedMs = watch.Elapsed.TotalMilliseconds, completedFps = measurements.Count / watch.Elapsed.TotalSeconds,
                    sourcePtsFps = measurements.Count > 1 ? (measurements.Count - 1) * 1e9 / (measurements[^1].PtsNs - measurements[0].PtsNs) : 0,
                    latency = Statistics(measurements.Select(item => item.LatencyMs)),
                    orderedLatency = Statistics(orderedLatencies),
                    processing = Statistics(measurements.Select(item => item.ProcessingMs)),
                    queue = Statistics(measurements.Select(item => item.QueueMs)),
                    inference = Statistics(measurements.Select(item => item.Timings.InferenceMs)),
                    preprocess = Statistics(measurements.Select(item => item.Timings.PreprocessMs)),
                    runtimeOutputs = Statistics(measurements.Select(item => item.Timings.RuntimeOutputsMs)),
                    decode = Statistics(measurements.Select(item => item.DecodeMs)),
                    copy = Statistics(measurements.Select(item => item.CopyMs)), draw = Statistics(measurements.Select(item => item.DrawMs)),
                    counters, acquired, released, ordered = true, allDrawn = measurements.All(item => item.DrawCompleted),
                    warmupPerWorker = warmup, configuration.SessionsPerCore, configuration.WorkerCount, configuration.Capacity,
                    perWorker = sessions.Select((session, index) => new { worker = index, session.CoreMask, measured = session.Processed - warmup }),
                    reachedEos, source = rtsp ? "rtsp-private-file" : fileUnpaced ? "local-mp4-unpaced" : "local-mp4"
                }));
            }
            finally
            {
                // RunAsync drains/joins before returning or throwing; models/images now have no in-flight use.
                foreach (var session in sessions) { session.Dispose(); }
            }
            GC.Collect(); GC.WaitForPendingFinalizers();
            fdCounts.Add(Directory.GetFiles("/proc/self/fd").Length);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "pipeline-release-summary", fdCounts }));
        return 0;
    }

    private static async Task WarmupAsync(GStreamerCapture capture, List<WorkerSession> sessions, int warmup, ColorSpace? colorSpace)
    {
        GstFrame native = capture.Pull();
        NativeSampleLease? lease = null;
        try
        {
            lease = new NativeSampleLease(ref native, -1, colorSpace);
            await Task.WhenAll(sessions.Select(session => Task.Run(() =>
            {
                for (int i = 0; i < warmup; i++) { session.Process(lease); }
            }))).ConfigureAwait(false);
        }
        finally
        {
            lease?.Dispose();
            if (native.Token != 0) { GStreamerCapture.Release(ref native); }
        }
    }

    private static object Statistics(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        return new { count = sorted.Length, average = sorted.Length > 0 ? sorted.Average() : 0,
            p50 = sorted.Length > 0 ? sorted[(int)Math.Ceiling(sorted.Length * 0.5) - 1] : 0,
            p95 = sorted.Length > 0 ? sorted[(int)Math.Ceiling(sorted.Length * 0.95) - 1] : 0 };
    }

    internal static async Task<int> RunReplayAsync(string modelPath, string source, string codec, bool rtsp,
        int requestCount, int warmup, ColorSpace? colorSpace, float confidence, PipelineConfiguration configuration)
    {
        var sources = new List<NativeSampleLease>();
        var sessions = new List<WorkerSession>();
        GStreamerCapture? capture = null;
        int acquired = 0, released = 0;
        try
        {
            capture = new GStreamerCapture(source, codec, rtsp);
            for (int sourceId = 0; sourceId < 6; sourceId++)
            {
                GstFrame native = capture.Pull();
                try { sources.Add(new NativeSampleLease(ref native, sourceId, colorSpace)); acquired++; }
                finally { if (native.Token != 0) { GStreamerCapture.Release(ref native); } }
            }
            capture.Pause();
            for (int worker = 0; worker < configuration.WorkerCount; worker++)
            {
                sessions.Add(new WorkerSession(modelPath, configuration.CoreForWorker(worker), confidence));
            }
            var expectedBySource = new Dictionary<long, IReadOnlyList<Detection>>();
            foreach (int workerCount in configuration.ReplayWorkerCounts)
            {
                await Task.WhenAll(sessions.Take(workerCount).Select(session => Task.Run(() =>
                {
                    for (int i = 0; i < warmup; i++) { session.Process(sources[i % sources.Count]); }
                }))).ConfigureAwait(false);
                int[] before = sessions.Select(session => session.Processed).ToArray();
                int requestReleased = 0;
                var results = new List<FrameResult>(requestCount);
                var orderedLatencies = new List<double>(requestCount);
                using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                long nextId = 0;
                var watch = Stopwatch.StartNew();
                var counters = await BoundedOrderedPipeline.RunAsync<ReplayRequest, FrameResult>(
                    workerCount, configuration.CapacityForWorkers(workerCount),
                    (id, token) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return ValueTask.FromResult(id < requestCount
                            ? new ReplayRequest(id, sources[(int)(id % sources.Count)], Stopwatch.GetTimestamp()) : null);
                    },
                    (worker, request) => sessions[worker].Process(request.Source, request.RequestId, request.AdmittedAt),
                    _ => Interlocked.Increment(ref requestReleased),
                    completion =>
                    {
                        if (completion.FrameId != nextId++) { throw new InvalidOperationException("Replay completion ordering mismatch."); }
                        if (completion.Error is not null) { return; }
                        var result = completion.Result ?? throw new InvalidOperationException("Replay result missing.");
                        if (result.FrameId != completion.FrameId || result.SourceFrameId != result.FrameId % sources.Count)
                        {
                            throw new InvalidOperationException("Replay request/source identity mismatch.");
                        }
                        if (!expectedBySource.TryGetValue(result.SourceFrameId, out var expected))
                        {
                            expectedBySource.Add(result.SourceFrameId, result.Detections);
                        }
                        else if (!expected.SequenceEqual(result.Detections))
                        {
                            Console.WriteLine(JsonSerializer.Serialize(new
                            {
                                kind = "replay-mismatch", workerCount, result.FrameId, result.SourceFrameId, result.CoreMask,
                                expected, actual = result.Detections
                            }));
                            throw new RknnException("Replayed source produced different detection results across requests/cores.");
                        }
                        results.Add(result);
                        orderedLatencies.Add(Stopwatch.GetElapsedTime(result.AdmittedAtTicks).TotalMilliseconds);
                    }, cancellation.Token).ConfigureAwait(false);
                watch.Stop();
                if (results.Count != requestCount || requestReleased != requestCount)
                {
                    throw new InvalidOperationException("Replay did not complete/release every request.");
                }
                // Emit only after timing: per-frame JSON cannot throttle the replay benchmark.
                foreach (var result in results)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { kind = "replay-request", workerCount, result }));
                }
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    kind = "replay-summary", workerCount, requests = results.Count, uniqueSourceFrames = sources.Count,
                    elapsedMs = watch.Elapsed.TotalMilliseconds, processingRequestsPerSecond = results.Count / watch.Elapsed.TotalSeconds,
                    totalDetections = results.Sum(result => result.Detections.Count), allSourceOutputsIdentical = true,
                    processing = Statistics(results.Select(result => result.ProcessingMs)),
                    orderedLatency = Statistics(orderedLatencies), queue = Statistics(results.Select(result => result.QueueMs)),
                    preprocess = Statistics(results.Select(result => result.Timings.PreprocessMs)),
                    inference = Statistics(results.Select(result => result.Timings.InferenceMs)),
                    runtimeOutputs = Statistics(results.Select(result => result.Timings.RuntimeOutputsMs)),
                    decode = Statistics(results.Select(result => result.DecodeMs)),
                    copy = Statistics(results.Select(result => result.CopyMs)), draw = Statistics(results.Select(result => result.DrawMs)),
                    counters, requestReleased, warmupPerWorker = warmup, configuration.SessionsPerCore,
                    perWorker = sessions.Take(workerCount).Select((session, index) => new { session.CoreMask, requests = session.Processed - before[index] }),
                    scope = "six retained read-only real DMA source frames; capture paused; includes preprocessing/NPU/postprocessing/copy/draw; excludes continuous VPU/network"
                }));
            }
        }
        finally
        {
            foreach (var session in sessions) { session.Dispose(); }
            foreach (var sourceFrame in sources) { sourceFrame.Dispose(); released++; }
            capture?.Dispose();
            Console.WriteLine(JsonSerializer.Serialize(new { kind = "replay-release-summary", acquired, released,
                finalFds = Directory.GetFiles("/proc/self/fd").Length }));
        }
        GC.Collect(); GC.WaitForPendingFinalizers();
        return 0;
    }

    private sealed record ReplayRequest(long RequestId, NativeSampleLease Source, long AdmittedAt);

    private sealed class WorkerSession : IDisposable
    {
        private readonly RknnModel _model;
        private readonly YoloV5Profile _profile;
        private DmaImage? _annotated;
        internal NpuCoreMask CoreMask => _model.CoreMask;
        internal int Processed { get; private set; }
        internal WorkerSession(string path, NpuCoreMask core, float confidence)
        {
            _profile = YoloV5Profile.CreateOfficial640Example(confidence);
            _model = new(path, core);
        }

        internal FrameResult Process(NativeSampleLease lease, long? requestId = null, long? admittedAt = null) => lease.WithFrame(frame =>
        {
            long start = Stopwatch.GetTimestamp();
            long origin = admittedAt ?? lease.PulledAt;
            double queueMs = Stopwatch.GetElapsedTime(origin, start).TotalMilliseconds;
            _annotated ??= DmaImage.Allocate(frame.Width, frame.Height);
            var detection = _model.Detect(frame, _profile);
            long copy = Stopwatch.GetTimestamp();
            _annotated.CopyFrom(frame);
            double copyMs = Stopwatch.GetElapsedTime(copy).TotalMilliseconds;
            long draw = Stopwatch.GetTimestamp();
            _annotated.DrawRectangles(detection.Detections);
            double drawMs = Stopwatch.GetElapsedTime(draw).TotalMilliseconds;
            Processed++;
            return new FrameResult(requestId ?? lease.FrameId, lease.FrameId, lease.PtsNs, origin, CoreMask, frame.FileDescriptor,
                queueMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                Stopwatch.GetElapsedTime(origin).TotalMilliseconds,
                detection.Timings, detection.PostprocessMs, copyMs, drawMs, detection.Detections, true);
        });

        public void Dispose() { _annotated?.Dispose(); _model.Dispose(); }
    }

    private sealed record FrameResult(long FrameId, long SourceFrameId, ulong PtsNs, long AdmittedAtTicks, NpuCoreMask CoreMask, int SourceDmaFd,
        double QueueMs, double ProcessingMs, double LatencyMs, RunTimings Timings,
        double DecodeMs, double CopyMs, double DrawMs, IReadOnlyList<Detection> Detections, bool DrawCompleted);
}
