namespace RKNNSharp;

/// <summary>统一检测配置. Anchors 与 Heads 必须且只能提供一种; 构造时复制集合并验证配置自身.</summary>
public sealed class RknnPipelineConfig
{
    internal YoloV5Profile? V5Profile { get; }
    internal YoloDetectionProfile? DflProfile { get; }
    internal ModelType Contract { get; }
    public int InputWidth { get; }
    public int InputHeight { get; }
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
            V5Profile = new YoloV5Profile(inputWidth, inputHeight, classCount, captured, confidenceThreshold, nmsThreshold, maxDetections);
            Anchors = Array.AsReadOnly(captured.Select(head => (IReadOnlyList<YoloAnchor>)Array.AsReadOnly(head)).ToArray());
            Heads = Array.Empty<YoloDetectionHead>();
            Contract = ModelType.YoloV5Detection;
        }
        else
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
            Anchors = Array.Empty<IReadOnlyList<YoloAnchor>>();
            Contract = ModelType.YoloV8Detection;
        }
        InputWidth = inputWidth; InputHeight = inputHeight; ClassCount = classCount; RegMax = regMax;
        ConfidenceThreshold = confidenceThreshold; NmsThreshold = nmsThreshold; MultiLabel = multiLabel;
        MaxDetections = maxDetections; ClassNames = Array.AsReadOnly(labels);
    }
}
