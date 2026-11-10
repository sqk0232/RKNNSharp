using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RKNNSharp;

/// <summary>构造时建立固定会话池. 同步推理独占借用一个会话, 耗尽时等待归还.</summary>
public sealed class RknnModel : IRknnSession
{
    private readonly object _gate = new();
    private readonly List<IRknnSession> _sessions = [];
    private readonly Queue<IRknnSession> _available = new();
    private readonly IReadOnlyList<uint> _defaultInputShape;
    private bool _disposing;
    private bool _disposed;
    public int SessionPoolSize => _sessions.Count;
    /// <summary>构造时读取的输入属性. 每次动态调用的实际属性随结果返回, 不回写共享 metadata.</summary>
    public IReadOnlyList<TensorInfo> Inputs { get; }
    public IReadOnlyList<TensorInfo> Outputs { get; }
    public TensorInfo InputTensor => Inputs[0];
    public int InputWidth => InputTensor.Dimensions.Count == 4 ? checked((int)InputTensor.Dimensions[InputTensor.Layout == 0 ? 3 : 2]) : 0;
    public int InputHeight => InputTensor.Dimensions.Count == 4 ? checked((int)InputTensor.Dimensions[InputTensor.Layout == 0 ? 2 : 1]) : 0;
    public int BatchSize => checked((int)InputTensor.Dimensions[0]);
    public string RuntimeVersions { get; }
    public NpuCoreMask CoreMask { get; }
    public FloatOutputMode OutputMode { get; }
    public CallerThreadPlacement? CallerThreadPlacement { get; }
    public bool IsDynamic { get; }

    public RknnModel(string modelPath) : this(modelPath, new RknnModelOptions()) { }
    public RknnModel(string modelPath, RknnModelOptions options)
        : this(modelPath, options, static (path, settings) => new NativeRknnSession(path, settings)) { }

    internal RknnModel(string modelPath, RknnModelOptions options,
        Func<string, RknnModelOptions, IRknnSession> createSession)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(createSession);
        if (options.SessionPoolSize < 1) { throw new ArgumentOutOfRangeException(nameof(options.SessionPoolSize)); }
        ILogger logger = (options.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RknnModel>();
        try
        {
            var primary = createSession(modelPath, options);
            _sessions.Add(primary);
            bool duplicateAvailable = true;
            for (int i = 1; i < options.SessionPoolSize; i++)
            {
                IRknnSession session;
                if (duplicateAvailable)
                {
                    try { session = primary.Duplicate(); }
                    catch (EntryPointNotFoundException)
                    {
                        duplicateAvailable = false;
                        if (logger.IsEnabled(LogLevel.Warning)) { Log.UnsharedPool(logger, options.SessionPoolSize - i); }
                        session = createSession(modelPath, options);
                    }
                }
                else { session = createSession(modelPath, options); }
                _sessions.Add(session);
            }
            Inputs = primary.Inputs;
            Outputs = primary.Outputs;
            RuntimeVersions = primary.RuntimeVersions;
            CoreMask = primary.CoreMask;
            OutputMode = primary.OutputMode;
            CallerThreadPlacement = primary.CallerThreadPlacement;
            IsDynamic = primary.IsDynamic;
            _defaultInputShape = IsDynamic && InputTensor.Layout == 0 && InputTensor.Dimensions.Count == 4
                ? Array.AsReadOnly(new[] { InputTensor.Dimensions[0], InputTensor.Dimensions[2], InputTensor.Dimensions[3], InputTensor.Dimensions[1] })
                : InputTensor.Dimensions;
            foreach (var session in _sessions) { _available.Enqueue(session); }
        }
        catch
        {
            for (int i = _sessions.Count - 1; i >= 0; i--) { _sessions[i].Dispose(); }
            throw;
        }
    }

