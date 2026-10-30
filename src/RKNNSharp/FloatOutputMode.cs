namespace RKNNSharp;

/// <summary>Run/RunInto的浮点转换策略, 不改变Detect的INT8路径或返回缓冲区所有权.</summary>
public enum FloatOutputMode : uint
{
    /// <summary>使用RKNN SDK的浮点转换, 保留现有默认行为与SDK浮点参考路径.</summary>
    SdkExact = 0,
    /// <summary>允许对满足条件的标准affine INT8输出使用快速转换, 其他head仍由SDK转换. 舍入结果可能不同.</summary>
    /// <remarks>不保证与SDK逐bit一致, 也不保证任意量化参数或置信度/NMS阈值边界处的全部决策相同.</remarks>
    PreferFastInt8 = 1
}
