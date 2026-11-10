namespace RKNNSharp;

/// <summary>统一检测配置. Anchors 与 Heads 必须且只能提供一种. 加载后以模型尺寸验证检测契约.</summary>
public sealed class RknnPipelineConfig
{
    internal YoloV5Profile? V5Profile { get; }
    internal YoloDetectionProfile? DflProfile { get; }
    internal ModelType Contract { get; }
    public int InputWidth { get; }
    public int InputHeight { get; }
    /// <summary>模型加载后为实际 batch, 尚未绑定模型时为 0.</summary>
    public int BatchSize => InputTensor is null ? 0 : checked((int)InputTensor.Dimensions[0]);
    /// <summary>加载后读取的完整输入属性, 包含布局、类型及量化参数. 未绑定时为 null.</summary>
    public TensorInfo? InputTensor { get; private init; }
    public ChannelOrder ChannelOrder { get; init; } = ChannelOrder.Rgb;
    public ResizeMode ResizeMode { get; init; } = ResizeMode.Letterbox;
    public NormalizationMode Normalization { get; init; } = NormalizationMode.ModelDefault;
    public int ClassCount { get; }
    public int RegMax { get; }
    public float ConfidenceThreshold { get; }
    public float NmsThreshold { get; }
    public bool MultiLabel { get; }
    public int MaxDetections { get; }
    public IReadOnlyList<IReadOnlyList<YoloAnchor>> Anchors { get; }
    public IReadOnlyList<YoloDetectionHead> Heads { get; }
    public IReadOnlyList<string> ClassNames { get; }

    /// <summary>V5 使用 anchors; DFL 使用 heads 和 regMax. 类别名仅为标签, 不参与解码.</summary>
    public RknnPipelineConfig(int inputWidth, int inputHeight, int classCount,
        IEnumerable<IEnumerable<YoloAnchor>>? anchors = null, IReadOnlyList<YoloDetectionHead>? heads = null,
        int regMax = 16, float confidenceThreshold = .25f, float nmsThreshold = .45f,
        bool multiLabel = false, int maxDetections = 300, IReadOnlyList<string>? classNames = null)
        : this(inputWidth, inputHeight, classCount, anchors, heads, regMax, confidenceThreshold,
            nmsThreshold, multiLabel, maxDetections, classNames, false) { }

    /// <summary>自动从模型读取宽高和 batch. 绑定前 InputWidth/InputHeight 为 0. 检测参数在加载时完整验证.</summary>
    public RknnPipelineConfig(int classCount,
        IEnumerable<IEnumerable<YoloAnchor>>? anchors = null, IReadOnlyList<YoloDetectionHead>? heads = null,
        int regMax = 16, float confidenceThreshold = .25f, float nmsThreshold = .45f,
        bool multiLabel = false, int maxDetections = 300, IReadOnlyList<string>? classNames = null)
        : this(0, 0, classCount, anchors, heads, regMax, confidenceThreshold,
            nmsThreshold, multiLabel, maxDetections, classNames, true) { }

    private RknnPipelineConfig(int inputWidth, int inputHeight, int classCount,
        IEnumerable<IEnumerable<YoloAnchor>>? anchors, IReadOnlyList<YoloDetectionHead>? heads,
        int regMax, float confidenceThreshold, float nmsThreshold,
        bool multiLabel, int maxDetections, IReadOnlyList<string>? classNames, bool inferDimensions)
    {
        if ((anchors is null) == (heads is null))
        { throw new ArgumentException("Provide exactly one of anchors (V5 raw) or heads (RKNN DFL)."); }
        string[] labels = classNames?.ToArray() ?? [];
        if (labels.Length != 0 && (labels.Length != classCount || labels.Any(string.IsNullOrWhiteSpace)))
        { throw new ArgumentException("Labels must match the class count and contain no blank entries.", nameof(classNames)); }
        if (anchors is not null)
        {
            if (multiLabel || regMax != 16)
            { throw new ArgumentException("MultiLabel and nondefault RegMax apply only to DFL detection."); }
            var captured = anchors.Select(head => head?.ToArray() ?? throw new ArgumentException("Null anchor head.", nameof(anchors))).ToArray();
            if (!inferDimensions)
            { V5Profile = new YoloV5Profile(inputWidth, inputHeight, classCount, captured, confidenceThreshold, nmsThreshold, maxDetections); }
            Anchors = Array.AsReadOnly(captured.Select(head => (IReadOnlyList<YoloAnchor>)Array.AsReadOnly(head)).ToArray());
            Heads = Array.Empty<YoloDetectionHead>();
            Contract = ModelType.YoloV5Detection;
        }
        else
        {
            if (!inferDimensions)
            {
                var config = new YoloDetectionConfig(inputWidth, inputHeight, classCount,
                    YoloDetectionOutputFormat.RknnDfl, heads!, regMax, confidenceThreshold, nmsThreshold, multiLabel, maxDetections, labels);
                int[] indices = config.Heads.SelectMany(head => head.ScoreSumOutputIndex is int sum
                    ? new[] { head.BoxOutputIndex, head.ClassOutputIndex, sum }
                    : new[] { head.BoxOutputIndex, head.ClassOutputIndex }).Order().ToArray();
                if (!indices.SequenceEqual(Enumerable.Range(0, indices.Length)))
                { throw new ArgumentException("DFL mapping must cover all output indices without gaps.", nameof(heads)); }
                DflProfile = new YoloDetectionProfile(config);
                Heads = config.Heads;
            }
            else { Heads = Array.AsReadOnly(heads!.ToArray()); }
            Anchors = Array.Empty<IReadOnlyList<YoloAnchor>>();
            Contract = ModelType.YoloV8Detection;
        }
        InputWidth = inputWidth; InputHeight = inputHeight; ClassCount = classCount; RegMax = regMax;
        ConfidenceThreshold = confidenceThreshold; NmsThreshold = nmsThreshold; MultiLabel = multiLabel;
        MaxDetections = maxDetections; ClassNames = Array.AsReadOnly(labels);
    }

    internal void ValidatePreprocessing()
    {
        if (!Enum.IsDefined(ChannelOrder)) { throw new ArgumentOutOfRangeException(nameof(ChannelOrder)); }
        if (!Enum.IsDefined(ResizeMode)) { throw new ArgumentOutOfRangeException(nameof(ResizeMode)); }
        if (!Enum.IsDefined(Normalization)) { throw new ArgumentOutOfRangeException(nameof(Normalization)); }
        if (ChannelOrder != ChannelOrder.Rgb || ResizeMode != ResizeMode.Letterbox || Normalization != NormalizationMode.ModelDefault)
        {
            throw new NotSupportedException($"Native preprocessing supports only Rgb, Letterbox, ModelDefault. " +
                $"Configured ChannelOrder={ChannelOrder}, ResizeMode={ResizeMode}, Normalization={Normalization}.");
        }
    }

    internal RknnPipelineConfig Bind(TensorInfo input)
    {
        NativeInputContract.Validate([input], InputWidth == 0 ? null : InputWidth, InputHeight == 0 ? null : InputHeight);
        return new RknnPipelineConfig(checked((int)input.Dimensions[2]), checked((int)input.Dimensions[1]), ClassCount,
            Contract == ModelType.YoloV5Detection ? Anchors : null,
            Contract == ModelType.YoloV8Detection ? Heads : null,
            RegMax, ConfidenceThreshold, NmsThreshold, MultiLabel, MaxDetections, ClassNames)
        {
            InputTensor = input,
            ChannelOrder = ChannelOrder,
            ResizeMode = ResizeMode,
            Normalization = Normalization
        };
    }
}
