using System.Security.Cryptography;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class ModelIntegrityTests
{
    [Fact]
    public void NullHashDoesNotInspectTheFileAndRemainsTheDefault()
    {
        ModelFileIntegrity.Verify(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString(), "absent.rknn"), null);
        Assert.Null(new RknnModelOptions().ExpectedModelSha256);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("abc")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void InvalidDigestFailsBeforeFileOrNativeAccess(string expected)
    {
        var error = Assert.Throws<ArgumentException>(() => new RknnPipeline(ModelType.RawTensor, "missing.rknn", options: new RknnModelOptions { ExpectedModelSha256 = expected }));
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
            Assert.Throws<InvalidDataException>(() => new RknnPipeline(ModelType.RawTensor, path, options: new RknnModelOptions { ExpectedModelSha256 = new string('0', 64) }));
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
