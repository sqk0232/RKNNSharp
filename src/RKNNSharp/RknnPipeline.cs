using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RKNNSharp;

/// <summary>通过枚举选择检测契约的统一入口. 同一实例的预测、解码与释放互斥; 输入仅同步借用至返回.</summary>
public sealed class RknnPipeline : IDisposable
{
    private readonly object _gate = new();
    private readonly IRknnSession _session;
    private readonly YoloV5Profile? _v5;
    private readonly YoloDetectionProfile? _dfl;
    private bool _disposed;
    public ModelType ModelType { get; }
    public RknnPipelineConfig? Config { get; }
    public IReadOnlyList<TensorInfo> Inputs => _session.Inputs;
    public IReadOnlyList<TensorInfo> Outputs => _session.Outputs;
    public string RuntimeVersions => _session.RuntimeVersions;
    public NpuCoreMask CoreMask => _session.CoreMask;
    public FloatOutputMode OutputMode => _session.OutputMode;
    public CallerThreadPlacement? CallerThreadPlacement => _session.CallerThreadPlacement;

    /// <summary>先验证枚举、配置与基础选项, 加载后验证 metadata. 构造失败会释放已创建的会话.</summary>
    /// <param name="modelType">实际导出契约, 不按模型文件名推断.</param>
    /// <param name="modelPath">本地 RKNN 文件路径.</param>
    /// <param name="config">检测模式必须提供匹配的不可变配置; RawTensor 必须省略.</param>
    /// <param name="options">可选核心、SHA256、输出与调用线程配置.</param>
    /// <param name="logger">可选日志, 默认 NullLogger. 调用方拥有 logger 与 options.LoggerFactory 的生命周期.</param>
    public RknnPipeline(ModelType modelType, string modelPath, RknnPipelineConfig? config = null,
        RknnModelOptions? options = null, ILogger? logger = null)
        : this(modelType, modelPath, config, options, logger, static (path, settings) => new RknnModel(path, settings)) { }

    internal RknnPipeline(ModelType modelType, string modelPath, RknnPipelineConfig? config,
        RknnModelOptions? options, ILogger? logger, Func<string, RknnModelOptions, IRknnSession> createSession)
    {
        if (!Enum.IsDefined(modelType)) { throw new ArgumentOutOfRangeException(nameof(modelType)); }
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        ArgumentNullException.ThrowIfNull(createSession);
        if (modelType == ModelType.RawTensor)
        {
            if (config is not null) { throw new ArgumentException("RawTensor does not accept detection configuration.", nameof(config)); }
        }
        else
        {
            ArgumentNullException.ThrowIfNull(config);
            if (config.Contract != modelType) { throw new ArgumentException("Configuration does not match the selected model contract.", nameof(config)); }
        }
        ModelType = modelType; Config = config; _v5 = config?.V5Profile; _dfl = config?.DflProfile;
        _session = createSession(modelPath, options ?? new RknnModelOptions());
        try
        {
            switch (modelType)
            {
                case ModelType.YoloV5Detection: _v5!.ValidateModel(Inputs, Outputs); break;
                case ModelType.YoloV8Detection: _dfl!.ValidateModel(Inputs, Outputs); break;
                case ModelType.RawTensor: break;
                default: throw new ArgumentOutOfRangeException(nameof(modelType));
            }
            Log.PipelineCreated(logger ?? NullLogger.Instance, modelType);
        }
        catch { _session.Dispose(); throw; }
    }

    /// <summary>同步 INT8 检测. FD/sample 必须就绪且保持存活, 返回独立检测结果.</summary>
    public DetectionResult Predict(DmaFrame frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return ModelType switch
            {
                ModelType.YoloV5Detection => _session.Detect(frame, _v5!),
                ModelType.YoloV8Detection => _session.Detect(frame, _dfl!),
                ModelType.RawTensor => throw new NotSupportedException("RawTensor has no detection decoder. Use PredictRaw or PredictRawInto."),
                _ => throw new InvalidOperationException("Unsupported model contract.")
            };
        }
    }

    /// <summary>保持 owned DMA 图像存活至同步检测结束.</summary>
    public DetectionResult Predict(DmaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(Predict);
    }

    /// <summary>返回独立 float 张量快照. 默认使用 SDK 浮点转换; 高频调用可用 PredictRawInto 复用缓冲区.</summary>
    public InferenceResult PredictRaw(DmaFrame frame)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _session.Run(frame); }
    }

    /// <summary>保持 owned DMA 图像存活至原始张量快照完成.</summary>
    public InferenceResult PredictRaw(DmaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(PredictRaw);
    }

    /// <summary>写入调用方独占、可 Pin 的不重叠缓冲区. 每头长度至少为 ElementCount; 返回有效段在下次复用时覆盖.</summary>
    /// <remarks>任意失败后全部输出均视为不完整. 消费结果后才可复用、归还池或释放 owner; 自定义 MemoryManager 的 Pin/GetSpan 必须一致.</remarks>
    public InferenceResult PredictRawInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers)
    {
        lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _session.RunInto(frame, outputBuffers); }
    }

    /// <summary>保持 owned DMA 图像存活至写入调用方输出缓冲区完成.</summary>
    public InferenceResult PredictRawInto(DmaImage image, IReadOnlyList<Memory<float>> outputBuffers)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(frame => PredictRawInto(frame, outputBuffers));
    }

    /// <summary>按构造时选定契约解码 float 输出, 可用于 SDK 浮点参考路径; 不再次访问硬件.</summary>
    public DetectionResult Decode(InferenceResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long start = Stopwatch.GetTimestamp();
            var detections = ModelType switch
            {
                ModelType.YoloV5Detection => _v5!.Decode(result.Outputs, result.Transform),
                ModelType.YoloV8Detection => _dfl!.Decode(result.Outputs, result.Transform),
                ModelType.RawTensor => throw new NotSupportedException("RawTensor has no detection decoder."),
                _ => throw new InvalidOperationException("Unsupported model contract.")
            };
            return new DetectionResult(detections, result.Transform, result.Timings, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
        }
    }

    /// <summary>等待当前调用结束后释放会话; 可重复调用.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) { return; }
            _disposed = true;
            _session.Dispose();
        }
    }
}
