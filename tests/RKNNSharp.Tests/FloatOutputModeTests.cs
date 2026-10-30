using Xunit;

namespace RKNNSharp.Tests;

public sealed class FloatOutputModeTests
{
    [Fact]
    public void ExplicitModesPreserveExistingConstructorSignatures()
    {
        Assert.Equal(0u, (uint)FloatOutputMode.SdkExact);
        Assert.Equal(1u, (uint)FloatOutputMode.PreferFastInt8);
        var original = typeof(RknnModel).GetConstructor([typeof(string), typeof(NpuCoreMask)]);
        Assert.NotNull(original);
        Assert.True(original.GetParameters()[1].IsOptional);
        Assert.Equal(NpuCoreMask.Auto, original.GetParameters()[1].DefaultValue);
        Assert.NotNull(typeof(RknnModel).GetConstructor([typeof(string), typeof(NpuCoreMask), typeof(string)]));
        var explicitMode = typeof(RknnModel).GetConstructor([typeof(string), typeof(NpuCoreMask), typeof(string), typeof(FloatOutputMode)]);
        Assert.NotNull(explicitMode);
        Assert.False(explicitMode.GetParameters()[3].IsOptional);
        Assert.Equal(typeof(FloatOutputMode), typeof(RknnModel).GetProperty(nameof(RknnModel.OutputMode))!.PropertyType);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void UnknownModeIsRejectedBeforeFileOrNativeAccess(uint mode)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new RknnModel("absent-model.rknn", NpuCoreMask.Auto, null, (FloatOutputMode)mode));
        Assert.Equal("outputMode", error.ParamName);
    }

    [Theory]
    [InlineData(FloatOutputMode.SdkExact)]
    [InlineData(FloatOutputMode.PreferFastInt8)]
    public void ModeDoesNotBypassModelHashValidation(FloatOutputMode mode)
    {
        var malformed = Assert.Throws<ArgumentException>(() =>
            new RknnModel("absent-model.rknn", NpuCoreMask.Auto, "not-a-sha256", mode));
        Assert.Equal("expectedModelSha256", malformed.ParamName);
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            Assert.Throws<InvalidDataException>(() => new RknnModel(path, NpuCoreMask.Auto, new string('0', 64), mode));
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
        var error = Assert.Throws<FileNotFoundException>(() => new RknnModel(missing, NpuCoreMask.Auto, null, mode));
        Assert.Equal(missing, error.FileName);
    }

    [Fact]
    public void ExistingDefaultLiteralHashCallRemainsUnambiguous()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "absent-model.rknn");
        var error = Assert.Throws<FileNotFoundException>(() => new RknnModel(missing, NpuCoreMask.Auto, default));
        Assert.Equal(missing, error.FileName);
    }
}
