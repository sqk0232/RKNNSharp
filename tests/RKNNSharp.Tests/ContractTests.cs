using System.Runtime.InteropServices;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class ContractTests
{
    [Fact]
    public void NativeLayoutsMatchMeasuredArm64Abi()
    {
        Assert.Equal(48, Marshal.SizeOf<DmaFrame>());
        Assert.Equal(364, Marshal.SizeOf<NativeTensor>());
        Assert.Equal(56, Marshal.SizeOf<NativeRunInfo>());
        Assert.Equal(16, Marshal.SizeOf<NativeRect>());
        Assert.Equal(24, Marshal.OffsetOf<DmaFrame>("<SizeBytes>k__BackingField").ToInt32());
        Assert.Equal(24, Marshal.OffsetOf<NativeRunInfo>(nameof(NativeRunInfo.PreprocessMs)).ToInt32());
    }

    [Fact]
    public void BorrowedNv12ChecksFullStrideAllocationAndPlanes()
    {
        var frame = new DmaFrame(5, 640, 480, PixelFormat.Nv12, 640, 480, 614400, ColorSpace: ColorSpace.Bt601Limited);
        frame.Validate();
        Assert.Throws<ArgumentException>(() => (frame with { SizeBytes = 460799 }).Validate());
        Assert.Throws<ArgumentException>(() => (frame with { HeightStride = 479 }).Validate());
        Assert.Throws<ArgumentException>(() => (frame with { OffsetBytes = 1 }).Validate());
        Assert.Throws<ArgumentException>(() => (frame with { FileDescriptor = -1 }).Validate());
        Assert.Throws<ArgumentException>(() => (frame with { RowStrideBytes = 639 }).Validate());
        Assert.Throws<ArgumentException>(() => (frame with { Width = uint.MaxValue }).Validate());
        new DmaFrame(5, 16, 16, PixelFormat.Rgb888, 48, 16, 768, ColorSpace: 0).Validate();
        Assert.Throws<ArgumentException>(() => new DmaFrame(5, 16, 16, PixelFormat.Rgb888, 49, 16, 784).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => DmaImage.Allocate(4097, 16));
        Assert.Throws<ArgumentException>(() => (frame with { ColorSpace = ColorSpace.Unspecified }).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => DmaImage.Allocate(16, 16, PixelFormat.Nv12));
    }

    [Fact]
    public void RestoreUsesActualIndependentScalesAndClipsPadding()
    {
        var transform = new LetterboxTransform(1920, 1080, 1f / 3, 1f / 3, 0, 140);
        Assert.Equal(new BoundingBox(300, 150, 600, 450), transform.Restore(new BoundingBox(100, 190, 200, 290)));
        Assert.False(transform.Restore(new BoundingBox(10, 5, 20, 100)).IsValid);
        var odd = new LetterboxTransform(101, 57, 0.5f, 0.4f, 3, 7);
        var restored = odd.Restore(new BoundingBox(8, 11, 13, 15));
        Assert.Equal(new BoundingBox(10, 10, 20, 20), restored);
        Assert.Throws<ArgumentException>(() => (odd with { ScaleX = float.NaN }).Validate());
    }

    [Fact]
    public void NmsIsClassAwareStableAndUsesContinuousIouBoundary()
    {
        var box = new BoundingBox(0, 0, 10, 10);
        Detection[] candidates = [new(0, 0.8f, box), new(0, 0.9f, box), new(1, 0.8f, box), new(2, 0.8f, box)];
        var result = YoloV5Profile.NonMaximumSuppression(candidates, 0.45f, 3);
        Assert.Equal(new[] { 0, 1, 2 }, result.Select(item => item.ClassId));
        Assert.Equal(0.9f, result[0].Confidence);
        Assert.Equal(0, YoloV5Profile.IntersectionOverUnion(box, new BoundingBox(10, 0, 20, 10)));
        Assert.Equal(1, YoloV5Profile.IntersectionOverUnion(box, box));
        Assert.Equal(2, YoloV5Profile.NonMaximumSuppression(candidates.Take(2).ToArray(), 1, 10).Count);
        Assert.Single(YoloV5Profile.NonMaximumSuppression(candidates, 0.45f, 1));
    }
}
