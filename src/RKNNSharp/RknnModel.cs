using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RKNNSharp;

/// <summary>同步 RKNN 会话. 同一实例的 Run 和 Dispose 互斥; 不提供软件图像处理回退.</summary>
public sealed class RknnModel : IDisposable
{
    private readonly object _gate = new();
    private readonly SessionHandle _handle;
    private readonly bool _supportsDirectFloat;
    private readonly ILogger _logger;
    private readonly PlacementState _placementState;
    public CallerThreadPlacement? CallerThreadPlacement { get; }
    public IReadOnlyList<TensorInfo> Inputs { get; }
    public IReadOnlyList<TensorInfo> Outputs { get; }
    public string RuntimeVersions { get; }
    public NpuCoreMask CoreMask { get; }
    /// <summary>请求的浮点输出模式. PreferFastInt8只对满足原生条件的head启用快速转换, 其他head仍由SDK转换.</summary>
    public FloatOutputMode OutputMode { get; }

    /// <summary>从本地 RKNN 文件创建 Linux ARM64 会话, 验证原生 ABI/输入契约并显式配置 core policy.</summary>
    /// <param name="modelPath">模型文件路径. 当前已验证模型见 docs/model-contract.md.</param>
    /// <param name="coreMask">可选核心策略. Auto 为默认; 多核 mask 不保证缩短单次推理.</param>
    public unsafe RknnModel(string modelPath, NpuCoreMask coreMask = NpuCoreMask.Auto)
        : this(modelPath, coreMask, null, FloatOutputMode.SdkExact) { }

    /// <summary>加载前可选校验模型主文件 SHA256. null 完全跳过hash读取; 校验仅发生在创建会话前.</summary>
    /// <param name="modelPath">模型文件路径.</param>
    /// <param name="coreMask">显式核心策略.</param>
    /// <param name="expectedModelSha256">null 或64位十六进制摘要, 大小写均接受. 非签名或原子加载, 不证明anchors/精度.</param>
    public unsafe RknnModel(string modelPath, NpuCoreMask coreMask, string? expectedModelSha256)
        : this(modelPath, coreMask, expectedModelSha256, FloatOutputMode.SdkExact) { }

    /// <summary>加载前可选校验模型SHA256, 并显式选择浮点输出模式. 显式快速模式需要支持配置导出的原生库.</summary>
    public unsafe RknnModel(string modelPath, NpuCoreMask coreMask, string? expectedModelSha256, FloatOutputMode outputMode)
        : this(modelPath, coreMask, expectedModelSha256, outputMode, null, null) { }

    /// <summary>使用可选调用线程调度与日志配置创建会话.</summary>
    public RknnModel(string modelPath, RknnModelOptions options)
        : this(modelPath, (options ?? throw new ArgumentNullException(nameof(options))).CoreMask,
            options.ExpectedModelSha256, options.OutputMode, options.CallerThreadPlacement, options.LoggerFactory) { }

