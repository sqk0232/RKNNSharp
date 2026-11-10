using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace RKNNSharp;

/// <summary>同步 RKNN 会话. 同一实例的 Run 和 Dispose 互斥; 不提供软件图像处理回退.</summary>
internal sealed class NativeRknnSession : IRknnSession
{
    private readonly object _gate = new();
    private readonly string _modelPath = string.Empty;
    private readonly RknnModelOptions _options = new();
    // Metadata belongs to this leased context. Shape changes replace its snapshots
    // and invalidate the last successfully validated profile of each type.
    private YoloV5Profile? _validatedV5Profile;
    private YoloDetectionProfile? _validatedDflProfile;
    private readonly SessionHandle _handle;
    private readonly bool _supportsDirectFloat;
    private readonly ILogger _logger;
    private readonly PlacementState _placementState;
    private bool _shapeReady;
    public CallerThreadPlacement? CallerThreadPlacement { get; }
    public bool IsDynamic { get; }
    public IReadOnlyList<TensorInfo> Inputs { get; private set; }
    public IReadOnlyList<TensorInfo> Outputs { get; private set; }
    public TensorInfo InputTensor { get; private set; }
    public int InputWidth => checked((int)InputTensor.Dimensions[2]);
    public int InputHeight => checked((int)InputTensor.Dimensions[1]);
    public int BatchSize => checked((int)InputTensor.Dimensions[0]);
    public string RuntimeVersions { get; }
    public NpuCoreMask CoreMask { get; }
    /// <summary>请求的浮点输出模式. PreferFastInt8只对满足原生条件的head启用快速转换, 其他head仍由SDK转换.</summary>
    public FloatOutputMode OutputMode { get; }

    /// <summary>使用可选调用线程调度与日志配置创建会话.</summary>
    internal NativeRknnSession(string modelPath) : this(modelPath, new RknnModelOptions()) { }

    internal NativeRknnSession(string modelPath, RknnModelOptions options, SessionHandle? source = null)
        : this(modelPath, (options ?? throw new ArgumentNullException(nameof(options))).CoreMask,
            options.ExpectedModelSha256, options.OutputMode, options.CallerThreadPlacement, options.LoggerFactory, source) { _modelPath = modelPath; _options = options; }

    private unsafe NativeRknnSession(string modelPath, NpuCoreMask coreMask, string? expectedModelSha256,
        FloatOutputMode outputMode, CallerThreadPlacement? placement, ILoggerFactory? loggerFactory, SessionHandle? source = null)
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
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<NativeRknnSession>();
        _placementState = new PlacementState(placement, _logger);
        ModelFileIntegrity.Verify(modelPath, expectedModelSha256);
        if (!File.Exists(modelPath)) { throw new FileNotFoundException("RKNN model was not found.", modelPath); }
        NativeMethods.EnsurePlatform();
        nint pointer;
        NativeMethods.Check(source is null ? NativeMethods.rks_create(modelPath, out pointer) : NativeMethods.rks_dup_context(source, out pointer));
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
            InputTensor = Inputs[0];
            try { IsDynamic = NativeMethods.rks_is_dynamic(_handle) != 0; }
            catch (EntryPointNotFoundException) { IsDynamic = false; }
            _shapeReady = !IsDynamic;
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

    public IRknnSession Duplicate() => new NativeRknnSession(_modelPath, _options, _handle);

