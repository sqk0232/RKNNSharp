using RKNNSharp;

namespace RKNNSharp.Smoke;

// 示例假设必须与实际模型匹配; 不根据文件名或型号猜测 anchors 和输出映射.
internal static class ExampleConfigurations
{
    internal static RknnPipelineConfig V5(float confidence = .25f) => new(640, 640, 80,
        anchors:
        [
            [new(10, 13), new(16, 30), new(33, 23)],
            [new(30, 61), new(62, 45), new(59, 119)],
            [new(116, 90), new(156, 198), new(373, 326)]
        ], confidenceThreshold: confidence);

    internal static RknnPipelineConfig Dfl() => new(640, 640, 80,
        heads: [new(0, 1, 2, 8), new(3, 4, 5, 16), new(6, 7, 8, 32)]);
}