    internal T Use<T>(Func<IRknnSession, T> operation, IReadOnlyList<uint>? inputShape = null)
    {
        // Snapshot caller-owned dimensions before waiting. Buffers and native output
        // pointers remain private to this lease through copying and decoding.
        uint[]? shape = inputShape?.ToArray();
        IRknnSession session;
        lock (_gate)
        {
            while (_available.Count == 0 && !_disposing) { Monitor.Wait(_gate); }
            ObjectDisposedException.ThrowIf(_disposing, this);
            session = _available.Dequeue();
        }
        try
        {
            var target = shape ?? _defaultInputShape;
            if (session.IsDynamic || !target.SequenceEqual(session.Inputs[0].Dimensions)) { session.SetInputShape(target); }
            NativeInputContract.Validate(session.Inputs);
            return operation(session);
        }
        finally
        {
            lock (_gate) { _available.Enqueue(session); Monitor.PulseAll(_gate); }
        }
    }

    /// <summary>返回独立 float 输出快照. 输入 DMA 由调用方保持存活且就绪至返回.</summary>
    public InferenceResult Run(DmaFrame frame)
    {
        frame.Validate();
        return Use(session => session.Run(frame) with { Inputs = session.Inputs });
    }
    /// <summary>在已构造的池槽上设置 NHWC 形状并运行. 不创建或销毁上下文.</summary>
    public InferenceResult Run(DmaFrame frame, IReadOnlyList<uint> inputShape)
    {
        ArgumentNullException.ThrowIfNull(inputShape);
        frame.Validate();
        return Use(session => session.Run(frame) with { Inputs = session.Inputs }, inputShape);
    }
    public InferenceResult Run(DmaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(Run);
    }
    public InferenceResult Run(DmaImage image, IReadOnlyList<uint> inputShape)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(frame => Run(frame, inputShape));
    }
    /// <summary>调用方独占可 Pin 的不重叠输出缓冲区直至返回. 结果有效段在缓冲区复用时覆盖.</summary>
    /// <remarks>任意失败后丢弃全部输出. 消费结果后才能复用、归还缓冲区或释放 owner.</remarks>
    public InferenceResult RunInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers)
    {
        ArgumentNullException.ThrowIfNull(outputBuffers);
        frame.Validate();
        return Use(session => session.RunInto(frame, outputBuffers) with { Inputs = session.Inputs });
    }
    public InferenceResult RunInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers, IReadOnlyList<uint> inputShape)
    {
        ArgumentNullException.ThrowIfNull(inputShape);
        ArgumentNullException.ThrowIfNull(outputBuffers);
        frame.Validate();
        return Use(session => session.RunInto(frame, outputBuffers) with { Inputs = session.Inputs }, inputShape);
    }
    public InferenceResult RunInto(DmaImage image, IReadOnlyList<Memory<float>> outputBuffers)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(outputBuffers);
        return image.WithFrame(frame => RunInto(frame, outputBuffers));
    }
    public InferenceResult RunInto(DmaImage image, IReadOnlyList<Memory<float>> outputBuffers, IReadOnlyList<uint> inputShape)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(outputBuffers);
        return image.WithFrame(frame => RunInto(frame, outputBuffers, inputShape));
    }
    internal DetectionResult Detect(DmaFrame frame, YoloV5Profile profile) => Use(session => session.Detect(frame, profile));
    internal DetectionResult Detect(DmaImage image, YoloV5Profile profile) => image.WithFrame(frame => Detect(frame, profile));
    internal DetectionResult Detect(DmaFrame frame, YoloDetectionProfile profile) => Use(session => session.Detect(frame, profile));
    internal DetectionResult Detect(DmaImage image, YoloDetectionProfile profile) => image.WithFrame(frame => Detect(frame, profile));
    DetectionResult IRknnSession.Detect(DmaFrame frame, YoloV5Profile profile) => Detect(frame, profile);
    DetectionResult IRknnSession.Detect(DmaFrame frame, YoloDetectionProfile profile) => Detect(frame, profile);

    /// <summary>拒绝新借用并等待所有在途调用归还, 然后逐个释放全部上下文. 可重复调用.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposing)
            {
                while (!_disposed) { Monitor.Wait(_gate); }
                return;
            }
            _disposing = true;
            Monitor.PulseAll(_gate);
            while (_available.Count != _sessions.Count) { Monitor.Wait(_gate); }
        }
        try
        {
            // Release duplicates before the primary weight owner.
            for (int i = _sessions.Count - 1; i >= 0; i--) { _sessions[i].Dispose(); }
        }
        finally { lock (_gate) { _disposed = true; Monitor.PulseAll(_gate); } }
    }
}
