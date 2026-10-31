using RKNNSharp.Smoke;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class SingleFrameSampleTests
{
    [Fact]
    public void HelpDocumentsTheDflSingleFrameDecoder()
    {
        Assert.Contains("decoder=v5|dfl", SingleFrameSample.Help, StringComparison.Ordinal);
        Assert.Contains("dfl", SingleFrameSample.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingFilesStillAcceptTheDefaultAndExplicitDecoders()
    {
        Assert.Throws<FileNotFoundException>(() => SingleFrameSample.Run(["missing.rknn", "--single-frame", "--file", "missing.mp4"]));
        Assert.Throws<FileNotFoundException>(() => SingleFrameSample.Run(
            ["missing.rknn", "--single-frame", "--file", "missing.mp4", "h264", "auto", "v5"]));
        Assert.Throws<FileNotFoundException>(() => SingleFrameSample.Run(
            ["missing.rknn", "--single-frame", "--rtsp-file", "missing.url", "h265", "709limit", "dfl"]));
    }

    [Theory]
    [InlineData("missing.rknn", "--single-frame", "--file")]
    [InlineData("missing.rknn", "--single-frame", "--file", "missing.mp4", "h264", "auto", "dfl", "extra")]
    [InlineData("missing.rknn", "--single-frame", "--camera", "missing.mp4")]
    [InlineData("missing.rknn", "--single-frame", "--file", "missing.mp4", "mjpeg")]
    [InlineData("missing.rknn", "--single-frame", "--file", "missing.mp4", "h264", "bt2020")]
    [InlineData("missing.rknn", "--single-frame", "--file", "missing.mp4", "h264", "auto", "yolov8")]
    public void InvalidSingleFrameArgumentsFailBeforeFileAccess(params string[] arguments)
    {
        Assert.Throws<ArgumentException>(() => SingleFrameSample.Run(arguments));
    }

    [Fact]
    public void DflExampleIsTheDocumentedNineOutputContract()
    {
        YoloDetectionProfile profile = SingleFrameSample.CreateExplicitDfl640Example();
        Assert.Equal(640, profile.Config.InputWidth);
        Assert.Equal(640, profile.Config.InputHeight);
        Assert.Equal(80, profile.Config.ClassCount);
        Assert.Equal(16, profile.Config.RegMax);
        Assert.Equal(0.25f, profile.Config.ConfidenceThreshold);
        Assert.Equal(0.45f, profile.Config.NmsThreshold);
        Assert.False(profile.Config.MultiLabel);
        Assert.Equal(300, profile.Config.MaxDetections);
        Assert.Equal(YoloDetectionOutputFormat.RknnDfl, profile.Config.OutputFormat);
        Assert.Equal([8, 16, 32], profile.Config.Heads.Select(head => head.Stride));
        Assert.Equal([0, 3, 6], profile.Config.Heads.Select(head => head.BoxOutputIndex));
        Assert.Equal([1, 4, 7], profile.Config.Heads.Select(head => head.ClassOutputIndex));
        Assert.Equal([2, 5, 8], profile.Config.Heads.Select(head => head.ScoreSumOutputIndex));

        var input = new TensorInfo(0, "images", [1, 640, 640, 3], 640u * 640 * 3, 1, 2, 2, -128, 1f / 255f);
        var outputs = new TensorInfo[9];
        foreach (YoloDetectionHead head in profile.Config.Heads)
        {
            uint grid = (uint)(profile.Config.InputWidth / head.Stride);
            outputs[head.BoxOutputIndex] = Output(head.BoxOutputIndex, 4 * profile.Config.RegMax, grid);
            outputs[head.ClassOutputIndex] = Output(head.ClassOutputIndex, profile.Config.ClassCount, grid);
            outputs[head.ScoreSumOutputIndex!.Value] = Output(head.ScoreSumOutputIndex.Value, 1, grid);
        }
        profile.ValidateModel([input], outputs);
    }

    private static TensorInfo Output(int index, int channels, uint grid) =>
        new((uint)index, $"output{index}", [1, (uint)channels, grid, grid], (uint)channels * grid * grid, 0, 2, 2, 0, 0.01f);
}
