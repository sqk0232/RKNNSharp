using Microsoft.Extensions.Logging;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class RknnPipelineTests
{
    private static readonly DmaFrame Frame = new(3, 32, 32, PixelFormat.Rgb888, 96, 32, 3072);
    private static readonly LetterboxTransform Transform = new(32, 32, 1, 1, 0, 0);
    private static readonly RunTimings Timings = new(1, 2, 3, 4);

    internal static RknnPipelineConfig Config(ModelType type) => type == ModelType.YoloV5Detection
        ? new(32, 32, 1, anchors: [[new(10, 13), new(16, 30), new(33, 23)], [new(30, 61), new(62, 45), new(59, 119)], [new(116, 90), new(156, 198), new(373, 326)]])
        : new(32, 32, 1, heads: [new(0, 1, null, 8), new(2, 3, null, 16), new(4, 5, null, 32)], regMax: 2);

    private static RknnPipeline Create(ModelType type, Session session, RknnPipelineConfig? config = null, ILogger? logger = null) =>
        new(type, "controlled.rknn", config ?? (type == ModelType.RawTensor ? null : Config(type)), null, logger, (_, _) => session);

    [Fact]
    public void PublicSurfaceRetainsUnifiedConstructorAndRawModelEntrypoint()
    {
        Assert.Equal(new[] { "YoloV5Detection", "YoloV8Detection", "RawTensor" }, Enum.GetNames<ModelType>());
        Assert.Equal(new[] { 0, 1, 2 }, Enum.GetValues<ModelType>().Select(value => (int)value).ToArray());
        var exported = typeof(RknnPipeline).Assembly.GetExportedTypes();
        foreach (string old in new[] { "YoloV5Profile", "YoloDetectionProfile", "YoloDetectionConfig", "YoloDetectionOutputFormat" })
        { Assert.DoesNotContain(exported, type => type.Name == old); }
        var constructor = Assert.Single(typeof(RknnPipeline).GetConstructors());
        Assert.Equal([typeof(ModelType), typeof(string), typeof(RknnPipelineConfig), typeof(RknnModelOptions), typeof(ILogger)],
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.True(constructor.GetParameters()[2].IsOptional);
        Assert.DoesNotContain(typeof(RknnPipeline).GetMethods(), method => method.Name is "Run" or "RunInto" or "Detect");
        foreach (Type contract in new[] { typeof(RknnModel), typeof(DmaFrame), typeof(DmaImage), typeof(DetectionResult), typeof(InferenceResult), typeof(TensorInfo), typeof(RknnModelOptions) })
        { Assert.Contains(contract, exported); }
        Assert.NotNull(typeof(RknnModel).GetConstructor([typeof(string)]));
    }

    private static RknnPipelineConfig AutoConfig(ModelType type)
    {
        var explicitConfig = Config(type);
        return new(classCount: explicitConfig.ClassCount,
            anchors: type == ModelType.YoloV5Detection ? explicitConfig.Anchors : null,
            heads: type == ModelType.YoloV8Detection ? explicitConfig.Heads : null,
            regMax: explicitConfig.RegMax);
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection)]
    [InlineData(ModelType.YoloV8Detection)]
    public void AutoDimensionsBindToModelMetadataAndKeepTheOriginalConfigUnbound(ModelType type)
    {
        var config = AutoConfig(type);
        var session = new Session(type, 96, 64);
        using var pipeline = Create(type, session, config);
        Assert.Equal(96, pipeline.InputWidth);
        Assert.Equal(64, pipeline.InputHeight);
        Assert.Equal(1, pipeline.BatchSize);
        Assert.Equal(96, pipeline.Config!.InputWidth);
        Assert.Equal(64, pipeline.Config.InputHeight);
        Assert.Equal(1, pipeline.Config.BatchSize);
        Assert.Same(session.Inputs[0], pipeline.InputTensor);
        Assert.Same(pipeline.InputTensor, pipeline.Config.InputTensor);
        Assert.Equal(1, pipeline.InputTensor.Layout);
        Assert.Equal(2, pipeline.InputTensor.DataType);
        Assert.Equal(2, pipeline.InputTensor.Quantization);
        Assert.Equal(-128, pipeline.InputTensor.ZeroPoint);
        Assert.Equal(1f / 255, pipeline.InputTensor.Scale);
        Assert.Equal("images", pipeline.InputTensor.Name);
        Assert.Equal(0, config.InputWidth);
        Assert.Equal(0, config.InputHeight);
        Assert.Equal(0, config.BatchSize);
        Assert.Null(config.InputTensor);
        Assert.Equal(ChannelOrder.Rgb, pipeline.Config.ChannelOrder);
        Assert.Equal(ResizeMode.Letterbox, pipeline.Config.ResizeMode);
        Assert.Equal(NormalizationMode.ModelDefault, pipeline.Config.Normalization);
        Assert.Single(pipeline.Predict(Frame).Detections);
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection)]
    [InlineData(ModelType.YoloV8Detection)]
    public void ExplicitDimensionsMustMatchTheModelAndReportBothValues(ModelType type)
    {
        var session = new Session(type, 64, 32);
        var error = Assert.Throws<ArgumentException>(() => Create(type, session, Config(type)));
        Assert.Contains("配置值)=32x32", error.Message);
        Assert.Contains("模型读出值)=64x32", error.Message);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, session.DetectCalls);

        using var matching = Create(type, new Session(type));
        Assert.Equal(32, matching.Config!.InputWidth);
        Assert.Equal(32, matching.Config.InputHeight);
        Assert.Equal(1, matching.Config.BatchSize);
    }

    [Fact]
    public void OneUnboundConfigurationCanBeUsedForDifferentModelSizes()
    {
        var config = AutoConfig(ModelType.YoloV5Detection);
        using var first = Create(ModelType.YoloV5Detection, new Session(ModelType.YoloV5Detection), config);
        using var second = Create(ModelType.YoloV5Detection, new Session(ModelType.YoloV5Detection, 64, 96), config);
        Assert.Equal(32, first.Config!.InputWidth);
        Assert.Equal(64, second.Config!.InputWidth);
        Assert.Equal(96, second.Config.InputHeight);
        Assert.Equal(0, config.InputWidth);
        Assert.NotSame(first.Config.V5Profile, second.Config.V5Profile);
    }

    [Theory]
    [InlineData(ChannelOrder.Bgr, ResizeMode.Letterbox, NormalizationMode.ModelDefault)]
    [InlineData(ChannelOrder.Rgb, ResizeMode.Stretch, NormalizationMode.ModelDefault)]
    [InlineData(ChannelOrder.Rgb, ResizeMode.Letterbox, NormalizationMode.None)]
    [InlineData(ChannelOrder.Rgb, ResizeMode.Letterbox, NormalizationMode.DivideBy255)]
    public void UnsupportedPreprocessingFailsBeforeCreatingANativeSession(ChannelOrder order, ResizeMode resize, NormalizationMode normalization)
    {
        var config = new RknnPipelineConfig(1, anchors: Config(ModelType.YoloV5Detection).Anchors)
        { ChannelOrder = order, ResizeMode = resize, Normalization = normalization };
        int creates = 0;
        var error = Assert.Throws<NotSupportedException>(() => new RknnPipeline(ModelType.YoloV5Detection, "x", config,
            null, null, (_, _) => { creates++; return new Session(ModelType.YoloV5Detection); }));
        Assert.Contains($"ChannelOrder={order}", error.Message);
        Assert.Contains($"ResizeMode={resize}", error.Message);
        Assert.Contains($"Normalization={normalization}", error.Message);
        Assert.Equal(0, creates);
    }

    [Theory]
    [InlineData(2, 32, 32, 3, 1, 2, 2, -128, false)]
    [InlineData(0, 32, 32, 3, 1, 2, 2, -128, false)]
    [InlineData(1, 0, 32, 3, 1, 2, 2, -128, false)]
    [InlineData(1, 32, 32, 1, 1, 2, 2, -128, false)]
    [InlineData(1, 32, 32, 3, 0, 2, 2, -128, false)]
    [InlineData(1, 32, 32, 3, 1, 1, 2, -128, false)]
    [InlineData(1, 32, 32, 3, 1, 2, 0, -128, false)]
    [InlineData(1, 32, 32, 3, 1, 2, 2, 0, false)]
    [InlineData(1, 32, 32, 3, 1, 2, 2, -128, true)]
    public void ModelDefaultStillRejectsUnsupportedNativeAttributes(int batch, int height, int width, int channels,
        int layout, int type, int quantization, int zeroPoint, bool approximateScale)
    {
        var input = new TensorInfo(0, "actual", [(uint)batch, (uint)height, (uint)width, (uint)channels],
            (uint)(batch * height * width * channels), layout, type, quantization, zeroPoint,
            approximateScale ? MathF.BitIncrement(1f / 255) : 1f / 255);
        var session = new Session(ModelType.YoloV5Detection) { Inputs = [input] };
        var error = Assert.Throws<ArgumentException>(() => Create(ModelType.YoloV5Detection, session, AutoConfig(ModelType.YoloV5Detection)));
        Assert.Contains(NativeInputContract.Describe(input), error.Message);
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, session.DetectCalls);
    }

    [Fact]
    public void RawTensorAlsoPublishesTheModelInputProperties()
    {
        var session = new Session(ModelType.RawTensor, 96, 64);
        using var pipeline = Create(ModelType.RawTensor, session);
        Assert.Equal(96, pipeline.InputWidth);
        Assert.Equal(64, pipeline.InputHeight);
        Assert.Equal(1, pipeline.BatchSize);
        Assert.Same(session.Inputs[0], pipeline.InputTensor);
        Assert.Null(pipeline.Config);
    }

    [Fact]
    public void UnknownPreprocessingEnumValuesAreRejectedBeforeSessionCreation()
    {
        var anchors = Config(ModelType.YoloV5Detection).Anchors;
        RknnPipelineConfig[] configs =
        [
            new(1, anchors: anchors) { ChannelOrder = (ChannelOrder)99 },
            new(1, anchors: anchors) { ResizeMode = (ResizeMode)99 },
            new(1, anchors: anchors) { Normalization = (NormalizationMode)99 }
        ];
        int creates = 0;
        foreach (var config in configs)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipeline(ModelType.YoloV5Detection, "x", config,
                null, null, (_, _) => { creates++; return new Session(ModelType.YoloV5Detection); }));
        }
        Assert.Equal(0, creates);
    }

    [Fact]
    public void BindingAnInvalidAutomaticDetectionContractDisposesTheSession()
    {
        var config = new RknnPipelineConfig(classCount: 1,
            heads: [new(0, 1, null, 8), new(2, 3, null, 16), new(4, 6, null, 32)]);
        var session = new Session(ModelType.YoloV8Detection);
        Assert.Throws<ArgumentException>(() => Create(ModelType.YoloV8Detection, session, config));
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, session.DetectCalls);
    }

    [Fact]
    public void InvalidEnumPathAndConfigurationNeverCreateASession()
    {
        int creates = 0;
        IRknnSession Factory(string _, RknnModelOptions __) { creates++; throw new InvalidOperationException(); }
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipeline((ModelType)99, "x", null, null, null, Factory));
        Assert.Throws<ArgumentException>(() => new RknnPipeline(ModelType.RawTensor, " ", null, null, null, Factory));
        Assert.Throws<ArgumentNullException>(() => new RknnPipeline(ModelType.YoloV5Detection, "x", null, null, null, Factory));
        Assert.Throws<ArgumentException>(() => new RknnPipeline(ModelType.YoloV8Detection, "x", Config(ModelType.YoloV5Detection), null, null, Factory));
        Assert.Throws<ArgumentException>(() => new RknnPipeline(ModelType.RawTensor, "x", Config(ModelType.YoloV5Detection), null, null, Factory));
        Assert.Equal(0, creates);
    }

    [Fact]
    public void ConfigRejectsMissingMixedInvalidAnchorsAndDflMappings()
    {
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1));
        var valid = Config(ModelType.YoloV5Detection);
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1, valid.Anchors, Config(ModelType.YoloV8Detection).Heads));
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1, anchors: [[new(float.NaN, 1)]]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipelineConfig(31, 32, 1, valid.Anchors));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipelineConfig(32, 32, 0, valid.Anchors));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipelineConfig(32, 32, 1, valid.Anchors, confidenceThreshold: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipelineConfig(32, 32, 1, valid.Anchors, nmsThreshold: 2));
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1, valid.Anchors, multiLabel: true));
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1, valid.Anchors, classNames: [" "]));
        var heads = Config(ModelType.YoloV8Detection).Heads.ToArray();
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnPipelineConfig(32, 32, 1, heads: heads, regMax: 1));
        heads[2] = new(4, 6, null, 32);
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1, heads: heads));
        heads[2] = new(4, 0, null, 32);
        Assert.Throws<ArgumentException>(() => new RknnPipelineConfig(32, 32, 1, heads: heads));
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection)]
    [InlineData(ModelType.YoloV8Detection)]
    public void SelectedContractDrivesPredictAndFloatDecode(ModelType type)
    {
        var session = new Session(type);
        using var pipeline = Create(type, session);
        DetectionResult predicted = pipeline.Predict(Frame);
        var detection = Assert.Single(predicted.Detections);
        Assert.Equal(0, detection.ClassId);
        Assert.True(detection.Confidence > .89f);
        Assert.Equal(type, session.LastContract);
        Assert.Equal(Timings, predicted.Timings);
        Assert.Equal(Transform, predicted.Transform);
        InferenceResult raw = pipeline.PredictRaw(Frame);
        Assert.Equal(predicted.Detections, pipeline.Decode(raw).Detections);
        Assert.Equal(1, session.DetectCalls);
        Assert.Same(session.Inputs, pipeline.Inputs);
        Assert.Same(session.Outputs, pipeline.Outputs);
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection, true)]
    [InlineData(ModelType.YoloV5Detection, false)]
    [InlineData(ModelType.YoloV8Detection, true)]
    [InlineData(ModelType.YoloV8Detection, false)]
    public void MetadataFailureDisposesCreatedSessionBeforeAnyPrediction(ModelType type, bool badInput)
    {
        var session = new Session(type);
        if (badInput) { session.Inputs = [new(0, "wrong", [1, 16, 16, 3], 768, 1, 2, 2, -128, 1f / 255)]; }
        else { session.Outputs = [new(0, "wrong", [1, 2], 2, 0, 2, 2, 0, 1)]; }
        Assert.Throws<ArgumentException>(() => Create(type, session));
        Assert.Equal(1, session.DisposeCalls);
        Assert.Equal(0, session.DetectCalls);
    }

    [Fact]
    public void RawTensorAcceptsNonDetectionOutputsAndRetainsCallerBufferAliasing()
    {
        var session = new Session(ModelType.RawTensor);
        using var pipeline = Create(ModelType.RawTensor, session);
        Assert.Null(pipeline.Config);
        Assert.Equal(new float[] { 2, 7 }, Assert.Single(pipeline.PredictRaw(Frame).Outputs).Values.ToArray());
        float[] storage = [-1, -1, 99];
        InferenceResult result = pipeline.PredictRawInto(Frame, [storage]);
        Assert.Equal(new float[] { 2, 7, 99 }, storage);
        storage[0] = 42;
        Assert.Equal(42, result.Outputs[0].Values.Span[0]);
        Assert.Throws<NotSupportedException>(() => pipeline.Predict(Frame));
        Assert.Throws<NotSupportedException>(() => pipeline.Decode(result));
        Assert.Equal(0, session.DetectCalls);
    }

    [Fact]
    public void OptionsAndLoggerUseTheUnifiedConstructionPath()
    {
        var options = new RknnModelOptions { CoreMask = NpuCoreMask.Core2, OutputMode = FloatOutputMode.PreferFastInt8 };
        var session = new Session(ModelType.RawTensor);
        var logger = new PlacementTests.CountingLogger(true);
        using var pipeline = new RknnPipeline(ModelType.RawTensor, "controlled", null, options, logger, (path, actual) =>
        {
            Assert.Equal("controlled", path);
            Assert.Same(options, actual);
            return session;
        });
        Assert.Equal(1, logger.Calls);
        var error = new InvalidOperationException("create failed");
        Assert.Same(error, Assert.Throws<InvalidOperationException>(() => new RknnPipeline(ModelType.RawTensor, "x", null,
            null, null, (_, _) => throw error)));
    }

    [Fact]
    public void LoggerFailureDuringConstructionReleasesTheSession()
    {
        var session = new Session(ModelType.RawTensor);
        Assert.Throws<InvalidOperationException>(() => Create(ModelType.RawTensor, session, logger: new ThrowingLogger()));
        Assert.Equal(1, session.DisposeCalls);
    }

    [Fact]
    public void FailedPredictionReleasesTheLockAndSessionRemainsUsable()
    {
        var session = new Session(ModelType.YoloV5Detection);
        using var pipeline = Create(ModelType.YoloV5Detection, session);
        session.BeforeRun = () => throw new InvalidOperationException("controlled inference failure");
        Assert.Throws<InvalidOperationException>(() => pipeline.Predict(Frame));
        session.BeforeRun = null;
        Assert.Single(pipeline.Predict(Frame).Detections);
        Assert.Equal(0, session.DisposeCalls);
    }

    [Fact]
    public void OwnedImageOverloadsRejectNullBeforeNativeAccess()
    {
        using var pipeline = Create(ModelType.RawTensor, new Session(ModelType.RawTensor));
        Assert.Throws<ArgumentNullException>(() => pipeline.Predict((DmaImage)null!));
        Assert.Throws<ArgumentNullException>(() => pipeline.PredictRaw((DmaImage)null!));
        Assert.Throws<ArgumentNullException>(() => pipeline.PredictRawInto((DmaImage)null!, []));
        Assert.Throws<ArgumentNullException>(() => pipeline.Decode(null!));
    }

    [Theory]
    [InlineData(ModelType.YoloV5Detection)]
    [InlineData(ModelType.YoloV8Detection)]
    public void ConfigCollectionsCannotBeChangedAfterConstruction(ModelType type)
    {
        var initial = Config(type);
        var anchors = initial.Anchors.Select(head => head.ToArray()).ToArray();
        var heads = initial.Heads.ToArray();
        string[] names = ["original"];
        var config = new RknnPipelineConfig(32, 32, 1, type == ModelType.YoloV5Detection ? anchors : null,
            type == ModelType.YoloV8Detection ? heads : null, regMax: type == ModelType.YoloV5Detection ? 16 : 2, classNames: names);
        var session = new Session(type);
        using var pipeline = Create(type, session, config);
        Detection[] expected = pipeline.Predict(Frame).Detections.ToArray();
        names[0] = "changed";
        if (anchors.Length > 0) { anchors[0][0] = new(8000, 8000); }
        if (heads.Length > 0) { heads[0] = new(99, 100, null, 8); }
        Assert.Equal("original", config.ClassNames[0]);
        Assert.Equal(expected, pipeline.Predict(Frame).Detections);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)config.ClassNames)[0] = "changed");
        if (anchors.Length > 0) { Assert.Throws<NotSupportedException>(() => ((IList<YoloAnchor>)config.Anchors[0])[0] = new(1, 1)); }
        if (heads.Length > 0) { Assert.Throws<NotSupportedException>(() => ((IList<YoloDetectionHead>)config.Heads)[0] = heads[0]); }
    }

    [Theory]
    [InlineData("predict")]
    [InlineData("raw")]
    [InlineData("into")]
    public async Task DisposeWaitsForActiveOperationAndRejectsAllLaterOperations(string operation)
    {
        var session = new Session(ModelType.YoloV5Detection);
        using var pipeline = Create(ModelType.YoloV5Detection, session);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        session.BeforeRun = () => { entered.SetResult(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); };
        var running = Task.Run(() =>
        {
            if (operation == "predict") { pipeline.Predict(Frame); }
            else if (operation == "raw") { pipeline.PredictRaw(Frame); }
            else { pipeline.PredictRawInto(Frame, session.Outputs.Select(info => (Memory<float>)new float[info.ElementCount]).ToArray()); }
        });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var dispose = Task.Run(() => { disposing.SetResult(); pipeline.Dispose(); });
            await disposing.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0, session.DisposeCalls);
            release.Set();
            await Task.WhenAll(running, dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, session.DisposeCalls);
            pipeline.Dispose();
            Assert.Equal(1, session.DisposeCalls);
            Assert.Throws<ObjectDisposedException>(() => pipeline.Predict(Frame));
            Assert.Throws<ObjectDisposedException>(() => pipeline.PredictRaw(Frame));
            Assert.Throws<ObjectDisposedException>(() => pipeline.PredictRawInto(Frame, []));
            Assert.Throws<ObjectDisposedException>(() => pipeline.Decode(session.Result));
        }
        finally { release.Set(); await running.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    private sealed class ThrowingLogger : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("controlled logger failure");
    }

    private sealed class Session : IRknnSession
    {
        public IReadOnlyList<TensorInfo> Inputs { get; set; } = [new(0, "images", [1, 32, 32, 3], 3072, 1, 2, 2, -128, 1f / 255)];
        public IReadOnlyList<TensorInfo> Outputs { get; set; }
        public string RuntimeVersions => "controlled";
        public NpuCoreMask CoreMask => NpuCoreMask.Auto;
        public FloatOutputMode OutputMode => FloatOutputMode.SdkExact;
        public CallerThreadPlacement? CallerThreadPlacement => null;
        public int DisposeCalls { get; private set; }
        public int DetectCalls { get; private set; }
        public ModelType? LastContract { get; private set; }
        public Action? BeforeRun { get; set; }
        public InferenceResult Result { get; }

        public Session(ModelType type, int width = 32, int height = 32)
        {
            Inputs = [new(0, "images", [1, (uint)height, (uint)width, 3], (uint)(width * height * 3), 1, 2, 2, -128, 1f / 255)];
            var outputs = new List<TensorOutput>();
            if (type == ModelType.RawTensor)
            { outputs.Add(new(new(0, "embedding", [1, 2], 2, 0, 0, 0, 0, 1), new float[] { 2, 7 })); }
            else
            {
                for (int stride = 8; stride <= 32; stride *= 2)
                {
                    int plane = width / stride * (height / stride);
                    if (type == ModelType.YoloV5Detection)
                    {
                        var values = Enumerable.Repeat(-20f, 18 * plane).ToArray();
                        if (stride == 8) { for (int channel = 0; channel < 6; channel++) { values[channel * plane] = channel < 4 ? 0 : 8; } }
                        Add(18, values);
                    }
                    else
                    {
                        Add(8, new float[8 * plane]);
                        var scores = new float[plane];
                        if (stride == 8) { scores[0] = .9f; }
                        Add(1, scores);
                    }
                    void Add(int channels, float[] values) => outputs.Add(new(new((uint)outputs.Count, "head", [1, (uint)channels, (uint)(height / stride), (uint)(width / stride)], (uint)values.Length, 0, 2, 2, 0, .1f), values));
                }
            }
            Outputs = outputs.Select(output => output.Info).ToArray();
            Result = new(outputs, Transform, Timings);
        }

        public InferenceResult Run(DmaFrame frame) { BeforeRun?.Invoke(); frame.Validate(); Assert.Equal(0, DisposeCalls); return Result; }
        public InferenceResult RunInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers)
        {
            Run(frame);
            Assert.Equal(Outputs.Count, outputBuffers.Count);
            var values = Result.Outputs.Select((output, index) =>
            {
                output.Values.CopyTo(outputBuffers[index]);
                return new TensorOutput(output.Info, outputBuffers[index][..output.Values.Length]);
            }).ToArray();
            return new(values, Transform, Timings);
        }
        public DetectionResult Detect(DmaFrame frame, YoloV5Profile profile)
        {
            Run(frame); DetectCalls++; LastContract = ModelType.YoloV5Detection;
            return new(profile.Decode(Result.Outputs, Transform), Transform, Timings, 0);
        }
        public DetectionResult Detect(DmaFrame frame, YoloDetectionProfile profile)
        {
            Run(frame); DetectCalls++; LastContract = ModelType.YoloV8Detection;
            return new(profile.Decode(Result.Outputs, Transform), Transform, Timings, 0);
        }
        public void Dispose() { DisposeCalls++; }
    }
}
