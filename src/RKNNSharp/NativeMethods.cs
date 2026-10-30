using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RKNNSharp;

internal static unsafe partial class NativeMethods
{
    private const string Library = "rknnsharp_native";

    [LibraryImport(Library)] internal static partial uint rks_abi_version();
    [LibraryImport(Library)] private static partial uint rks_capabilities();
    [LibraryImport(Library)] internal static partial nint rks_last_error();
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int rks_create(string modelPath, out nint session);
    [LibraryImport(Library)] internal static partial void rks_destroy(nint session);
    [LibraryImport(Library)] internal static partial int rks_get_tensor_count(SessionHandle session, out uint inputs, out uint outputs);
    [LibraryImport(Library)] internal static partial int rks_get_tensor(SessionHandle session, uint isOutput, uint index, out NativeTensor tensor);
    [LibraryImport(Library)] internal static partial int rks_get_versions(SessionHandle session, byte* utf8, uint capacity);
    [LibraryImport(Library)] internal static partial int rks_run(SessionHandle session, in DmaFrame frame, out NativeRunInfo info);
    [LibraryImport(Library)] internal static partial int rks_run_float_into(SessionHandle session, in DmaFrame frame,
        float** outputs, uint* counts, uint outputCount, out NativeRunInfo info);
    [LibraryImport(Library)] internal static partial int rks_run_int8(SessionHandle session, in DmaFrame frame, out NativeRunInfo info);
    [LibraryImport(Library)] internal static partial int rks_borrow_output_int8(SessionHandle session, uint index, out nint values, out uint count, out ulong generation);
    [LibraryImport(Library)] internal static partial int rks_set_core_mask(SessionHandle session, uint mask);
    [LibraryImport(Library)] internal static partial int rks_set_float_output_mode(SessionHandle session, uint mode);
    [LibraryImport(Library)] internal static partial int rks_copy_output(SessionHandle session, uint index, float* values, uint count);
    [LibraryImport(Library)] internal static partial int rks_allocate_image(uint width, uint height, PixelFormat format, out nint owner, out DmaFrame frame);
    [LibraryImport(Library)] internal static partial void rks_release_image(nint owner);
    [LibraryImport(Library)] internal static partial int rks_copy_frame(in DmaFrame source, in DmaFrame destination);
    [LibraryImport(Library)] internal static partial int rks_draw_rectangles(in DmaFrame frame, NativeRect* rectangles, uint count, uint rgba, int thickness);

    // One probe per loaded managed assembly; ABI2 libraries without the optional export keep the original Run path.
    private static readonly Lazy<bool> DirectFloatSupport = new(() =>
    {
        try { return (rks_capabilities() & 1u) != 0; }
        catch (EntryPointNotFoundException) { return false; }
    });
    internal static bool SupportsDirectFloat => DirectFloatSupport.Value;

    internal static void Check(int status)
    {
        if (status != 0)
        {
            throw new RknnException(Marshal.PtrToStringUTF8(rks_last_error()) ?? "RKNN/RGA native operation failed.");
        }
    }

    internal static void EnsurePlatform()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64)
        {
            throw new PlatformNotSupportedException("RKNNSharp native operations require Linux ARM64 on RK3588.");
        }
        if (rks_abi_version() != 2) { throw new RknnException("Unsupported rknnsharp native ABI; expected version 2."); }
    }
}

internal sealed class SessionHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal SessionHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { NativeMethods.rks_destroy(handle); return true; }
}

internal sealed class ImageHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    internal ImageHandle(nint value) : base(true) => SetHandle(value);
    protected override bool ReleaseHandle() { NativeMethods.rks_release_image(handle); return true; }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct NativeTensor
{
    internal uint Index, DimensionCount;
    internal fixed uint Dimensions[16];
    internal uint ElementCount;
    internal int Layout, DataType, Quantization, ZeroPoint;
    internal float Scale;
    internal uint SizeBytes, WidthStride, SizeWithStride;
    internal fixed byte Name[256];

    internal TensorInfo ToInfo()
    {
        if (DimensionCount is 0 or > 16) { throw new RknnException("Invalid native tensor rank."); }
        var dimensions = new uint[DimensionCount];
        for (int i = 0; i < dimensions.Length; i++) { dimensions[i] = Dimensions[i]; }
        fixed (byte* name = Name)
        {
            int count = 0;
            while (count < 256 && name[count] != 0) { count++; }
            return new TensorInfo(Index, Encoding.UTF8.GetString(name, count), dimensions, ElementCount,
                Layout, DataType, Quantization, ZeroPoint, Scale);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct NativeRunInfo
{
    internal float ScaleX, ScaleY;
    internal int PadLeft, PadTop, ResizeWidth, ResizeHeight;
    internal double PreprocessMs, InferenceMs, OutputsMs;
    internal int InputDmaFd;
    internal uint InputStrideBytes;
}

[StructLayout(LayoutKind.Sequential)]
internal readonly record struct NativeRect(int X, int Y, int Width, int Height);
