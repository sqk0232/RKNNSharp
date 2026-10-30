using Xunit;

namespace RKNNSharp.Tests;

public sealed class NativeGeometryTests
{
    private static readonly TensorInfo Input = new(0, "images", [1, 640, 640, 3], 640 * 640 * 3, 1, 2, 2, -128, 1f / 255);

    [Theory]
    [InlineData(2560, 1440, 640, 360, 0, 140)]
    [InlineData(256, 101, 640, 253, 0, 193)]
    [InlineData(640, 637, 640, 637, 0, 1)]
    [InlineData(641, 479, 640, 478, 0, 81)]
    [InlineData(1, 8192, 2, 640, 319, 0)] // Geometry only; not a claim that RGA accepts this source.
    public void PreservesNativeRoundingMin2AndActualScales(int width, int height, int resizedWidth, int resizedHeight, int left, int top)
    {
        var source = new DmaFrame(5, (uint)width, (uint)height, PixelFormat.Rgb888, (uint)width * 3, (uint)height, (ulong)width * (uint)height * 3);
        var info = new NativeRunInfo { ResizeWidth = resizedWidth, ResizeHeight = resizedHeight, PadLeft = left, PadTop = top,
            ScaleX = (float)resizedWidth / width, ScaleY = (float)resizedHeight / height };
        Assert.Equal(new LetterboxTransform(width, height, info.ScaleX, info.ScaleY, left, top), NativeLetterboxGeometry.Create(source, Input, in info));
    }

    [Fact]
    public void ReadsNonSquareNhwcTargetHeightAndWidthInTheirDeclaredOrder()
    {
        var input = new TensorInfo(0, "images", [1, 384, 640, 3], 384 * 640 * 3, 1, 2, 2, -128, 1f / 255);
        var source = new DmaFrame(5, 1280, 720, PixelFormat.Nv12, 1280, 720, 1843200, ColorSpace: ColorSpace.Bt601Full);
        var info = new NativeRunInfo { ResizeWidth = 640, ResizeHeight = 360, PadTop = 12, ScaleX = .5f, ScaleY = .5f };
        Assert.Equal(new LetterboxTransform(1280, 720, .5f, .5f, 0, 12), NativeLetterboxGeometry.Create(source, input, in info));
    }

    [Fact]
    public void RejectsEachCorruptedNativeGeometryFieldAndInvalidStaticMetadata()
    {
        var source = new DmaFrame(5, 2560, 1440, PixelFormat.Nv12, 2560, 1440, 7372800, ColorSpace: ColorSpace.Bt601Full);
        var valid = new NativeRunInfo { ResizeWidth = 640, ResizeHeight = 360, PadTop = 140, ScaleX = .25f, ScaleY = .25f };
        var invalid = new List<NativeRunInfo>();
        var value = valid; value.ResizeWidth = 639; invalid.Add(value);
        value = valid; value.ResizeHeight = 361; invalid.Add(value);
        value = valid; value.PadLeft = 1; invalid.Add(value);
        value = valid; value.PadTop = 139; invalid.Add(value);
        value = valid; value.ScaleX = MathF.BitIncrement(.25f); invalid.Add(value);
        value = valid; value.ScaleY = MathF.BitDecrement(.25f); invalid.Add(value);
        value = valid; value.ScaleX = float.NaN; invalid.Add(value);
        value = valid; value.ScaleY = float.PositiveInfinity; invalid.Add(value);
        value = valid; value.ResizeWidth = 1; invalid.Add(value);
        foreach (var broken in invalid) { Assert.Throws<RknnException>(() => NativeLetterboxGeometry.Create(source, Input, in broken)); }
        var badInput = new TensorInfo(0, "bad", [1, 3, 640, 640], 640 * 640 * 3, 0, 2, 2, -128, 1f / 255);
        Assert.Throws<RknnException>(() => NativeLetterboxGeometry.Create(source, badInput, in valid));
        Assert.Throws<RknnException>(() => NativeLetterboxGeometry.Create(source with { Width = 0 }, Input, in valid));
        // Public callers can still supply non-centered or independently scaled transforms.
        new LetterboxTransform(101, 57, .5f, .4f, 3, 7).Validate();
    }
}
