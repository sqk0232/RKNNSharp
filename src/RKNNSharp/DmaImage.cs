namespace RKNNSharp;

/// <summary>独占可写的 DMA32 图像. RGA 复制和绘框均不在 CPU 映射像素.</summary>
public sealed class DmaImage : IDisposable
{
    private readonly object _gate = new();
    private readonly ImageHandle _handle;
    private readonly DmaFrame _frame;

    private DmaImage(ImageHandle handle, DmaFrame frame) { _handle = handle; _frame = frame; }

    /// <summary>分配 1..4096 的 owned DMA32 图像. NV12 必须为偶数尺寸并显式指定颜色空间, RGB 不需要 YUV 色域.</summary>
    public static DmaImage Allocate(uint width, uint height, PixelFormat format = PixelFormat.Rgb888,
        ColorSpace colorSpace = ColorSpace.Unspecified)
    {
        if (width is 0 or > 4096 || height is 0 or > 4096 || !Enum.IsDefined(format) ||
            (format == PixelFormat.Nv12 && (((width | height) & 1) != 0 ||
                colorSpace is not (ColorSpace.Bt601Limited or ColorSpace.Bt601Full or ColorSpace.Bt709Limited))))
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Dimensions must be 1..4096 (even for NV12), with a supported format.");
        }
        NativeMethods.EnsurePlatform();
        NativeMethods.Check(NativeMethods.rks_allocate_image(width, height, format, out nint owner, out var frame));
        if (format == PixelFormat.Nv12) { frame = frame with { ColorSpace = colorSpace }; }
        return new DmaImage(new ImageHandle(owner), frame);
    }

    /// <summary>同步借用, 回调结束后不得保存或使用描述符; 不得在回调中 Dispose 当前图像.</summary>
    public T WithFrame<T>(Func<DmaFrame, T> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_handle.IsClosed, this);
            bool added = false;
            try
            {
                _handle.DangerousAddRef(ref added);
                return action(_frame);
            }
            finally { if (added) { _handle.DangerousRelease(); } }
        }
    }

    /// <summary>源帧借用直至返回; 所有权属于 decoder 的帧只作为只读源.</summary>
    public void CopyFrom(DmaFrame source)
    {
        source.Validate();
        WithFrame(destination => { NativeMethods.Check(NativeMethods.rks_copy_frame(in source, in destination)); return 0; });
    }

    /// <summary>以 floor(left/top)、ceil(right/bottom) 转换并裁剪, NV12 外扩至偶数边界. 极小框由 RGA 填充标记, 可能外扩为图内 2x2/2xN.</summary>
    public unsafe void DrawRectangles(IReadOnlyList<Detection> detections, uint rgba = 0xff00ff00, int thickness = 2)
    {
        ArgumentNullException.ThrowIfNull(detections);
        if (thickness is < 1 or > 16) { throw new ArgumentOutOfRangeException(nameof(thickness)); }
        if (detections.Count > 4096) { throw new ArgumentException("Too many drawing rectangles (maximum 4096).", nameof(detections)); }
        var rectangles = new List<NativeRect>(detections.Count);
        foreach (var detection in detections)
        {
            var box = detection.Box;
            if (!box.IsValid) { throw new ArgumentException("Invalid drawing rectangle.", nameof(detections)); }
            int left = (int)Math.Clamp(MathF.Floor(box.Left), 0, _frame.Width);
            int top = (int)Math.Clamp(MathF.Floor(box.Top), 0, _frame.Height);
            int right = (int)Math.Clamp(MathF.Ceiling(box.Right), 0, _frame.Width);
            int bottom = (int)Math.Clamp(MathF.Ceiling(box.Bottom), 0, _frame.Height);
            if (_frame.Format == PixelFormat.Nv12)
            {
                // NV12 chroma spans 2x2 pixels; expand/clamp to even drawing boundaries.
                left &= ~1; top &= ~1;
                right = Math.Min((right + 1) & ~1, (int)_frame.Width);
                bottom = Math.Min((bottom + 1) & ~1, (int)_frame.Height);
            }
            if (right > left && bottom > top)
            {
                rectangles.Add(new NativeRect(left, top, right - left, bottom - top));
            }
        }
        var values = rectangles.ToArray();
        if (_frame.Format == PixelFormat.Nv12 && (thickness & 1) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thickness), "NV12 rectangle thickness must be even.");
        }
        WithFrame(frame =>
        {
            fixed (NativeRect* pointer = values)
            {
                NativeMethods.Check(NativeMethods.rks_draw_rectangles(in frame, pointer, (uint)values.Length, rgba, thickness));
            }
            return 0;
        });
    }

    public void Dispose() { lock (_gate) { _handle.Dispose(); } }
}
