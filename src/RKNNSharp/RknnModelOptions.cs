using Microsoft.Extensions.Logging;

namespace RKNNSharp;

/// <summary>创建会话时的可选配置. 默认保持原有调度和输出行为.</summary>
public sealed class RknnModelOptions
{
    /// <summary>构造时创建的固定会话数. 耗尽时等待, 默认 1, 不自动分配不同核心.</summary>
    public int SessionPoolSize { get; init; } = 1;
    public NpuCoreMask CoreMask { get; init; } = NpuCoreMask.Auto;
    public string? ExpectedModelSha256 { get; init; }
    public FloatOutputMode OutputMode { get; init; } = FloatOutputMode.SdkExact;
    public CallerThreadPlacement? CallerThreadPlacement { get; init; }
    /// <summary>调用方拥有工厂的生命周期; 模型不会释放它.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }
}
