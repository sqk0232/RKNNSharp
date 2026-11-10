using System.Buffers;

namespace RKNNSharp;

/// <summary>显式的 Rockchip DFL 检测解码器. 类别 tensor 已是概率, 无 objectness 或二次 sigmoid.</summary>
internal sealed class YoloDetectionProfile
{
    private readonly YoloDetectionHead[] _headsByStride;
    public YoloDetectionConfig Config { get; }

    public YoloDetectionProfile(YoloDetectionConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        Config = config;
        // 步长唯一且固定为 8/16/32，排序结果与每次 OrderBy 相同，避免每帧分配。
        _headsByStride = config.Heads.OrderBy(head => head.Stride).ToArray();
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
        Span<int> planes = stackalloc int[3];
        Span<int> offsets = stackalloc int[3];
        Span<int> classSlot = stackalloc int[outputs.Count];
        classSlot.Fill(-1);
        int total = 0;
        for (int slot = 0; slot < _headsByStride.Length; slot++)
        {
            YoloDetectionHead head = _headsByStride[slot];
            int plane = Config.InputWidth / head.Stride * (Config.InputHeight / head.Stride);
            planes[slot] = plane;
            offsets[slot] = total;
            total += plane;
            classSlot[head.ClassOutputIndex] = slot;
        }
        float[] rentedScores = ArrayPool<float>.Shared.Rent(total);
        int[] rentedIds = ArrayPool<int>.Shared.Rent(total);
        try
        {
            // 按输出下标顺序拒绝 NaN/Inf，与原先逐张量扫描的失败顺序一致。
            // 类别张量顺带做 argmax：每个元素只读一次。box 与 score-sum 在低分格点或
            // 官方参考里不会被解码，仍须整块检查。
            for (int index = 0; index < outputs.Count; index++)
            {
                TensorOutput output = outputs[index];
                ReadOnlySpan<float> values = output.Values.Span;
                if (values.Length != output.Info.ElementCount)
                { throw new ArgumentException($"Output {output.Info.Index} float buffer length mismatch.", nameof(outputs)); }
                int slot = classSlot[index];
                if (slot < 0)
                {
                    if (FiniteTensor.FirstNonFinite(values) >= 0)
                    { throw new ArgumentException($"Output {output.Info.Index} contains non-finite values.", nameof(outputs)); }
                }
                else
                {
                    ReadBestClass(values, planes[slot], Config.ClassCount, rentedScores.AsSpan(offsets[slot], planes[slot]),
                        rentedIds.AsSpan(offsets[slot], planes[slot]), output.Info.Index);
                }
            }

            var candidates = new List<DetectionCandidate>();
            // Canonical branch order makes equal-score ties independent of the caller's mapping order.
            for (int slot = 0; slot < _headsByStride.Length; slot++)
            {
                YoloDetectionHead head = _headsByStride[slot];
                ReadOnlySpan<float> boxes = outputs[head.BoxOutputIndex].Values.Span;
                ReadOnlySpan<float> classes = outputs[head.ClassOutputIndex].Values.Span;
                ReadOnlySpan<float> bestScores = rentedScores.AsSpan(offsets[slot], planes[slot]);
                ReadOnlySpan<int> bestIds = rentedIds.AsSpan(offsets[slot], planes[slot]);
                int width = Config.InputWidth / head.Stride, height = Config.InputHeight / head.Stride;
                int plane = planes[slot];
                // Keep SDK dequantized scores verbatim, including small quantization excursions
                // outside [0,1], as the fixed official Python reference does.
                for (int cell = 0; cell < plane; cell++)
                {
                    float bestScore = bestScores[cell];
                    int bestClass = bestIds[cell];
                    if (bestScore < Config.ConfidenceThreshold) { continue; }
                    float left = Expectation(boxes, cell, plane, 0);
                    float top = Expectation(boxes, cell, plane, 1);
                    float right = Expectation(boxes, cell, plane, 2);
                    float bottom = Expectation(boxes, cell, plane, 3);
                    float centerX = cell % width + .5f, centerY = cell / width + .5f;
                    var box = new BoundingBox((centerX - left) * head.Stride, (centerY - top) * head.Stride,
                        (centerX + right) * head.Stride, (centerY + bottom) * head.Stride);
                    if (!box.IsValid) { continue; }
                    if (!Config.MultiLabel) { candidates.Add(new(new(bestClass, bestScore, box), candidates.Count)); }
                    else
                    {
                        for (int classId = 0; classId < Config.ClassCount; classId++)
                        {
                            float score = classes[classId * plane + cell];
                            if (score >= Config.ConfidenceThreshold) { candidates.Add(new(new(classId, score, box), candidates.Count)); }
                        }
                    }
                }
            }
            var selected = Suppress(candidates);
            return Array.AsReadOnly(selected.Select(detection => detection with { Box = transform.Restore(detection.Box) })
                .Where(detection => detection.Box.IsValid).ToArray());
        }
        finally
        {
            ArrayPool<float>.Shared.Return(rentedScores);
            ArrayPool<int>.Shared.Return(rentedIds);
        }
    }

