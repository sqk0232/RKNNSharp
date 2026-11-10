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
        RknnPipelineConfig config = SingleFrameSample.CreateExplicitDfl640Example();
        Assert.Equal(640, config.InputWidth);
        Assert.Equal(640, config.InputHeight);
        Assert.Equal(80, config.ClassCount);
        Assert.Equal(16, config.RegMax);
        Assert.Equal(0.25f, config.ConfidenceThreshold);
        Assert.Equal(0.45f, config.NmsThreshold);
        Assert.False(config.MultiLabel);
        Assert.Equal(300, config.MaxDetections);
        Assert.Equal(ModelType.YoloV8Detection, config.Contract);
        Assert.Equal([8, 16, 32], config.Heads.Select(head => head.Stride));
        Assert.Equal([0, 3, 6], config.Heads.Select(head => head.BoxOutputIndex));
        Assert.Equal([1, 4, 7], config.Heads.Select(head => head.ClassOutputIndex));
        Assert.Equal([2, 5, 8], config.Heads.Select(head => head.ScoreSumOutputIndex));

        var input = new TensorInfo(0, "images", [1, 640, 640, 3], 640u * 640 * 3, 1, 2, 2, -128, 1f / 255f);
        var outputs = new TensorInfo[9];
        foreach (YoloDetectionHead head in config.Heads)
        {
            uint grid = (uint)(config.InputWidth / head.Stride);
            outputs[head.BoxOutputIndex] = Output(head.BoxOutputIndex, 4 * config.RegMax, grid);
            outputs[head.ClassOutputIndex] = Output(head.ClassOutputIndex, config.ClassCount, grid);
            outputs[head.ScoreSumOutputIndex!.Value] = Output(head.ScoreSumOutputIndex.Value, 1, grid);
        }
        config.DflProfile!.ValidateModel([input], outputs);
    }

    private static TensorInfo Output(int index, int channels, uint grid) =>
        new((uint)index, $"output{index}", [1, (uint)channels, grid, grid], (uint)channels * grid * grid, 0, 2, 2, 0, 0.01f);
}
