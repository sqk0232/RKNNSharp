using Xunit;

namespace RKNNSharp.Tests;

public sealed class Int8DecodeTests
{
    private static YoloV5Profile Profile(float threshold = 0.25f) => new(32, 32, 3,
        [[new(1, 2), new(4, 2), new(8, 10)], [new(3, 6), new(6, 4), new(9, 11)], [new(11, 9), new(15, 19), new(37, 32)]], threshold);

    private static (TensorInfo[] Infos, sbyte[][] Buffers) Heads()
    {
        var infos = new TensorInfo[3];
        var buffers = new sbyte[3][];
        for (int head = 0; head < 3; head++)
        {
            uint grid = (uint)(4 >> head), count = 24 * grid * grid;
            infos[head] = new((uint)head, "head", [1, 24, grid, grid], count, 0, 2, 2, -10 + head * 10, 0.1f + head * 0.03f);
            buffers[head] = new sbyte[count];
            Array.Fill(buffers[head], (sbyte)-128);
        }
        return (infos, buffers);
    }

    private static IReadOnlyList<Detection> FloatReference(YoloV5Profile profile, TensorInfo[] infos, sbyte[][] buffers, LetterboxTransform transform)
    {
        var outputs = new TensorOutput[3];
        for (int head = 0; head < 3; head++)
        {
            var info = infos[head];
            var values = buffers[head].Select(value => (value - info.ZeroPoint) * info.Scale).ToArray();
            outputs[head] = new(info, values);
        }
        return profile.Decode(outputs, transform);
    }

    [Fact]
    public void RawDecodeMatchesIndependentFloatDequantizationAcrossAllHeads()
    {
        var profile = Profile();
        var transform = new LetterboxTransform(32, 32, 1, 1, 0, 0);
        for (int seed = 0; seed < 20; seed++)
        {
            var (infos, buffers) = Heads();
            var random = new Random(seed);
            foreach (var buffer in buffers)
            {
                for (int i = 0; i < buffer.Length; i++) { buffer[i] = (sbyte)random.Next(-128, 128); }
            }
            var expected = FloatReference(profile, infos, buffers, transform);
            var actual = profile.DecodeInt8(infos, buffers[0], buffers[1], buffers[2], transform);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void QuantizedZeroPointHasInclusiveQuarterScoreAndSignedArgmax()
    {
        var profile = Profile();
        var (infos, buffers) = Heads();
        int cell = 5, plane = 16;
        for (int k = 0; k < 6; k++) { buffers[0][k * plane + cell] = (sbyte)infos[0].ZeroPoint; }
        var result = Assert.Single(profile.DecodeInt8(infos, buffers[0], buffers[1], buffers[2], new(32, 32, 1, 1, 0, 0)));
        Assert.Equal(0.25f, result.Confidence);
        Assert.Equal(0, result.ClassId);
        Assert.Equal(new BoundingBox(11.5f, 11, 12.5f, 13), result.Box);
        Assert.Equal(FloatReference(profile, infos, buffers, new(32, 32, 1, 1, 0, 0)), new[] { result });
    }

    [Fact]
    public void SaturatedProbabilitiesDoNotCreateClassTies()
    {
        var profile = Profile();
        var (infos, buffers) = Heads();
        infos[0] = new(0, "head", [1, 24, 4, 4], 384, 0, 2, 2, 0, 8);
        int cell = 5, plane = 16;
        for (int k = 0; k < 4; k++) { buffers[0][k * plane + cell] = 0; }
        buffers[0][4 * plane + cell] = 127;
        buffers[0][5 * plane + cell] = 126;
        buffers[0][6 * plane + cell] = 127;
        var result = Assert.Single(profile.DecodeInt8(infos, buffers[0], buffers[1], buffers[2], new(32, 32, 1, 1, 0, 0)));
        Assert.Equal(1, result.ClassId);
        Assert.Equal(1, result.Confidence);
    }

    [Fact]
    public void SignedClassTiesAndAdjacentQuarterThresholdsAreDeterministic()
    {
        var (infos, buffers) = Heads();
        int cell = 5, plane = 16;
        for (int k = 0; k < 7; k++) { buffers[0][k * plane + cell] = (sbyte)infos[0].ZeroPoint; }
        var transform = new LetterboxTransform(32, 32, 1, 1, 0, 0);
        Assert.Equal(0, Assert.Single(Profile().DecodeInt8(infos, buffers[0], buffers[1], buffers[2], transform)).ClassId);
        Assert.Empty(Profile(MathF.BitIncrement(0.25f)).DecodeInt8(infos, buffers[0], buffers[1], buffers[2], transform));
        Assert.Single(Profile(MathF.BitDecrement(0.25f)).DecodeInt8(infos, buffers[0], buffers[1], buffers[2], transform));
    }

    [Fact]
    public async Task SharedLookupPublicationSupportsDifferentQuantizationConcurrently()
    {
        var profile = Profile();
        var first = Heads(); var second = Heads();
        for (int head = 0; head < 3; head++)
        {
            var info = second.Infos[head];
            second.Infos[head] = new(info.Index, info.Name, info.Dimensions, info.ElementCount,
                info.Layout, info.DataType, info.Quantization, info.ZeroPoint + 7, info.Scale * 1.25f);
        }
        var random = new Random(731);
        foreach (var buffer in first.Buffers.Concat(second.Buffers))
        {
            for (int i = 0; i < buffer.Length; i++) { buffer[i] = (sbyte)random.Next(-128, 128); }
        }
        var transform = new LetterboxTransform(32, 32, 1, 1, 0, 0);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++)
            {
                var set = ((i + worker) & 1) == 0 ? first : second;
                Assert.Equal(FloatReference(profile, set.Infos, set.Buffers, transform),
                    profile.DecodeInt8(set.Infos, set.Buffers[0], set.Buffers[1], set.Buffers[2], transform));
            }
        })));
    }

    [Fact]
    public void RawBuffersAndLookupQuantizationAreValidated()
    {
        var profile = Profile();
        var (infos, buffers) = Heads();
        var transform = new LetterboxTransform(32, 32, 1, 1, 0, 0);
        Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, new sbyte[1], buffers[1], buffers[2], transform));
        infos[0] = new(0, "head", [1, 24, 4, 4], 384, 0, 2, 2, 0, float.MaxValue);
        Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, buffers[0], buffers[1], buffers[2], transform));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RknnModel("unused", (NpuCoreMask)5));
    }
}