    private static void ReadBestClass(
        ReadOnlySpan<float> values, int plane, int classCount, Span<float> bestScores,
        Span<int> bestIds, uint outputIndex)
    {
        for (int classId = 0; classId < classCount; classId++)
        {
            ReadOnlySpan<float> planeValues = values.Slice(classId * plane, plane);
            if (FiniteTensor.FirstNonFinite(planeValues) >= 0)
            { throw new ArgumentException($"Output {outputIndex} contains non-finite values.", "outputs"); }
            if (classId == 0)
            {
                planeValues.CopyTo(bestScores);
                bestIds.Fill(0);
                continue;
            }
            for (int cell = 0; cell < plane; cell++)
            {
                float score = planeValues[cell];
                if (score > bestScores[cell]) { bestScores[cell] = score; bestIds[cell] = classId; }
            }
        }
    }

    // Addresses borrow standard, contiguous NCHW outputs only for this synchronous call.
    // Match explicit affine dequantization followed by Decode, including float subtraction
    // before Exp. SDK want_float rounding remains a separate Run/RunInto reference.
    internal unsafe IReadOnlyList<Detection> DecodeInt8(IReadOnlyList<TensorInfo> infos,
        ReadOnlySpan<nint> addresses, ReadOnlySpan<uint> counts, LetterboxTransform transform, bool modelValidated = false)
    {
        transform.Validate();
        // RknnModel skips only structure checks after validating its immutable metadata under the session lock.
        if (!modelValidated) { ValidateOutputs(infos); }
        if (addresses.Length != infos.Count || counts.Length != infos.Count)
        { throw new ArgumentException("INT8 buffer count must exactly match model outputs.", nameof(addresses)); }
        for (int index = 0; index < infos.Count; index++)
        {
            TensorInfo info = infos[index];
            if (addresses[index] == 0 || counts[index] != info.ElementCount || counts[index] > int.MaxValue)
            { throw new ArgumentException($"Output {index} INT8 buffer length/pointer mismatch.", nameof(addresses)); }
            // Finite positive scales normally make every possible INT8 value finite.
            // For extreme scales check actual values, including unused box/score-sum tails.
            if (!float.IsFinite((-128 - info.ZeroPoint) * info.Scale) ||
                !float.IsFinite((127 - info.ZeroPoint) * info.Scale))
            {
                foreach (sbyte value in new ReadOnlySpan<sbyte>((void*)addresses[index], (int)counts[index]))
                {
                    if (!float.IsFinite((value - info.ZeroPoint) * info.Scale))
                    { throw new ArgumentException($"Output {index} contains non-finite values.", nameof(addresses)); }
                }
            }
        }

        int largestPlane = Config.InputWidth / 8 * (Config.InputHeight / 8);
        sbyte[] rentedScores = ArrayPool<sbyte>.Shared.Rent(largestPlane);
        int[] rentedIds = ArrayPool<int>.Shared.Rent(largestPlane);
        try
        {
            var candidates = new List<DetectionCandidate>();
            Span<float> boxLookup = stackalloc float[256];
            Span<float> classLookup = stackalloc float[256];
            Span<float> gathered = stackalloc float[Config.RegMax];
            foreach (YoloDetectionHead head in _headsByStride)
            {
                TensorInfo boxInfo = infos[head.BoxOutputIndex], classInfo = infos[head.ClassOutputIndex];
                for (int value = -128; value <= 127; value++)
                {
                    boxLookup[value + 128] = (value - boxInfo.ZeroPoint) * boxInfo.Scale;
                    classLookup[value + 128] = (value - classInfo.ZeroPoint) * classInfo.Scale;
                }
                int width = Config.InputWidth / head.Stride, height = Config.InputHeight / head.Stride;
                int plane = width * height;
                var boxes = new ReadOnlySpan<sbyte>((void*)addresses[head.BoxOutputIndex], (int)boxInfo.ElementCount);
                var classes = new ReadOnlySpan<sbyte>((void*)addresses[head.ClassOutputIndex], (int)classInfo.ElementCount);
                Span<sbyte> bestScores = rentedScores.AsSpan(0, plane);
                Span<int> bestIds = rentedIds.AsSpan(0, plane);
                classes[..plane].CopyTo(bestScores);
                bestIds.Clear();
                for (int classId = 1; classId < Config.ClassCount; classId++)
                {
                    ReadOnlySpan<sbyte> values = classes.Slice(classId * plane, plane);
                    for (int cell = 0; cell < plane; cell++)
                    {
                        sbyte value = values[cell];
                        // Positive affine scale is strictly ordered over finite INT8 results:
                        // |value-zp| <= 255, including subnormal scales. Equal bytes keep the first class.
                        if (value > bestScores[cell]) { bestScores[cell] = value; bestIds[cell] = classId; }
                    }
                }
                for (int cell = 0; cell < plane; cell++)
                {
                    float bestScore = classLookup[bestScores[cell] + 128];
                    if (bestScore < Config.ConfidenceThreshold) { continue; }
                    float left = ExpectationInt8(boxes, boxLookup, gathered, cell, plane, 0);
                    float top = ExpectationInt8(boxes, boxLookup, gathered, cell, plane, 1);
                    float right = ExpectationInt8(boxes, boxLookup, gathered, cell, plane, 2);
                    float bottom = ExpectationInt8(boxes, boxLookup, gathered, cell, plane, 3);
                    float centerX = cell % width + .5f, centerY = cell / width + .5f;
                    var box = new BoundingBox((centerX - left) * head.Stride, (centerY - top) * head.Stride,
                        (centerX + right) * head.Stride, (centerY + bottom) * head.Stride);
                    if (!box.IsValid) { continue; }
                    if (!Config.MultiLabel) { candidates.Add(new(new(bestIds[cell], bestScore, box), candidates.Count)); }
                    else
                    {
                        for (int classId = 0; classId < Config.ClassCount; classId++)
                        {
                            float score = classLookup[classes[classId * plane + cell] + 128];
                            if (score >= Config.ConfidenceThreshold) { candidates.Add(new(new(classId, score, box), candidates.Count)); }
                        }
                    }
                }
            }
            var selected = Suppress(candidates);
            return Array.AsReadOnly(selected.Select(detection => detection with { Box = transform.Restore(detection.Box) })
                .Where(detection => detection.Box.IsValid).ToArray());
        }
        finally
        {
            ArrayPool<sbyte>.Shared.Return(rentedScores);
            ArrayPool<int>.Shared.Return(rentedIds);
        }
    }

