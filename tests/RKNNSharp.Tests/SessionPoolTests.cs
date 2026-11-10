using Microsoft.Extensions.Logging;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class SessionPoolTests
{
    private static readonly DmaFrame Frame = new(3, 32, 32, PixelFormat.Rgb888, 96, 32, 3072);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void ConstructorBuildsEverySlotAndDisposeDestroysEachExactlyOnce(int size)
    {
        var factory = new Factory();
        var model = factory.CreateModel(size);
        Assert.Equal(1, new RknnModelOptions().SessionPoolSize);
        Assert.Equal(size, model.SessionPoolSize);
        Assert.Equal(1, factory.Creates);
        Assert.Equal(size - 1, factory.Duplicates);
        Assert.Equal(size, factory.Sessions.Count);
        Assert.All(factory.Sessions, session => Assert.Equal(0, session.Runs));
        Assert.Equal(NpuCoreMask.Core2, model.CoreMask);
        Assert.All(factory.Sessions, session => Assert.Equal(model.CoreMask, session.CoreMask));
        model.Dispose();
        model.Dispose();
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.Disposals));
        Assert.Throws<ObjectDisposedException>(() => model.Run(Frame));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidPoolSizeFailsBeforeCreate(int size)
    {
        var factory = new Factory();
        Assert.Throws<ArgumentOutOfRangeException>(() => factory.CreateModel(size));
        Assert.Empty(factory.Sessions);
    }

    [Fact]
    public void MissingDuplicateExportFallsBackOnlyDuringConstructionAndWarns()
    {
        var factory = new Factory { DuplicateFailure = new EntryPointNotFoundException("rks_dup_context") };
        var logger = new TestLogger();
        using var logging = new TestLoggerFactory(logger);
        using var model = factory.CreateModel(3, logging);
        for (int i = 0; i < 15; i++) { model.Run(Frame); }
        Assert.Equal(3, factory.Creates);
        Assert.Equal(1, factory.Duplicates);
        Assert.Equal(15, factory.Sessions.Sum(session => session.Runs));
        Assert.Single(logger.Messages);
        Assert.Contains("without shared weights", logger.Messages[0]);
        Assert.Contains("Memory usage will be higher", logger.Messages[0]);
    }

    [Fact]
    public void DuplicateRuntimeFailureDoesNotFallBackAndReleasesPartialPool()
    {
        var factory = new Factory { FailDuplicateAt = 2, DuplicateFailure = new RknnException("dup failed") };
        var error = Assert.Throws<RknnException>(() => factory.CreateModel());
        Assert.Equal("dup failed", error.Message);
        Assert.Equal(1, factory.Creates);
        Assert.Equal(2, factory.Sessions.Count);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.Disposals));
    }

    [Fact]
    public void FallbackCreateFailureReleasesAllPreviouslyCreatedSessions()
    {
        var factory = new Factory { DuplicateFailure = new EntryPointNotFoundException(), FailCreateAt = 3 };
        Assert.Throws<RknnException>(() => factory.CreateModel());
        Assert.Equal(2, factory.Sessions.Count);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.Disposals));
    }

    [Fact]
    public async Task ExhaustedPoolWaitsAndConcurrentPipelineCallsUseDistinctExistingSlots()
    {
        var factory = new Factory();
        using var entered = new CountdownEvent(3);
        using var release = new ManualResetEventSlim();
        factory.BeforeRun = () => { if (entered.CurrentCount > 0) { entered.Signal(); } Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var model = factory.CreateModel();
        using var pipeline = new RknnPipeline(ModelType.RawTensor, "controlled", null, null, null, (_, _) => model);
        var first = Enumerable.Range(0, 3).Select(_ => Start(() => pipeline.PredictRaw(Frame))).ToArray();
        Task? fourth = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            Assert.All(factory.Sessions, session => Assert.Equal(1, session.Active));
            var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fourth = Start(() => { attempted.SetResult(); pipeline.PredictRaw(Frame); });
            await attempted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(100);
            Assert.False(fourth.IsCompleted);
            Assert.Equal(1, factory.Creates);
            Assert.Equal(2, factory.Duplicates);
            Assert.Equal(3, factory.Sessions.Sum(session => session.Runs));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(first.Append(fourth ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.Equal(4, factory.Sessions.Sum(session => session.Runs));
        Assert.All(factory.Sessions, session => Assert.InRange(session.MaxActive, 0, 1));
        Assert.Equal(3, factory.Sessions.Count);
    }

    [Fact]
    public async Task DisposeWaitsForAllBorrowedSlotsAndRejectsWaitingBorrowers()
    {
        var factory = new Factory();
        using var entered = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        factory.BeforeRun = () => { entered.Signal(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var model = factory.CreateModel(2);
        var running = Enumerable.Range(0, 2).Select(_ => Start(() => model.Run(Frame))).ToArray();
        Task? disposal = null;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
            var waiting = Start(() => Assert.Throws<ObjectDisposedException>(() => model.Run(Frame)));
            disposal = Start(model.Dispose);
            await waiting.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(disposal.IsCompleted);
            Assert.All(factory.Sessions, session => Assert.Equal(0, session.Disposals));
        }
        finally
        {
            release.Set();
            await Task.WhenAll(running.Append(disposal ?? Task.CompletedTask)).WaitAsync(TimeSpan.FromSeconds(10));
            model.Dispose();
        }
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.Disposals));
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection, 1)]
    [InlineData(ModelType.YoloV5Detection, 3)]
    [InlineData(ModelType.YoloV8Detection, 1)]
    [InlineData(ModelType.YoloV8Detection, 3)]
    public async Task PipelineDisposeRejectsQueuedAndNewPredictsAndWaitsForEveryActiveSession(ModelType type, int size)
    {
        var timeout = TimeSpan.FromSeconds(10);
        var factory = new Factory { Detection = true, Dfl = type == ModelType.YoloV8Detection };
        using var entered = new CountdownEvent(size);
        var releases = Enumerable.Range(0, size).Select(_ => new ManualResetEventSlim()).ToArray();
        int nextRun = -1;
        factory.BeforeRun = () =>
        {
            int index = Interlocked.Increment(ref nextRun);
            entered.Signal();
            Assert.True(releases[index].Wait(timeout));
        };
        var model = factory.CreateModel(size);
        var pipeline = new RknnPipeline(type, "controlled", RknnPipelineTests.Config(type), null, null, (_, _) => model);
        var running = Enumerable.Range(0, size).Select(_ => Start(() => pipeline.Predict(Frame))).ToArray();
        Task waiting = Task.CompletedTask;
        Task disposal = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(timeout));
            Assert.All(factory.Sessions, session => Assert.Equal(1, Volatile.Read(ref session.Active)));
            var waiterThread = new TaskCompletionSource<Thread>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = Start(() =>
            {
                waiterThread.SetResult(Thread.CurrentThread);
                Assert.Throws<ObjectDisposedException>(() => pipeline.Predict(Frame));
            });
            var thread = await waiterThread.Task.WaitAsync(timeout);
            // All slots are held and this thread has no other blocking operation.
            // Observe its pool wait before starting Dispose, without timing delays.
            Assert.True(SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, timeout));
            Assert.False(waiting.IsCompleted);
            Assert.Equal(size, factory.Sessions.Sum(session => Volatile.Read(ref session.Runs)));

            disposal = Start(pipeline.Dispose);
            await waiting.WaitAsync(timeout);
            // The queued caller's rejection proves disposal has reached the pool.
            await Start(() => Assert.Throws<ObjectDisposedException>(() => pipeline.Predict(Frame))).WaitAsync(timeout);
            Assert.False(disposal.IsCompleted);
            Assert.All(factory.Sessions, session => Assert.Equal(0, Volatile.Read(ref session.Disposals)));
            if (size > 1)
            {
                releases[0].Set();
                var completed = await Task.WhenAny(running).WaitAsync(timeout);
                await completed.WaitAsync(timeout);
                Assert.False(disposal.IsCompleted);
                Assert.All(factory.Sessions, session => Assert.Equal(0, Volatile.Read(ref session.Disposals)));
            }
        }
        finally
        {
            foreach (var release in releases) { release.Set(); }
            try { await Task.WhenAll(running.Append(waiting).Append(disposal)).WaitAsync(timeout); }
            finally
            {
                // Keep cleanup bounded even when a lifecycle regression is detected.
                try { await Start(pipeline.Dispose).WaitAsync(timeout); }
                finally { foreach (var release in releases) { release.Dispose(); } }
            }
        }
        Assert.True(disposal.IsCompletedSuccessfully);
        Assert.Equal(size, factory.Sessions.Sum(session => session.Runs));
        Assert.All(factory.Sessions, session =>
        {
            Assert.Equal(0, session.Active);
            Assert.Equal(1, session.Disposals);
            Assert.Equal(1, session.Runs);
        });
        Assert.Throws<ObjectDisposedException>(() => pipeline.Predict(Frame));
    }

    [Fact]
    public void DynamicShapesOnlySetExistingContextsAndResultsKeepTheirOwnMetadata()
    {
        var factory = new Factory { Dynamic = true };
        using var model = factory.CreateModel();
        using var pipeline = new RknnPipeline(ModelType.RawTensor, "dynamic", null, null, null, (_, _) => model);
        Assert.Equal(3, factory.Sessions.Count);
        Assert.Equal(0u, model.Inputs[0].Dimensions[1]);
        var first = pipeline.PredictRaw(Frame, [1, 32, 64, 3]);
        var second = pipeline.PredictRawInto(Frame, [new float[1]], [1, 64, 32, 3]);
        Assert.Equal(new uint[] { 1, 32, 64, 3 }, first.Inputs![0].Dimensions);
        Assert.Equal(new uint[] { 1, 64, 32, 3 }, second.Inputs![0].Dimensions);
        Assert.Equal(0u, model.Inputs[0].Dimensions[1]);
        Assert.Equal(2, factory.Sessions.Sum(session => session.ShapeCalls));
        Assert.Equal(1, factory.Creates);
        Assert.Equal(2, factory.Duplicates);
        Assert.All(factory.Sessions, session => Assert.Equal(0, session.Disposals));
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection)]
    [InlineData(ModelType.YoloV8Detection)]
    public void DynamicDetectionBindsProfilesToEachCallsActualShape(ModelType type)
    {
        var factory = new Factory { Dynamic = true, Detection = true, Dfl = type == ModelType.YoloV8Detection };
        using var model = factory.CreateModel();
        var original = RknnPipelineTests.Config(type);
        var config = new RknnPipelineConfig(1,
            anchors: type == ModelType.YoloV5Detection ? original.Anchors : null,
            heads: type == ModelType.YoloV8Detection ? original.Heads : null, regMax: original.RegMax);
        using var pipeline = new RknnPipeline(type, "dynamic", config, null, null, (_, _) => model);
        Assert.Same(config, pipeline.Config);
        Assert.Empty(pipeline.Predict(Frame, [1, 32, 32, 3]).Detections);
        var result = pipeline.PredictRaw(Frame, [1, 64, 64, 3]);
        Assert.Empty(pipeline.Decode(result).Detections);
        Assert.Empty(pipeline.Predict(Frame, [1, 96, 96, 3]).Detections);
        Assert.Equal(3, factory.Sessions.Sum(session => session.ShapeCalls));
        Assert.All(factory.Sessions, session => Assert.Equal(0, session.Disposals));
    }

    [Fact]
    public async Task ConcurrentDynamicResultsKeepEachLeasesShapeAndOutputMetadata()
    {
        var factory = new Factory { Dynamic = true };
        using var entered = new CountdownEvent(3);
        using var release = new ManualResetEventSlim();
        factory.BeforeRun = () => { entered.Signal(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        using var model = factory.CreateModel();
        var runs = new uint[] { 32, 64, 96 }.Select(size => Task.Factory.StartNew(
            () => model.Run(Frame, new uint[] { 1, size, size, 3 }), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
        try { Assert.True(entered.Wait(TimeSpan.FromSeconds(10))); }
        finally { release.Set(); }
        var results = await Task.WhenAll(runs).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new uint[] { 32, 64, 96 }, results.Select(result => result.Inputs![0].Dimensions[1]));
        Assert.All(results, result => Assert.Single(result.Outputs));
        Assert.Equal(1, factory.Creates);
        Assert.Equal(2, factory.Duplicates);
        Assert.All(factory.Sessions, session => Assert.Equal(1, session.ShapeCalls));
    }

    [Fact]
    public void ShapeFailureAndRunFailureReturnTheSlotWithoutCreatingReplacement()
    {
        var factory = new Factory { Dynamic = true, ShapeFailure = true };
        using var model = factory.CreateModel(1);
        Assert.Throws<NotSupportedException>(() => model.Run(Frame, [1, 32, 32, 3]));
        factory.ShapeFailure = false;
        factory.RunFailure = true;
        Assert.Throws<RknnException>(() => model.Run(Frame, [1, 32, 32, 3]));
        factory.RunFailure = false;
        Assert.Single(model.Run(Frame, [1, 64, 64, 3]).Outputs);
        Assert.Equal(1, factory.Creates);
        Assert.Equal(0, factory.Sessions[0].Disposals);
        Assert.Equal(0, factory.Sessions[0].Active);
    }

    [Fact]
    public void UnsupportedDynamicCurrentAttributesStillFailWithActualProperties()
    {
        var factory = new Factory { Dynamic = true, BadQuantization = true };
        using var model = factory.CreateModel();
        var error = Assert.Throws<ArgumentException>(() => model.Run(Frame, [1, 32, 32, 3]));
        Assert.Contains("zeroPoint=0", error.Message);
        Assert.Contains("dimensions=[1,32,32,3]", error.Message);
        Assert.Equal(0, factory.Sessions.Sum(session => session.Runs));
    }

    private static Task Start(Action action) => Task.Factory.StartNew(action, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private sealed class Factory
    {
        internal readonly List<Session> Sessions = [];
        internal int Creates, Duplicates;
        internal bool Dynamic, Detection, Dfl, ShapeFailure, RunFailure, BadQuantization;
        internal int FailDuplicateAt = 1, FailCreateAt;
        internal Exception? DuplicateFailure;
        internal Action? BeforeRun;
        internal RknnModel CreateModel(int size = 3, ILoggerFactory? logging = null) => new("controlled",
            new RknnModelOptions { SessionPoolSize = size, LoggerFactory = logging, CoreMask = NpuCoreMask.Core2 },
            (_, _) =>
            {
                Creates++;
                if (Creates == FailCreateAt) { throw new RknnException("create failed"); }
                return NewSession();
            });
        internal Session NewSession() { var session = new Session(this); Sessions.Add(session); return session; }
    }

    private sealed class Session : IRknnSession
    {
        private readonly Factory _factory;
        internal int Runs, Disposals, Active, MaxActive, ShapeCalls;
        public IReadOnlyList<TensorInfo> Inputs { get; private set; }
        public IReadOnlyList<TensorInfo> Outputs { get; private set; }
        public string RuntimeVersions => "controlled";
        public NpuCoreMask CoreMask => NpuCoreMask.Core2;
        public FloatOutputMode OutputMode => FloatOutputMode.SdkExact;
        public CallerThreadPlacement? CallerThreadPlacement => null;
        public bool IsDynamic => _factory.Dynamic;
        internal Session(Factory factory)
        {
            _factory = factory;
            Inputs = [Input([1, factory.Dynamic ? 0u : 32u, 32, 3])];
            Outputs = MakeOutputs(32, 32);
        }
        private TensorInfo Input(IReadOnlyList<uint> dims) => new(0, "images", dims,
            dims[0] * dims[1] * dims[2] * dims[3], 1, 2, 2, _factory.BadQuantization ? 0 : -128, 1f / 255);
        private IReadOnlyList<TensorInfo> MakeOutputs(uint width, uint height)
        {
            if (!_factory.Detection) { return [new(0, "output", [1], 1, 0, 0, 0, 0, 1)]; }
            var outputs = new List<TensorInfo>();
            foreach (uint stride in new uint[] { 8, 16, 32 })
            {
                Add(_factory.Dfl ? 8u : 18u);
                if (_factory.Dfl) { Add(1); }
                void Add(uint channels) => outputs.Add(new((uint)outputs.Count, "head",
                    [1, channels, height / stride, width / stride], channels * (height / stride) * (width / stride), 0, 2, 2, 0, .1f));
            }
            return outputs;
        }
        public IRknnSession Duplicate()
        {
            _factory.Duplicates++;
            if (_factory.Duplicates == _factory.FailDuplicateAt && _factory.DuplicateFailure is not null) { throw _factory.DuplicateFailure; }
            return _factory.NewSession();
        }
        public void SetInputShape(IReadOnlyList<uint> dimensions)
        {
            ShapeCalls++;
            Assert.Equal(0, Disposals);
            if (_factory.ShapeFailure) { throw new NotSupportedException("rks_set_input_shapes unavailable. Actual input " + NativeInputContract.Describe(Inputs[0])); }
            Inputs = [Input(dimensions)];
            Outputs = MakeOutputs(dimensions[2], dimensions[1]);
        }
        public InferenceResult Run(DmaFrame frame)
        {
            int active = Interlocked.Increment(ref Active);
            MaxActive = Math.Max(MaxActive, active);
            try
            {
                Assert.Equal(1, active);
                Assert.Equal(0, Disposals);
                Interlocked.Increment(ref Runs);
                _factory.BeforeRun?.Invoke();
                Assert.Equal(0, Volatile.Read(ref Disposals));
                if (_factory.RunFailure) { throw new RknnException("run failed"); }
                return new(Outputs.Select(info => new TensorOutput(info, Enumerable.Repeat(-20f, (int)info.ElementCount).ToArray())).ToArray(),
                    new(32, 32, 1, 1, 0, 0), new(0, 0, 0, 0));
            }
            finally { Interlocked.Decrement(ref Active); }
        }
        public InferenceResult RunInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers)
        {
            var result = Run(frame);
            return result with { Outputs = result.Outputs.Select((output, index) =>
            {
                output.Values.CopyTo(outputBuffers[index]);
                return new TensorOutput(output.Info, outputBuffers[index]);
            }).ToArray() };
        }
        public DetectionResult Detect(DmaFrame frame, YoloV5Profile profile)
        {
            profile.ValidateModel(Inputs, Outputs);
            var result = Run(frame);
            return new(profile.Decode(result.Outputs, result.Transform), result.Transform, result.Timings, 0);
        }
        public DetectionResult Detect(DmaFrame frame, YoloDetectionProfile profile)
        {
            profile.ValidateModel(Inputs, Outputs);
            var result = Run(frame);
            return new(profile.Decode(result.Outputs, result.Transform), result.Transform, result.Timings, 0);
        }
        public void Dispose() { Assert.Equal(0, Active); Disposals++; }
    }

    private sealed class TestLogger : ILogger
    {
        internal readonly List<string> Messages = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
    private sealed class TestLoggerFactory(TestLogger logger) : ILoggerFactory
    {
        public ILogger CreateLogger(string categoryName) => logger;
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }
    }
}