    private unsafe RknnModel(string modelPath, NpuCoreMask coreMask, string? expectedModelSha256,
        FloatOutputMode outputMode, CallerThreadPlacement? placement, ILoggerFactory? loggerFactory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!Enum.IsDefined(coreMask)) { throw new ArgumentOutOfRangeException(nameof(coreMask)); }
        if (!Enum.IsDefined(outputMode)) { throw new ArgumentOutOfRangeException(nameof(outputMode)); }
        if (placement is not null)
        {
            bool linux = OperatingSystem.IsLinux();
            placement.Validate(linux, linux && placement.CpuMask.HasValue ? File.ReadAllText("/sys/devices/system/cpu/online") : null);
        }
        CallerThreadPlacement = placement;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RknnModel>();
        _placementState = new PlacementState(placement, _logger);
        ModelFileIntegrity.Verify(modelPath, expectedModelSha256);
        if (!File.Exists(modelPath)) { throw new FileNotFoundException("RKNN model was not found.", modelPath); }
        NativeMethods.EnsurePlatform();
        NativeMethods.Check(NativeMethods.rks_create(modelPath, out nint pointer));
        _handle = new SessionHandle(pointer);
        try
        {
            NativeMethods.Check(NativeMethods.rks_set_core_mask(_handle, (uint)coreMask));
            CoreMask = coreMask;
            if (outputMode == FloatOutputMode.PreferFastInt8)
            {
                try { NativeMethods.Check(NativeMethods.rks_set_float_output_mode(_handle, (uint)outputMode)); }
                catch (EntryPointNotFoundException exception)
                {
                    throw new NotSupportedException("PreferFastInt8 requires a native library exporting rks_set_float_output_mode; use SdkExact or update the matching native library.", exception);
                }
            }
            OutputMode = outputMode;
            NativeMethods.Check(NativeMethods.rks_get_tensor_count(_handle, out uint inputs, out uint outputs));
            Inputs = ReadTensors(false, inputs);
            Outputs = ReadTensors(true, outputs);
            _supportsDirectFloat = NativeMethods.SupportsDirectFloat;
            byte* versions = stackalloc byte[2048];
            NativeMethods.Check(NativeMethods.rks_get_versions(_handle, versions, 2048));
            RuntimeVersions = Encoding.UTF8.GetString(MemoryMarshalBytes(versions, 2048));
            if (_logger.IsEnabled(LogLevel.Information))
            {
                Log.SessionCreated(_logger, coreMask, outputMode);
                if (placement is not null) { Log.PlacementEnabled(_logger, placement.CpuMask, placement.UtilClampMin); }
            }
            if (_logger.IsEnabled(LogLevel.Debug)) { Log.DirectFloat(_logger, _supportsDirectFloat); }
        }
        catch { _handle.Dispose(); throw; }
    }

    private static unsafe ReadOnlySpan<byte> MemoryMarshalBytes(byte* value, int capacity)
    {
        int length = 0;
        while (length < capacity && value[length] != 0) { length++; }
        return new ReadOnlySpan<byte>(value, length);
    }

    private IReadOnlyList<TensorInfo> ReadTensors(bool output, uint count)
    {
        if (count is 0 or > 64) { throw new RknnException("Unsupported native tensor count."); }
        var tensors = new TensorInfo[count];
        for (uint i = 0; i < count; i++)
        {
            NativeMethods.Check(NativeMethods.rks_get_tensor(_handle, output ? 1u : 0u, i, out var tensor));
            tensors[i] = tensor.ToInfo();
        }
        return Array.AsReadOnly(tensors);
    }

    private PlacementScope EnterPlacement() => PlacementScope.Enter(_placementState);

    /// <summary>借用输入直至方法返回. 调用方必须同步生产者, 并防止 FD/sample 关闭、复用或写入.</summary>
    public unsafe InferenceResult Run(DmaFrame frame)
    {
        frame.Validate();
        lock (_gate)
        {
            return RunCore(frame);
        }
    }

    private unsafe InferenceResult RunCore(DmaFrame frame)
    {
        ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
        if (_supportsDirectFloat) { return RunDirectFloat(frame); }
        using var placement = EnterPlacement();
        NativeMethods.Check(NativeMethods.rks_run(_handle, in frame, out var info));
        var transform = NativeLetterboxGeometry.Create(frame, Inputs[0], in info);
        long managedCopyStart = Stopwatch.GetTimestamp();
        var values = new TensorOutput[Outputs.Count];
        for (int i = 0; i < values.Length; i++)
        {
            // Native validates the exact count and overwrites every element; publish only after all heads succeed.
            var buffer = GC.AllocateUninitializedArray<float>(checked((int)Outputs[i].ElementCount));
            fixed (float* destination = buffer)
            {
                NativeMethods.Check(NativeMethods.rks_copy_output(_handle, (uint)i, destination, (uint)buffer.Length));
            }
            values[i] = new TensorOutput(Outputs[i], buffer);
        }
        return new InferenceResult(Array.AsReadOnly(values), transform,
            new RunTimings(info.PreprocessMs, info.InferenceMs, info.OutputsMs, Stopwatch.GetElapsedTime(managedCopyStart).TotalMilliseconds));
    }

    private unsafe InferenceResult RunDirectFloat(DmaFrame frame)
    {
        using var placement = EnterPlacement();
        long managedStart = Stopwatch.GetTimestamp();
        var values = new TensorOutput[Outputs.Count];
        var pins = new GCHandle[Outputs.Count];
        float** destinations = stackalloc float*[Outputs.Count];
        uint* counts = stackalloc uint[Outputs.Count];
        NativeRunInfo info = default;
        double managedMs = 0;
        try
        {
            for (int i = 0; i < values.Length; i++)
            {
                counts[i] = Outputs[i].ElementCount;
                var buffer = GC.AllocateUninitializedArray<float>(checked((int)counts[i]));
                values[i] = new TensorOutput(Outputs[i], buffer);
                pins[i] = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                destinations[i] = (float*)pins[i].AddrOfPinnedObject();
            }
            managedMs = Stopwatch.GetElapsedTime(managedStart).TotalMilliseconds;
            // Native validates exact counts and writes every head synchronously. Failed/partial writes are never published.
            NativeMethods.Check(NativeMethods.rks_run_float_into(_handle, in frame, destinations, counts,
                (uint)values.Length, out info));
        }
        finally
        {
            long releaseStart = Stopwatch.GetTimestamp();
            for (int i = 0; i < pins.Length; i++) { if (pins[i].IsAllocated) { pins[i].Free(); } }
            managedMs += Stopwatch.GetElapsedTime(releaseStart).TotalMilliseconds;
        }
        var transform = NativeLetterboxGeometry.Create(frame, Inputs[0], in info);
        return new InferenceResult(Array.AsReadOnly(values), transform,
            new RunTimings(info.PreprocessMs, info.InferenceMs, info.OutputsMs, managedMs));
    }

    /// <summary>运行期间持有 owned DMA 图像, Dispose 等待硬件访问结束.</summary>
    public InferenceResult Run(DmaImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(Run);
    }

    /// <summary>同步推理到调用方可复用的 float 缓冲区. 返回 Values 引用各缓冲区有效段, 下次复用会覆盖旧结果.</summary>
    /// <param name="frame">借用至返回的已就绪 DMA 帧.</param>
    /// <param name="outputBuffers">head 数须精确匹配, 每头长度至少为 ElementCount. 有效段不得重叠; 未用尾部不写入.</param>
    /// <remarks>
    /// 调用方拥有缓冲区, 必须支持 Pin 且在调用期间独占/保持存活, 包括跨模型使用.
    /// 重叠检查以固定后的实际地址为准; 调用方不得提供隐蔽物理别名, 自定义 MemoryManager 的 Pin/GetSpan 必须一致.
    /// 每头只捕获一次; 返回 Values 长度精确等于模型 ElementCount. 消费完结果后才可复用、归还池或释放 owner.
    /// 任意失败时不返回结果, 调用方应将全部输出视为不完整. Run 的独立快照行为不变.
    /// </remarks>
    public unsafe InferenceResult RunInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers)
    {
        ArgumentNullException.ThrowIfNull(outputBuffers);
        frame.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var placement = EnterPlacement();
            long managedStart = Stopwatch.GetTimestamp();
            var pinned = new PinnedRunOutputs(Outputs, outputBuffers);
            double managedMs = Stopwatch.GetElapsedTime(managedStart).TotalMilliseconds;
            NativeRunInfo info = default;
            try
            {
                if (_supportsDirectFloat)
                {
                    fixed (nint* destinations = pinned.Addresses)
                    fixed (uint* counts = pinned.Counts)
                    {
                        NativeMethods.Check(NativeMethods.rks_run_float_into(_handle, in frame, (float**)destinations,
                            counts, (uint)pinned.Counts.Length, out info));
                    }
                }
                else
                {
                    // Old ABI2 libraries keep the same preflight and lifetime contract before any hardware work.
                    NativeMethods.Check(NativeMethods.rks_run(_handle, in frame, out info));
                    long copyStart = Stopwatch.GetTimestamp();
                    for (int i = 0; i < pinned.Outputs.Length; i++)
                    {
                        NativeMethods.Check(NativeMethods.rks_copy_output(_handle, (uint)i,
                            (float*)pinned.Addresses[i], pinned.Counts[i]));
                    }
                    managedMs += Stopwatch.GetElapsedTime(copyStart).TotalMilliseconds;
                }
            }
            finally
            {
                long releaseStart = Stopwatch.GetTimestamp();
                pinned.Dispose();
                managedMs += Stopwatch.GetElapsedTime(releaseStart).TotalMilliseconds;
            }
            var transform = NativeLetterboxGeometry.Create(frame, Inputs[0], in info);
            return new InferenceResult(Array.AsReadOnly(pinned.Outputs), transform,
                new RunTimings(info.PreprocessMs, info.InferenceMs, info.OutputsMs, managedMs));
        }
    }

    /// <summary>同步借用 owned DMA 图像运行到调用方缓冲区; 缓冲区复用/所有权规则与 DmaFrame 重载相同.</summary>
    public InferenceResult RunInto(DmaImage image, IReadOnlyList<Memory<float>> outputBuffers)
    {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(outputBuffers);
        return image.WithFrame(frame => RunInto(frame, outputBuffers));
    }

    /// <summary>同步运行 INT8 检测快路径. 输入 FD/sample 借用至返回, 原生输出不逃逸会话锁, 返回独立检测结果.</summary>
    /// <param name="frame">生产者已完成写入的只读 DMA 描述符, NV12 必须指定准确的颜色空间.</param>
    /// <param name="profile">显式的三头 raw-logit YOLOv5 anchors/类别数/阈值配置.</param>
    public unsafe DetectionResult Detect(DmaFrame frame, YoloV5Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.ValidateModel(Inputs, Outputs);
        frame.Validate();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var placement = EnterPlacement();
            bool added = false;
            try
            {
                // Keep native vectors alive throughout the complete borrowed-pointer decode.
                // No pointer or tensor buffer escapes this lock/handle lifetime.
                _handle.DangerousAddRef(ref added);
                NativeMethods.Check(NativeMethods.rks_run_int8(_handle, in frame, out var info));
                var transform = NativeLetterboxGeometry.Create(frame, Inputs[0], in info);
                long borrowStart = Stopwatch.GetTimestamp();
                var head0 = BorrowOutput(0, out ulong generation0);
                var head1 = BorrowOutput(1, out ulong generation1);
                var head2 = BorrowOutput(2, out ulong generation2);
                if (generation0 != generation1 || generation0 != generation2)
                {
                    throw new RknnException("Native output generation mismatch.");
                }
                double borrowMs = Stopwatch.GetElapsedTime(borrowStart).TotalMilliseconds;
                long postprocessStart = Stopwatch.GetTimestamp();
                var detections = profile.DecodeInt8(Outputs, head0, head1, head2, transform);
                return new DetectionResult(detections, transform,
                    new RunTimings(info.PreprocessMs, info.InferenceMs, info.OutputsMs, borrowMs),
                    Stopwatch.GetElapsedTime(postprocessStart).TotalMilliseconds);
            }
            finally { if (added) { _handle.DangerousRelease(); } }
        }
    }

    private unsafe ReadOnlySpan<sbyte> BorrowOutput(uint index, out ulong generation)
    {
        NativeMethods.Check(NativeMethods.rks_borrow_output_int8(_handle, index, out nint pointer, out uint count, out generation));
        if (pointer == 0 || count != Outputs[(int)index].ElementCount || count > int.MaxValue)
        {
            throw new RknnException("Native INT8 output pointer/element count mismatch.");
        }
        return new ReadOnlySpan<sbyte>((void*)pointer, (int)count);
    }

    /// <summary>运行期间持有 owned DMA 图像; Dispose 等待同步检测完成.</summary>
    public DetectionResult Detect(DmaImage image, YoloV5Profile profile)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(frame => Detect(frame, profile));
    }

    /// <summary>等待本会话当前操作结束后释放原生资源, 可重复调用.</summary>
    public void Dispose() { lock (_gate) { _handle.Dispose(); } }
}