    private static float ExpectationInt8(ReadOnlySpan<sbyte> values, ReadOnlySpan<float> lookup,
        Span<float> gathered, int cell, int plane, int side)
    {
        int start = side * gathered.Length * plane + cell;
        for (int bin = 0; bin < gathered.Length; bin++) { gathered[bin] = lookup[values[start + bin * plane] + 128]; }
        return Expectation(gathered);
    }

    private float Expectation(ReadOnlySpan<float> values, int cell, int plane, int side)
    {
        int start = side * Config.RegMax * plane + cell;
        int bins = Config.RegMax;
        // 每个 bin 只做一次跨 plane 读取。MathF.Max 与 Exp 的顺序和原两次扫描相同。
        Span<float> gathered = stackalloc float[bins];
        for (int bin = 0; bin < bins; bin++) { gathered[bin] = values[start + bin * plane]; }
        return Expectation(gathered);
    }

    private static float Expectation(ReadOnlySpan<float> gathered)
    {
        float maximum = gathered[0];
        for (int bin = 1; bin < gathered.Length; bin++) { maximum = MathF.Max(maximum, gathered[bin]); }
        float sum = 0, weighted = 0;
        for (int bin = 0; bin < gathered.Length; bin++)
        {
            float weight = MathF.Exp(gathered[bin] - maximum);
            sum += weight;
            weighted += bin * weight;
        }
        return weighted / sum;
    }

    internal IReadOnlyList<Detection> Suppress(List<DetectionCandidate> candidates)
    {
        candidates.Sort();
        var kept = new List<Detection>();
        foreach (var item in candidates)
        {
            Detection candidate = item.Value;
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