    public unsafe void SetInputShape(IReadOnlyList<uint> dimensions)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            if (_shapeReady && dimensions.SequenceEqual(InputTensor.Dimensions)) { return; }
            if (dimensions.Count != 4 || dimensions.Any(value => value == 0 || value > int.MaxValue))
            { throw new ArgumentException("Input shape must contain four positive dimensions. Actual input " + NativeInputContract.Describe(InputTensor), nameof(dimensions)); }
            var shape = new NativeTensor { Index = 0, DimensionCount = 4 };
            for (int i = 0; i < dimensions.Count; i++) { shape.Dimensions[i] = dimensions[i]; }
            // A failed shape change may have changed the vendor context. Refresh metadata
            // before it can be borrowed again, and invalidate cached detection validation.
            _validatedV5Profile = null;
            _validatedDflProfile = null;
            _shapeReady = false;
            try
            {
                try { NativeMethods.Check(NativeMethods.rks_set_input_shapes(_handle, 1, &shape)); }
                catch (EntryPointNotFoundException exception)
                {
                    throw new NotSupportedException("Changing input shape requires rks_set_input_shapes. Actual input " +
                        NativeInputContract.Describe(InputTensor), exception);
                }
            }
            finally
            {
                Inputs = ReadTensors(false, 1);
                InputTensor = Inputs[0];
                Outputs = ReadTensors(true, (uint)Outputs.Count);
            }
            _shapeReady = true;
        }
    }

    /// <summary>借用输入直至方法返回. 调用方必须同步生产者, 并防止 FD/sample 关闭、复用或写入.</summary>
    /// <remarks>非热路径便利 API. 每次分配并返回各输出的独立 float 快照. 高频调用使用 RunInto 复用缓冲区, 或使用 Detect 直接解码 INT8.</remarks>
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
    /// <remarks>非热路径便利 API. 每次返回新分配的独立输出快照. 热路径使用 RunInto 或 Detect.</remarks>
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

    DetectionResult IRknnSession.Detect(DmaFrame frame, YoloV5Profile profile) => Detect(frame, profile);

    /// <summary>同步运行 INT8 检测快路径. 输入 FD/sample 借用至返回, 原生输出不逃逸会话锁, 返回独立检测结果.</summary>
    /// <param name="frame">生产者已完成写入的只读 DMA 描述符, NV12 必须指定准确的颜色空间.</param>
    /// <param name="profile">显式的三头 raw-logit YOLOv5 anchors/类别数/阈值配置.</param>
    internal unsafe DetectionResult Detect(DmaFrame frame, YoloV5Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            if (!ReferenceEquals(_validatedV5Profile, profile))
            {
                profile.ValidateModel(Inputs, Outputs);
                _validatedV5Profile = profile;
            }
            frame.Validate();
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
                var detections = profile.DecodeInt8(Outputs, head0, head1, head2, transform, modelValidated: true);
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
    internal DetectionResult Detect(DmaImage image, YoloV5Profile profile)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(frame => Detect(frame, profile));
    }

    DetectionResult IRknnSession.Detect(DmaFrame frame, YoloDetectionProfile profile) => Detect(frame, profile);

    /// <summary>同步运行标准 affine INT8 DFL 检测. 输出借用至解码结束, 返回独立检测结果.</summary>
    /// <remarks>
    /// 使用显式 (signedValue-zeroPoint)*scale 和稳定 DFL softmax, 不受 FloatOutputMode 影响.
    /// 与 SDK want_float 的舍入可能不同, Run/RunInto 加 Decode 保留为 SDK 浮点参考路径.
    /// </remarks>
    internal unsafe DetectionResult Detect(DmaFrame frame, YoloDetectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            if (!ReferenceEquals(_validatedDflProfile, profile))
            {
                profile.ValidateModel(Inputs, Outputs);
                _validatedDflProfile = profile;
            }
            frame.Validate();
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            using var placement = EnterPlacement();
            bool added = false;
            try
            {
                _handle.DangerousAddRef(ref added);
                NativeMethods.Check(NativeMethods.rks_run_int8(_handle, in frame, out var info));
                var transform = NativeLetterboxGeometry.Create(frame, Inputs[0], in info);
                long borrowStart = Stopwatch.GetTimestamp();
                Span<nint> addresses = stackalloc nint[Outputs.Count];
                Span<uint> counts = stackalloc uint[Outputs.Count];
                ulong generation = 0;
                for (int index = 0; index < Outputs.Count; index++)
                {
                    NativeMethods.Check(NativeMethods.rks_borrow_output_int8(_handle, (uint)index,
                        out nint pointer, out uint count, out ulong currentGeneration));
                    if (pointer == 0 || count != Outputs[index].ElementCount || count > int.MaxValue)
                    { throw new RknnException("Native INT8 output pointer/element count mismatch."); }
                    if (index > 0 && currentGeneration != generation)
                    { throw new RknnException("Native output generation mismatch."); }
                    generation = currentGeneration;
                    addresses[index] = pointer;
                    counts[index] = count;
                }
                double borrowMs = Stopwatch.GetElapsedTime(borrowStart).TotalMilliseconds;
                long postprocessStart = Stopwatch.GetTimestamp();
                var detections = profile.DecodeInt8(Outputs, addresses, counts, transform, modelValidated: true);
                return new DetectionResult(detections, transform,
                    new RunTimings(info.PreprocessMs, info.InferenceMs, info.OutputsMs, borrowMs),
                    Stopwatch.GetElapsedTime(postprocessStart).TotalMilliseconds);
            }
            finally { if (added) { _handle.DangerousRelease(); } }
        }
    }

    /// <summary>运行期间持有 owned DMA 图像; Dispose 等待同步 DFL 检测完成.</summary>
    internal DetectionResult Detect(DmaImage image, YoloDetectionProfile profile)
    {
        ArgumentNullException.ThrowIfNull(image);
        return image.WithFrame(frame => Detect(frame, profile));
    }

    /// <summary>等待本会话当前操作结束后释放原生资源, 可重复调用.</summary>
    public void Dispose() { lock (_gate) { _handle.Dispose(); } }
}
