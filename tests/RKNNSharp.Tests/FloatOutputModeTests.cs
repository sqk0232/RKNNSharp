using Xunit;

namespace RKNNSharp.Tests;

public sealed class FloatOutputModeTests
{
    [Fact]
    public void UnifiedPipelineKeepsFloatModeContract()
    {
        Assert.Equal(0u, (uint)FloatOutputMode.SdkExact);
        Assert.Equal(1u, (uint)FloatOutputMode.PreferFastInt8);
        Assert.Equal(FloatOutputMode.SdkExact, new RknnModelOptions().OutputMode);
        Assert.Equal(typeof(FloatOutputMode), typeof(RknnPipeline).GetProperty(nameof(RknnPipeline.OutputMode))!.PropertyType);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void UnknownModeIsRejectedBeforeFileOrNativeAccess(uint mode)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RknnPipeline(ModelType.RawTensor, "absent-model.rknn", options: new RknnModelOptions { OutputMode = (FloatOutputMode)mode }));
        Assert.Equal("outputMode", error.ParamName);
    }

    [Theory]
    [InlineData(FloatOutputMode.SdkExact)]
    [InlineData(FloatOutputMode.PreferFastInt8)]
    public void ModeDoesNotBypassModelHashValidation(FloatOutputMode mode)
    {
        var malformed = Assert.Throws<ArgumentException>(() =>
            new RknnPipeline(ModelType.RawTensor, "absent-model.rknn", options: new RknnModelOptions { ExpectedModelSha256 = "not-a-sha256", OutputMode = mode }));
        Assert.Equal("expectedModelSha256", malformed.ParamName);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Throws<InvalidDataException>(() => new RknnPipeline(ModelType.RawTensor, path, options: new RknnModelOptions { ExpectedModelSha256 = new string('0', 64), OutputMode = mode }));
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(FloatOutputMode.SdkExact)]
    [InlineData(FloatOutputMode.PreferFastInt8)]
    public void ModeDoesNotBypassMissingModelValidation(FloatOutputMode mode)
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent-model.rknn");
        var error = Assert.Throws<FileNotFoundException>(() => new RknnPipeline(ModelType.RawTensor, missing, options: new RknnModelOptions { OutputMode = mode }));
        Assert.Equal(missing, error.FileName);
    }

    [Fact]
    public void DefaultOptionsStillRejectMissingModel()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent-model.rknn");
        var error = Assert.Throws<FileNotFoundException>(() => new RknnPipeline(ModelType.RawTensor, missing));
        Assert.Equal(missing, error.FileName);
    }
}
