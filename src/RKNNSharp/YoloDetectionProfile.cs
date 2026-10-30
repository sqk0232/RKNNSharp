namespace RKNNSharp;

/// <summary>显式的 Rockchip DFL 检测解码器. 类别 tensor 已是概率, 无 objectness 或二次 sigmoid.</summary>
public sealed class YoloDetectionProfile
{
    public YoloDetectionConfig Config { get; }

    public YoloDetectionProfile(YoloDetectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
    }

    /// <summary>验证原生输入约束及映射的标准 NCHW affine INT8 输出, 不依据模型名称推断格式.</summary>
    public void ValidateModel(IReadOnlyList<TensorInfo> inputs, IReadOnlyList<TensorInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != 1 || inputs[0] is not { } input || input.Index != 0 ||
            input.Layout != 1 || input.DataType != 2 || input.Quantization != 2 ||
            input.ZeroPoint != -128 || input.Scale != 1f / 255 ||
            !input.Dimensions.SequenceEqual(new uint[] { 1, (uint)Config.InputHeight, (uint)Config.InputWidth, 3 }) ||
            input.ElementCount != (uint)(Config.InputWidth * Config.InputHeight * 3))
        {
            throw new ArgumentException("DFL detection requires the verified INT8 affine NHWC RGB input contract: zeroPoint=-128, scale=1/255.", nameof(inputs));
        }
        ValidateOutputs(outputs);
    }

    private void ValidateOutputs(IReadOnlyList<TensorInfo> outputs)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        int mappedCount = Config.Heads.Sum(head => head.ScoreSumOutputIndex.HasValue ? 3 : 2);
        if (outputs.Count != mappedCount)
        { throw new ArgumentException("Every output must be explicitly mapped to a DFL box, class probability, or optional score-sum tensor.", nameof(outputs)); }
        foreach (var head in Config.Heads)
        {
            ValidateTensor(head.BoxOutputIndex, 4 * Config.RegMax, head.Stride);
            ValidateTensor(head.ClassOutputIndex, Config.ClassCount, head.Stride);
            if (head.ScoreSumOutputIndex is int sum) { ValidateTensor(sum, 1, head.Stride); }
        }

        void ValidateTensor(int index, int channels, int stride)
        {
            if (index >= outputs.Count) { throw new ArgumentException($"Mapped output {index} does not exist.", nameof(outputs)); }
            var tensor = outputs[index];
            uint height = (uint)(Config.InputHeight / stride), width = (uint)(Config.InputWidth / stride);
            if (tensor is null || tensor.Index != (uint)index || tensor.Layout != 0 ||
                tensor.DataType != 2 || tensor.Quantization != 2 || tensor.ZeroPoint is < -128 or > 127 ||
                !float.IsFinite(tensor.Scale) || tensor.Scale <= 0 ||
                !tensor.Dimensions.SequenceEqual(new[] { 1u, (uint)channels, height, width }) ||
                tensor.ElementCount != (uint)channels * height * width)
            {
                throw new ArgumentException($"Output {index} requires affine INT8 standard NCHW [1,{channels},{height},{width}].", nameof(outputs));
            }
        }
    }

    /// <summary>读取 SDK want_float=1 的 float 数据, 不再反量化. Score-sum 遵循官方 Python 参考, 仅验证而不用于预筛.</summary>
    public IReadOnlyList<Detection> Decode(IReadOnlyList<TensorOutput> outputs, LetterboxTransform transform)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        transform.Validate();
        if (outputs.Any(output => output is null)) { throw new ArgumentException("Null output tensor.", nameof(outputs)); }
        ValidateOutputs(outputs.Select(output => output.Info).ToArray());
        foreach (var output in outputs)
        {
            if (output.Values.Length != output.Info.ElementCount)
            { throw new ArgumentException($"Output {output.Info.Index} float buffer length mismatch.", nameof(outputs)); }
            foreach (float value in output.Values.Span)
            {
                if (!float.IsFinite(value))
                { throw new ArgumentException($"Output {output.Info.Index} contains non-finite values.", nameof(outputs)); }
            }
        }

        var candidates = new List<Detection>();
        // Canonical branch order makes equal-score ties independent of the caller's mapping order.
        foreach (var head in Config.Heads.OrderBy(head => head.Stride))
        {
            ReadOnlySpan<float> boxes = outputs[head.BoxOutputIndex].Values.Span;
            ReadOnlySpan<float> classes = outputs[head.ClassOutputIndex].Values.Span;
            int width = Config.InputWidth / head.Stride, height = Config.InputHeight / head.Stride;
            int plane = width * height;
            // Keep SDK dequantized scores verbatim, including small quantization excursions
            // outside [0,1], as the fixed official Python reference does.
            for (int cell = 0; cell < plane; cell++)
            {
                int bestClass = 0;
                float bestScore = classes[cell];
                for (int classId = 1; classId < Config.ClassCount; classId++)
                {
                    float score = classes[classId * plane + cell];
                    if (score > bestScore) { bestScore = score; bestClass = classId; }
                }
                if (bestScore < Config.ConfidenceThreshold) { continue; }
                float left = Expectation(boxes, cell, plane, 0);
                float top = Expectation(boxes, cell, plane, 1);
                float right = Expectation(boxes, cell, plane, 2);
                float bottom = Expectation(boxes, cell, plane, 3);
                float centerX = cell % width + .5f, centerY = cell / width + .5f;
                var box = new BoundingBox((centerX - left) * head.Stride, (centerY - top) * head.Stride,
                    (centerX + right) * head.Stride, (centerY + bottom) * head.Stride);
                if (!box.IsValid) { continue; }
                if (!Config.MultiLabel) { candidates.Add(new(bestClass, bestScore, box)); }
                else
                {
                    for (int classId = 0; classId < Config.ClassCount; classId++)
                    {
                        float score = classes[classId * plane + cell];
                        if (score >= Config.ConfidenceThreshold) { candidates.Add(new(classId, score, box)); }
                    }
                }
            }
        }
        var selected = Suppress(candidates);
        return Array.AsReadOnly(selected.Select(detection => detection with { Box = transform.Restore(detection.Box) })
            .Where(detection => detection.Box.IsValid).ToArray());
    }

    private float Expectation(ReadOnlySpan<float> values, int cell, int plane, int side)
    {
        int start = side * Config.RegMax * plane + cell;
        float maximum = values[start];
        for (int bin = 1; bin < Config.RegMax; bin++) { maximum = MathF.Max(maximum, values[start + bin * plane]); }
        float sum = 0, weighted = 0;
        for (int bin = 0; bin < Config.RegMax; bin++)
        {
            float weight = MathF.Exp(values[start + bin * plane] - maximum);
            sum += weight;
            weighted += bin * weight;
        }
        return weighted / sum;
    }

    private IReadOnlyList<Detection> Suppress(IReadOnlyList<Detection> candidates)
    {
        var kept = new List<Detection>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Confidence))
        {
            if (kept.Any(previous => previous.ClassId == candidate.ClassId &&
                ReferenceIntersectionOverUnion(previous.Box, candidate.Box) > Config.NmsThreshold)) { continue; }
            kept.Add(candidate);
            if (kept.Count == Config.MaxDetections) { break; }
        }
        return kept;
    }

    private static float ReferenceIntersectionOverUnion(BoundingBox a, BoundingBox b)
    {
        // Rockchip Python reference includes a 1e-5 intersection epsilon, no +1 area convention.
        double width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) + .00001);
        double height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + .00001);
        double intersection = width * height;
        double union = (double)a.Width * a.Height + (double)b.Width * b.Height - intersection;
        return union > 0 ? (float)(intersection / union) : 0;
    }
}
