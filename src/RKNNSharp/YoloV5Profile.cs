namespace RKNNSharp;

public readonly record struct YoloAnchor(float Width, float Height);

/// <summary>显式的三头 NCHW INT8 YOLOv5 raw-logit 配置. 不自动猜测 anchors、类别名或概率输出.</summary>
public sealed class YoloV5Profile
{
    private readonly YoloAnchor[][] _anchors;
    private LookupSet? _int8Lookups;
    public int InputWidth { get; }
    public int InputHeight { get; }
    public int ClassCount { get; }
    public float ConfidenceThreshold { get; }
    public float NmsThreshold { get; }
    public int MaxDetections { get; }

    public YoloV5Profile(int inputWidth, int inputHeight, int classCount,
        IEnumerable<IEnumerable<YoloAnchor>> anchors, float confidenceThreshold = 0.25f,
        float nmsThreshold = 0.45f, int maxDetections = 300)
    {
        ArgumentNullException.ThrowIfNull(anchors);
        if (inputWidth <= 0 || inputHeight <= 0 || inputWidth > 8192 || inputHeight > 8192 ||
            inputWidth % 32 != 0 || inputHeight % 32 != 0 || classCount is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(inputWidth), "Dimensions must be positive multiples of 32; class count must be 1..1000.");
        }
        if (!float.IsFinite(confidenceThreshold) || confidenceThreshold <= 0 || confidenceThreshold > 1 ||
            !float.IsFinite(nmsThreshold) || nmsThreshold < 0 || nmsThreshold > 1 || maxDetections is < 1 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(confidenceThreshold), "Invalid detection thresholds or limit.");
        }
        _anchors = anchors.Select(head => head?.ToArray() ?? throw new ArgumentException("Null anchor head.", nameof(anchors))).ToArray();
        if (_anchors.Length != 3 || _anchors.Any(head => head.Length != 3 || head.Any(anchor =>
            !float.IsFinite(anchor.Width) || !float.IsFinite(anchor.Height) || anchor.Width <= 0 || anchor.Height <= 0 ||
            anchor.Width > 8192 || anchor.Height > 8192)))
        {
            throw new ArgumentException("Exactly three heads of three finite positive anchors are required.", nameof(anchors));
        }
        InputWidth = inputWidth; InputHeight = inputHeight; ClassCount = classCount;
        ConfidenceThreshold = confidenceThreshold; NmsThreshold = nmsThreshold; MaxDetections = maxDetections;
    }

    /// <summary>仅为示例的官方默认 anchors; 未从所提供模型恢复. 调用方须确认自己的模型配置.</summary>
    public static YoloV5Profile CreateOfficial640Example(float confidenceThreshold = 0.25f, float nmsThreshold = 0.45f) =>
        new(640, 640, 80,
        [
            [new(10, 13), new(16, 30), new(33, 23)],
            [new(30, 61), new(62, 45), new(59, 119)],
            [new(116, 90), new(156, 198), new(373, 326)]
        ], confidenceThreshold, nmsThreshold);

    public void ValidateModel(IReadOnlyList<TensorInfo> inputs, IReadOnlyList<TensorInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(outputs);
        if (inputs.Count != 1 || inputs[0].Layout != 1 || inputs[0].DataType != 2 ||
            inputs[0].Quantization != 2 || inputs[0].ZeroPoint != -128 ||
            inputs[0].Scale != 1f / 255 ||
            !inputs[0].Dimensions.SequenceEqual(new uint[] { 1, (uint)InputHeight, (uint)InputWidth, 3 }) ||
            inputs[0].ElementCount != (uint)(InputWidth * InputHeight * 3))
        {
            throw new ArgumentException("YOLO input requires the verified INT8 affine NHWC RGB zeroPoint=-128 scale=1/255 contract.");
        }
        ValidateOutputs(outputs);
    }

    private void ValidateOutputs(IReadOnlyList<TensorInfo> outputs)
    {
        if (outputs.Count != 3) { throw new ArgumentException("YOLOv5 requires exactly three output heads."); }
        for (int head = 0; head < 3; head++)
        {
            var tensor = outputs[head];
            int stride = 8 << head;
            uint width = (uint)(InputWidth / stride), height = (uint)(InputHeight / stride);
            uint channels = (uint)(3 * (5 + ClassCount));
            if (tensor.Index != (uint)head || tensor.Layout != 0 || tensor.DataType != 2 || tensor.Quantization != 2 ||
                tensor.ZeroPoint is < -128 or > 127 || !float.IsFinite(tensor.Scale) || tensor.Scale <= 0 ||
                !tensor.Dimensions.SequenceEqual(new[] { 1u, channels, height, width }) ||
                tensor.ElementCount != channels * height * width)
            {
                throw new ArgumentException($"Head {head} requires affine INT8 standard NCHW [1,{channels},{height},{width}].");
            }
        }
    }

    /// <summary>输入为已转换为float的标准NCHW logits; 默认来自SDK转换, 显式快速模式可能有不同舍入. 方法不访问图像像素.</summary>
    public IReadOnlyList<Detection> Decode(IReadOnlyList<TensorOutput> outputs, LetterboxTransform transform)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        transform.Validate();
        ValidateOutputs(outputs.Select(output => output.Info).ToArray());
        var candidates = new List<Detection>();
        for (int head = 0; head < 3; head++)
        {
            var output = outputs[head];
            var values = output.Values.Span;
            if (values.Length != output.Info.ElementCount) { throw new ArgumentException($"Head {head} float buffer length mismatch."); }
            foreach (float value in values)
            {
                if (!float.IsFinite(value)) { throw new ArgumentException($"Head {head} contains non-finite logits."); }
            }
            int stride = 8 << head, gridWidth = InputWidth / stride, gridHeight = InputHeight / stride;
            int plane = gridWidth * gridHeight, attributes = 5 + ClassCount;
            for (int anchorIndex = 0; anchorIndex < 3; anchorIndex++)
            {
                int start = anchorIndex * attributes * plane;
                var anchor = _anchors[head][anchorIndex];
                for (int cell = 0; cell < plane; cell++)
                {
                    float objectness = Sigmoid(values[start + 4 * plane + cell]);
                    if (objectness < ConfidenceThreshold) { continue; }
                    int classId = 0;
                    float bestClass = values[start + 5 * plane + cell];
                    for (int classIndex = 1; classIndex < ClassCount; classIndex++)
                    {
                        float value = values[start + (5 + classIndex) * plane + cell];
                        if (value > bestClass) { bestClass = value; classId = classIndex; }
                    }
                    float confidence = objectness * Sigmoid(bestClass);
                    if (confidence < ConfidenceThreshold) { continue; }
                    float centerX = (2 * Sigmoid(values[start + cell]) - 0.5f + cell % gridWidth) * stride;
                    float centerY = (2 * Sigmoid(values[start + plane + cell]) - 0.5f + cell / gridWidth) * stride;
                    float widthScale = 2 * Sigmoid(values[start + 2 * plane + cell]);
                    float heightScale = 2 * Sigmoid(values[start + 3 * plane + cell]);
                    float width = widthScale * widthScale * anchor.Width, height = heightScale * heightScale * anchor.Height;
                    var box = new BoundingBox(centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2);
                    if (box.IsValid) { candidates.Add(new Detection(classId, confidence, box)); }
                }
            }
        }
        return Finish(candidates, transform);
    }

    internal IReadOnlyList<Detection> DecodeInt8(IReadOnlyList<TensorInfo> infos,
        ReadOnlySpan<sbyte> head0, ReadOnlySpan<sbyte> head1, ReadOnlySpan<sbyte> head2, LetterboxTransform transform)
    {
        transform.Validate();
        ValidateOutputs(infos);
        var lookups = Volatile.Read(ref _int8Lookups);
        if (lookups is null || !lookups.Matches(infos))
        {
            lookups = new LookupSet(infos);
            Volatile.Write(ref _int8Lookups, lookups);
        }
        var candidates = new List<Detection>();
        for (int head = 0; head < 3; head++)
        {
            ReadOnlySpan<sbyte> values = head switch { 0 => head0, 1 => head1, _ => head2 };
            if (values.Length != infos[head].ElementCount) { throw new ArgumentException($"Head {head} INT8 buffer length mismatch."); }
            var lookup = lookups.Probabilities[head];
            int stride = 8 << head, gridWidth = InputWidth / stride, gridHeight = InputHeight / stride;
            int plane = gridWidth * gridHeight, attributes = 5 + ClassCount;
            for (int anchorIndex = 0; anchorIndex < 3; anchorIndex++)
            {
                int start = anchorIndex * attributes * plane;
                var anchor = _anchors[head][anchorIndex];
                for (int cell = 0; cell < plane; cell++)
                {
                    float objectness = lookup[values[start + 4 * plane + cell] + 128];
                    if (objectness < ConfidenceThreshold) { continue; }
                    int classId = 0;
                    sbyte bestClass = values[start + 5 * plane + cell];
                    for (int classIndex = 1; classIndex < ClassCount; classIndex++)
                    {
                        sbyte value = values[start + (5 + classIndex) * plane + cell];
                        // Compare signed quantized logits, not rounded/saturated probabilities.
                        if (value > bestClass) { bestClass = value; classId = classIndex; }
                    }
                    float confidence = objectness * lookup[bestClass + 128];
                    if (confidence < ConfidenceThreshold) { continue; }
                    float centerX = (2 * lookup[values[start + cell] + 128] - 0.5f + cell % gridWidth) * stride;
                    float centerY = (2 * lookup[values[start + plane + cell] + 128] - 0.5f + cell / gridWidth) * stride;
                    float widthScale = 2 * lookup[values[start + 2 * plane + cell] + 128];
                    float heightScale = 2 * lookup[values[start + 3 * plane + cell] + 128];
                    float width = widthScale * widthScale * anchor.Width, height = heightScale * heightScale * anchor.Height;
                    var box = new BoundingBox(centerX - width / 2, centerY - height / 2, centerX + width / 2, centerY + height / 2);
                    if (box.IsValid) { candidates.Add(new Detection(classId, confidence, box)); }
                }
            }
        }
        // INT8 contains no NaN/Inf. The LUT validates every possible dequantized value once,
        // so a repeated full tensor finite scan is unnecessary without relaxing validation.
        return Finish(candidates, transform);
    }

    private IReadOnlyList<Detection> Finish(IReadOnlyList<Detection> candidates, LetterboxTransform transform)
    {
        // NMS is applied in model coordinates; restoration/clipping cannot change suppression at image edges.
        var selected = NonMaximumSuppression(candidates, NmsThreshold, MaxDetections);
        return Array.AsReadOnly(selected.Select(detection => detection with { Box = transform.Restore(detection.Box) })
            .Where(detection => detection.Box.IsValid).ToArray());
    }

    private sealed class LookupSet
    {
        private readonly int[] _zeroPoints;
        private readonly float[] _scales;
        internal float[][] Probabilities { get; }

        internal LookupSet(IReadOnlyList<TensorInfo> infos)
        {
            _zeroPoints = infos.Select(info => info.ZeroPoint).ToArray();
            _scales = infos.Select(info => info.Scale).ToArray();
            Probabilities = new float[3][];
            for (int head = 0; head < 3; head++)
            {
                var values = new float[256];
                for (int value = -128; value <= 127; value++)
                {
                    float logit = (value - _zeroPoints[head]) * _scales[head];
                    if (!float.IsFinite(logit)) { throw new ArgumentException("INT8 quantization overflows finite float logits."); }
                    values[value + 128] = Sigmoid(logit);
                }
                Probabilities[head] = values;
            }
        }

        internal bool Matches(IReadOnlyList<TensorInfo> infos)
        {
            for (int i = 0; i < 3; i++)
            {
                if (_zeroPoints[i] != infos[i].ZeroPoint || _scales[i] != infos[i].Scale) { return false; }
            }
            return true;
        }
    }

    internal static float Sigmoid(float value)
    {
        if (value >= 0) { return 1 / (1 + MathF.Exp(-value)); }
        float exponential = MathF.Exp(value);
        return exponential / (1 + exponential);
    }

    internal static IReadOnlyList<Detection> NonMaximumSuppression(IReadOnlyList<Detection> candidates, float threshold, int limit)
    {
        var ordered = candidates.Select((value, index) => (value, index))
            .Where(item => item.value.Box.IsValid && float.IsFinite(item.value.Confidence))
            .OrderByDescending(item => item.value.Confidence).ThenBy(item => item.index);
        var kept = new List<Detection>();
        foreach (var item in ordered)
        {
            bool suppressed = kept.Any(previous => previous.ClassId == item.value.ClassId && IntersectionOverUnion(previous.Box, item.value.Box) > threshold);
            if (!suppressed)
            {
                kept.Add(item.value);
                if (kept.Count == limit) { break; }
            }
        }
        return kept;
    }

    internal static float IntersectionOverUnion(BoundingBox a, BoundingBox b)
    {
        float intersection = MathF.Max(0, MathF.Min(a.Right, b.Right) - MathF.Max(a.Left, b.Left)) *
            MathF.Max(0, MathF.Min(a.Bottom, b.Bottom) - MathF.Max(a.Top, b.Top));
        float union = a.Width * a.Height + b.Width * b.Height - intersection;
        return union > 0 ? intersection / union : 0;
    }
}
