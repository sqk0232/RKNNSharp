namespace RKNNSharp;

// 原生会话边界. 测试可注入受控会话, 无需加载 ARM64 库或访问真实设备.
internal interface IRknnSession : IDisposable
{
    IReadOnlyList<TensorInfo> Inputs { get; }
    IReadOnlyList<TensorInfo> Outputs { get; }
    string RuntimeVersions { get; }
    NpuCoreMask CoreMask { get; }
    FloatOutputMode OutputMode { get; }
    CallerThreadPlacement? CallerThreadPlacement { get; }
    InferenceResult Run(DmaFrame frame);
    InferenceResult RunInto(DmaFrame frame, IReadOnlyList<Memory<float>> outputBuffers);
    DetectionResult Detect(DmaFrame frame, YoloV5Profile profile);
    DetectionResult Detect(DmaFrame frame, YoloDetectionProfile profile);
}
