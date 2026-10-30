using System.Security.Cryptography;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class ModelIntegrityTests
{
    [Fact]
    public void NullHashDoesNotInspectTheFileAndOldConstructorSignatureIsPreserved()
    {
        ModelFileIntegrity.Verify(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "absent.rknn"), null);
        var old = typeof(RknnModel).GetConstructor([typeof(string), typeof(NpuCoreMask)]);
        Assert.NotNull(old);
        Assert.True(old.GetParameters()[1].IsOptional);
        Assert.Equal(NpuCoreMask.Auto, old.GetParameters()[1].DefaultValue);
        var added = typeof(RknnModel).GetConstructor([typeof(string), typeof(NpuCoreMask), typeof(string)]);
        Assert.NotNull(added);
        Assert.False(added.GetParameters()[2].IsOptional);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void InvalidDigestFailsBeforeFileOrNativeAccess(string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => new RknnModel("missing.rknn", NpuCoreMask.Auto, expected));
        Assert.Equal("expectedModelSha256", error.ParamName);
    }

    [Fact]
    public void CaseInsensitiveHashIsFreshAndFailureDoesNotPoisonNextVerification()
    {
        string path = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            string expected = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            ModelFileIntegrity.Verify(path, expected.ToLowerInvariant());
            ModelFileIntegrity.Verify(path, expected.ToUpperInvariant());
            Assert.Throws<InvalidDataException>(() => new RknnModel(path, NpuCoreMask.Auto, new string('0', 64)));
            ModelFileIntegrity.Verify(path, expected);
            File.WriteAllBytes(path, [5, 6, 7, 8]);
            Assert.Throws<InvalidDataException>(() => ModelFileIntegrity.Verify(path, expected));
            string next = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            ModelFileIntegrity.Verify(path, next);
            // A failed check closes its stream; the file can immediately be opened exclusively.
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        finally { File.Delete(path); }
    }
}
