using System.Runtime.InteropServices;

namespace RKNNSharp;

public enum PixelFormat : uint { Nv12 = 1, Rgb888 = 2, Rgba8888 = 3 }
public enum ColorSpace : uint { Unspecified = 0, Bt601Limited = 1, Bt601Full = 2, Bt709Limited = 3 }

/// <summary>借用的单 FD、线性 DMA 图像. 调用期间生产者必须完成写入并保留 FD/底层 sample, 不转移所有权.</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct DmaFrame(
    int FileDescriptor, uint Width, uint Height, PixelFormat Format,
    uint RowStrideBytes, uint HeightStride, ulong SizeBytes,
    ulong OffsetBytes = 0, ColorSpace ColorSpace = ColorSpace.Unspecified, uint Reserved = 0)
{
    /// <summary>只验证描述符的可表示布局; FD 的有效性/硬件兼容性由原生层验证.</summary>
    public void Validate()
    {
        if (FileDescriptor < 0 || Width == 0 || Height == 0 || Width > 8192 || Height > 8192 ||
            HeightStride < Height || HeightStride > 8192 || OffsetBytes != 0 || Reserved != 0)
        {
            throw new ArgumentException("Frame requires a valid borrowed FD, positive dimensions <=8192, linear offset zero and valid height stride.");
        }
        ulong minimumStride = Format switch
        {
            PixelFormat.Nv12 => Width,
            PixelFormat.Rgb888 => (ulong)Width * 3,
            PixelFormat.Rgba8888 => (ulong)Width * 4,
            _ => throw new ArgumentException("Unsupported DMA pixel format.")
        };
        if (RowStrideBytes < minimumStride || RowStrideBytes > 32768 ||
            (Format == PixelFormat.Rgb888 && RowStrideBytes % 3 != 0) ||
            (Format == PixelFormat.Rgba8888 && RowStrideBytes % 4 != 0) ||
            (Format == PixelFormat.Nv12 && ((Width | Height | HeightStride | RowStrideBytes) & 1) != 0) ||
            (Format == PixelFormat.Nv12 && ColorSpace is not (ColorSpace.Bt601Limited or ColorSpace.Bt601Full or ColorSpace.Bt709Limited)))
        {
            throw new ArgumentException("Invalid stride, NV12 alignment or color space.");
        }
        ulong required = (ulong)RowStrideBytes * HeightStride;
        if (Format == PixelFormat.Nv12) { required += required / 2; }
        if (SizeBytes < required || SizeBytes > int.MaxValue) { throw new ArgumentException("DMA allocation size is outside the supported plane/hardware bounds."); }
    }
}
