namespace RKNNSharp;

/// <summary>已实现的模型输出契约. 枚举值不代表任意同名型号或导出格式均兼容.</summary>
public enum ModelType
{
    /// <summary>三个 NCHW affine INT8 raw-logit head, 显式配置每尺度三个 anchors.</summary>
    YoloV5Detection,
    /// <summary>仅支持指定 RKNN 三尺度 DFL 导出契约: 步长 8/16/32 的 NCHW affine INT8 box logits 和类别概率, 可含 score-sum.</summary>
    YoloV8Detection,
    /// <summary>原始张量推理, 仅要求原生输入契约; 不提供检测解码.</summary>
    RawTensor
}
