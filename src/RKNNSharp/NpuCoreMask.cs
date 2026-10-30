namespace RKNNSharp;

/// <summary>显式选择 RKNN 会话使用的 NPU 核心. 多核 mask 不保证降低单次推理延迟.</summary>
public enum NpuCoreMask : uint
{
    Auto = 0,
    Core0 = 1,
    Core1 = 2,
    Core0AndCore1 = 3,
    Core2 = 4,
    All = 7
}
