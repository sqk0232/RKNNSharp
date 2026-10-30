using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using RKNNSharp;

namespace RKNNSharp.Smoke;

// This adapter belongs to the sample. The library has no GStreamer dependency.
internal sealed unsafe class GStreamerCapture : IDisposable
{
    private readonly CaptureHandle _handle;
    internal GStreamerCapture(string source, string codec, bool rtsp, bool fileUnpaced = false)
    {
        byte* error = stackalloc byte[1024];
        int status = rtsp
            ? GstMethods.rkgst_open_rtsp(source, codec, 200, out nint pointer, error, 1024)
            : fileUnpaced ? GstMethods.rkgst_open_file_unpaced(source, codec, out pointer, error, 1024)
            : GstMethods.rkgst_open_file(source, codec, out pointer, error, 1024);
        if (status != 0) { throw new InvalidOperationException(ErrorText(error)); }
        _handle = new CaptureHandle(pointer);
    }

    internal GstFrame Pull()
    {
        GstFrame frame = default;
        byte* error = stackalloc byte[1024];
        int status = GstMethods.rkgst_pull(_handle, 5000, ref frame, error, 1024);
        return status switch
        {
            1 => frame,
            0 => throw new TimeoutException("Timed out waiting for a hardware-decoded DMA frame."),
            2 => throw new EndOfStreamException("Video reached EOS."),
            _ => throw new InvalidOperationException(ErrorText(error))
        };
    }

    internal void Pause()
    {
        byte* error = stackalloc byte[1024];
        if (GstMethods.rkgst_pause(_handle, error, 1024) != 0) { throw new InvalidOperationException(ErrorText(error)); }
    }

    private static string ErrorText(byte* error)
    {
        int size = 0;
        while (size < 1024 && error[size] != 0) { size++; }
        return Encoding.UTF8.GetString(error, size);
    }

    internal static DmaFrame Borrow(in GstFrame frame, ColorSpace? colorSpaceOverride)
    {
        if (frame.StructSize != 112 || frame.Format != 0x3231564e || frame.PlaneCount != 2 || frame.Token == 0 ||
            frame.Fd0 < 0 || frame.Fd0 != frame.Fd1 || frame.Stride0 <= 0 || frame.Stride0 != frame.Stride1 ||
            frame.Offset0 != 0 || frame.Offset1 % (uint)frame.Stride0 != 0 ||
            frame.Offset1 / (uint)frame.Stride0 > uint.MaxValue || frame.Allocation0 != frame.Allocation1)
        {
            throw new InvalidOperationException("Unsupported decoder DMA layout; refusing CPU fallback.");
        }
        var colorSpace = colorSpaceOverride ?? (ColorSpace)frame.ColorSpace;
        if (colorSpace is not (ColorSpace.Bt601Limited or ColorSpace.Bt601Full or ColorSpace.Bt709Limited))
        {
            throw new InvalidOperationException("Decoder caps did not declare a supported color space; an explicit override is required.");
        }
        var result = new DmaFrame(frame.Fd0, frame.Width, frame.Height, PixelFormat.Nv12,
            (uint)frame.Stride0, (uint)(frame.Offset1 / (uint)frame.Stride0), frame.Allocation0, 0, colorSpace);
        result.Validate();
        return result;
    }

    internal static void Release(ref GstFrame frame) => GstMethods.rkgst_release(ref frame);
    public void Dispose() => _handle.Dispose();
}

[StructLayout(LayoutKind.Sequential)]
internal struct GstFrame
{
    internal uint StructSize, Width, Height, Format, PlaneCount, ColorSpace;
    internal int Fd0, Fd1, Stride0, Stride1;
    internal ulong Offset0, Offset1, Allocation0, Allocation1;
    internal ulong PtsNs, DtsNs, RunningTimeNs, ArrivalMonotonicNs;
    internal nint Token;
}

internal sealed class CaptureHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal CaptureHandle(nint pointer) : base(true) => SetHandle(pointer);
    protected override bool ReleaseHandle() { GstMethods.rkgst_close(handle); return true; }
}

internal static unsafe partial class GstMethods
{
    private const string Library = "rknnsharp_gst";
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int rkgst_open_rtsp(string url, string codec, uint latencyMs, out nint capture, byte* error, uint errorSize);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int rkgst_open_file(string path, string codec, out nint capture, byte* error, uint errorSize);
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int rkgst_open_file_unpaced(string path, string codec, out nint capture, byte* error, uint errorSize);
    [LibraryImport(Library)]
    internal static partial int rkgst_pull(CaptureHandle capture, uint timeoutMs, ref GstFrame frame, byte* error, uint errorSize);
    [LibraryImport(Library)] internal static partial void rkgst_release(ref GstFrame frame);
    [LibraryImport(Library)] internal static partial void rkgst_close(nint capture);
    [LibraryImport(Library)] internal static partial int rkgst_pause(CaptureHandle capture, byte* error, uint errorSize);
}
