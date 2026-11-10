namespace RKNNSharp;

/// <summary>模型要求的通道顺序, 与源 DMA 帧像素格式分别配置.</summary>
public enum ChannelOrder
{
    Rgb,
    Bgr
}

public enum ResizeMode
{
    /// <summary>保持比例, 居中填充 RGB 114, 使用原生 RGA 对齐后的实际变换.</summary>
    Letterbox,
    Stretch
}

public enum NormalizationMode
{
    /// <summary>沿用模型量化契约, 不额外施加均值/方差. 当前要求 affine INT8, zp=-128, 精确 float(1/255).</summary>
    ModelDefault,
    None,
    DivideBy255
}
