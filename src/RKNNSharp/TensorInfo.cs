namespace RKNNSharp;

/// <summary>RKNN 标准 tensor 属性. 输出 Values 已按会话模式转换为 float, 不再次反量化; 默认使用SDK转换.</summary>
public sealed class TensorInfo
{
    public uint Index { get; }
    public string Name { get; }
    public IReadOnlyList<uint> Dimensions { get; }
    public uint ElementCount { get; }
    public int Layout { get; }
    public int DataType { get; }
    public int Quantization { get; }
    public int ZeroPoint { get; }
    public float Scale { get; }

    public TensorInfo(uint index, string name, IEnumerable<uint> dimensions, uint elementCount,
        int layout, int dataType, int quantization, int zeroPoint, float scale)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(dimensions);
        Index = index; Name = name; Dimensions = Array.AsReadOnly(dimensions.ToArray());
        ElementCount = elementCount; Layout = layout; DataType = dataType;
        Quantization = quantization; ZeroPoint = zeroPoint; Scale = scale;
    }
}

public sealed record TensorOutput(TensorInfo Info, ReadOnlyMemory<float> Values);
/// <summary>RuntimeOutputsMs 是原生输出获取/转换/释放. ManagedOutputsMs 是 Run 的快照分配/固定/复制, RunInto 的验证/固定/兼容复制, 或 Detect 的指针/代数校验.</summary>
public readonly record struct RunTimings(double PreprocessMs, double InferenceMs, double RuntimeOutputsMs, double ManagedOutputsMs);
public sealed record InferenceResult(IReadOnlyList<TensorOutput> Outputs, LetterboxTransform Transform, RunTimings Timings);
