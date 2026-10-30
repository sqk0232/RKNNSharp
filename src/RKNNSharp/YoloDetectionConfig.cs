namespace RKNNSharp;

/// <summary>显式的模型输出契约, 不根据 YOLO 型号或 tensor 名称猜测.</summary>
public enum YoloDetectionOutputFormat
{
    /// <summary>三个 NCHW 分支: DFL box logits、类别概率、可选 score-sum. 不对类别概率重复 sigmoid.</summary>
    RknnDfl
}

/// <summary>一个空间尺度的实际 tensor 索引与步长. 与固定官方 Python 参考一致, ScoreSum 只校验而不预筛类别概率.</summary>
public sealed record YoloDetectionHead(int BoxOutputIndex, int ClassOutputIndex, int? ScoreSumOutputIndex, int Stride);

/// <summary>静态 batch1 RKNN YOLOv8/YOLO11 检测配置. 仅表达已明确的 DFL 输出格式, 不改变原生输入限制.</summary>
public sealed class YoloDetectionConfig
{
    public int InputWidth { get; }
    public int InputHeight { get; }
    public int ClassCount { get; }
    public int RegMax { get; }
    public YoloDetectionOutputFormat OutputFormat { get; }
    public IReadOnlyList<YoloDetectionHead> Heads { get; }
    public float ConfidenceThreshold { get; }
    public float NmsThreshold { get; }
    public bool MultiLabel { get; }
    public int MaxDetections { get; }
    public IReadOnlyList<string> ClassNames { get; }

    public YoloDetectionConfig(int inputWidth, int inputHeight, int classCount,
        YoloDetectionOutputFormat outputFormat, IReadOnlyList<YoloDetectionHead> heads,
        int regMax = 16, float confidenceThreshold = .25f, float nmsThreshold = .45f,
        bool multiLabel = false, int maxDetections = 300, IReadOnlyList<string>? classNames = null)
    {
        ArgumentNullException.ThrowIfNull(heads);
        if (inputWidth is < 32 or > 4096 || inputHeight is < 32 or > 4096 || inputWidth % 32 != 0 || inputHeight % 32 != 0)
        { throw new ArgumentOutOfRangeException(nameof(inputWidth), "Input dimensions must be multiples of32 in32..4096."); }
        if (classCount <= 0 || classCount > int.MaxValue / (inputWidth / 8 * (inputHeight / 8)))
        { throw new ArgumentOutOfRangeException(nameof(classCount), "Class count and spatial dimensions exceed the tensor element contract."); }
        if (regMax is < 2 or > 256) { throw new ArgumentOutOfRangeException(nameof(regMax), "DFL bin count must be2..256."); }
        if (outputFormat != YoloDetectionOutputFormat.RknnDfl) { throw new ArgumentOutOfRangeException(nameof(outputFormat)); }
        if (!float.IsFinite(confidenceThreshold) || confidenceThreshold <= 0 || confidenceThreshold > 1)
        { throw new ArgumentOutOfRangeException(nameof(confidenceThreshold)); }
        if (!float.IsFinite(nmsThreshold) || nmsThreshold < 0 || nmsThreshold > 1)
        { throw new ArgumentOutOfRangeException(nameof(nmsThreshold)); }
        if (maxDetections is < 1 or > 10000) { throw new ArgumentOutOfRangeException(nameof(maxDetections)); }
        YoloDetectionHead[] captured = heads.ToArray();
        if (captured.Length != 3 || captured.Any(head => head is null) ||
            !captured.Select(head => head.Stride).Order().SequenceEqual(new[] { 8, 16, 32 }))
        { throw new ArgumentException("Exactly three explicit branches with strides8/16/32 are required.", nameof(heads)); }
        var indices = new HashSet<int>();
        foreach (var head in captured)
        {
            if (head.BoxOutputIndex < 0 || head.ClassOutputIndex < 0 || head.ScoreSumOutputIndex < 0 ||
                !indices.Add(head.BoxOutputIndex) || !indices.Add(head.ClassOutputIndex) ||
                (head.ScoreSumOutputIndex is int sum && !indices.Add(sum)))
            { throw new ArgumentException("Mapped tensor indices must be nonnegative and unique across branches.", nameof(heads)); }
        }
        string[] labels = classNames?.ToArray() ?? [];
        if (labels.Length != 0 && (labels.Length != classCount || labels.Any(string.IsNullOrWhiteSpace)))
        { throw new ArgumentException("Provided labels must match the class count and contain no blank entries.", nameof(classNames)); }
        InputWidth = inputWidth; InputHeight = inputHeight; ClassCount = classCount; RegMax = regMax;
        OutputFormat = outputFormat; Heads = Array.AsReadOnly(captured); ConfidenceThreshold = confidenceThreshold;
        NmsThreshold = nmsThreshold; MultiLabel = multiLabel; MaxDetections = maxDetections;
        ClassNames = Array.AsReadOnly(labels);
    }
}
