using System.Runtime.InteropServices;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class DflInt8Tests
{
    private static YoloDetectionConfig Config(bool sums = true, bool multiLabel = false, int bins = 16,
        float confidence = .25f, float nms = .45f, int limit = 300) => new(64, 32, 3,
        YoloDetectionOutputFormat.RknnDfl,
        Enumerable.Range(0, 3).Select(slot => new YoloDetectionHead(slot * (sums ? 3 : 2),
            slot * (sums ? 3 : 2) + 1, sums ? slot * 3 + 2 : null, 8 << slot)).Reverse().ToArray(),
        regMax: bins, confidenceThreshold: confidence, nmsThreshold: nms, multiLabel: multiLabel, maxDetections: limit);

    private static readonly LetterboxTransform Transform = new(101, 57, .5f, .4f, 3, 7);

    private static (TensorInfo[] Infos, sbyte[][] Values) Outputs(YoloDetectionConfig config, int variant = 0)
    {
        int count = config.Heads.Sum(head => head.ScoreSumOutputIndex.HasValue ? 3 : 2);
        var infos = new TensorInfo[count];
        var values = new sbyte[count][];
        foreach (YoloDetectionHead head in config.Heads)
        {
            Add(head.BoxOutputIndex, 4 * config.RegMax, head.Stride);
            Add(head.ClassOutputIndex, config.ClassCount, head.Stride);
            if (head.ScoreSumOutputIndex is int sum) { Add(sum, 1, head.Stride); }
        }
        return (infos, values);

        void Add(int index, int channels, int stride)
        {
            uint height = (uint)(config.InputHeight / stride), width = (uint)(config.InputWidth / stride);
            int zeroPoint = -80 + index * 13 + variant;
            float scale = (index % 3 + 1) * .01234567f * (variant + 1);
            uint elements = (uint)channels * height * width;
            infos[index] = new((uint)index, $"output{index}", [1, (uint)channels, height, width], elements,
                0, 2, 2, zeroPoint, scale);
            values[index] = Enumerable.Repeat((sbyte)zeroPoint, (int)elements).ToArray();
        }
    }

    private static IReadOnlyList<Detection> Reference(YoloDetectionProfile profile, TensorInfo[] infos,
        sbyte[][] values, LetterboxTransform transform) => profile.Decode(infos.Select((info, index) =>
        new TensorOutput(info, values[index].Select(value => (value - info.ZeroPoint) * info.Scale).ToArray())).ToArray(), transform);

    private static IReadOnlyList<Detection> Decode(YoloDetectionProfile profile, TensorInfo[] infos,
        sbyte[][] values, LetterboxTransform transform)
    {
        using var pinned = new PinnedOutputs(values);
        var actual = profile.DecodeInt8(infos, pinned.Addresses, pinned.Counts, transform);
        YoloDetectionConfig config = profile.Config;
        profile.ValidateModel([new(0, "input", [1, (uint)config.InputHeight, (uint)config.InputWidth, 3],
            (uint)(config.InputWidth * config.InputHeight * 3), 1, 2, 2, -128, 1f / 255)], infos);
        Assert.Equal(actual, profile.DecodeInt8(infos, pinned.Addresses, pinned.Counts, transform, modelValidated: true));
        return actual;
    }

    [Theory]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 16)]
    [InlineData(true, true, 256)]
    [InlineData(false, false, 16)]
    public void RawMatchesAffineFloatAcrossHeadsBinsMultiLabelAndNonSquareGrids(bool sums, bool multiLabel, int bins)
    {
        var config = Config(sums, multiLabel, bins);
        var profile = new YoloDetectionProfile(config);
        var (infos, values) = Outputs(config);
        var random = new Random(735);
        for (int iteration = 0; iteration < 12; iteration++)
        {
            foreach (sbyte[] buffer in values)
            {
                for (int i = 0; i < buffer.Length; i++) { buffer[i] = (sbyte)random.Next(-128, 128); }
            }
            Assert.Equal(Reference(profile, infos, values, Transform), Decode(profile, infos, values, Transform));
        }
    }

    [Fact]
    public void EachHeadCellCandidateMatchesReferenceWithoutSuppression()
    {
        var config = Config();
        var profile = new YoloDetectionProfile(config);
        var (infos, values) = Outputs(config);
        var random = new Random(913);
        var identity = new LetterboxTransform(64, 32, 1, 1, 0, 0);
        foreach (YoloDetectionHead head in config.Heads)
        {
            sbyte[] boxes = values[head.BoxOutputIndex];
            for (int i = 0; i < boxes.Length; i++) { boxes[i] = (sbyte)random.Next(-128, 128); }
            int plane = config.InputWidth / head.Stride * (config.InputHeight / head.Stride);
            for (int cell = 0; cell < plane; cell++)
            {
                int classId = cell % config.ClassCount;
                int index = classId * plane + cell;
                values[head.ClassOutputIndex][index] = (sbyte)(infos[head.ClassOutputIndex].ZeroPoint + 50);
                var actual = Assert.Single(Decode(profile, infos, values, identity));
                Assert.Equal(classId, actual.ClassId);
                Assert.Equal(Assert.Single(Reference(profile, infos, values, identity)), actual);
                values[head.ClassOutputIndex][index] = (sbyte)infos[head.ClassOutputIndex].ZeroPoint;
            }
        }
    }

    [Fact]
    public void SignedEndpointsAndStableExtremeSoftmaxPreserveSideOrder()
    {
        var config = Config();
        var profile = new YoloDetectionProfile(config);
        var (infos, values) = Outputs(config);
        var head = config.Heads.Single(head => head.Stride == 8);
        infos[head.BoxOutputIndex] = Requantize(infos[head.BoxOutputIndex], 127, 1000);
        Array.Fill(values[head.BoxOutputIndex], (sbyte)-128);
        int plane = 32, cell = 19;
        int[] bins = [1, 2, 0, 1];
        for (int side = 0; side < 4; side++)
        { values[head.BoxOutputIndex][(side * config.RegMax + bins[side]) * plane + cell] = 127; }
        infos[head.ClassOutputIndex] = Requantize(infos[head.ClassOutputIndex], 0, .01f);
        Array.Fill(values[head.ClassOutputIndex], (sbyte)-128);
        values[head.ClassOutputIndex][plane + cell] = 127;
        var identity = new LetterboxTransform(64, 32, 1, 1, 0, 0);
        var actual = Assert.Single(Decode(profile, infos, values, identity));
        Assert.Equal(1, actual.ClassId);
        Assert.Equal(new BoundingBox(20, 4, 28, 28), actual.Box);
        Assert.Equal(Reference(profile, infos, values, identity), Decode(profile, infos, values, identity));
    }

    [Fact]
    public void ConfidenceEqualityLowestClassTieAndIgnoredScoreSumMatchReference()
    {
        var config = Config();
        var (infos, values) = Outputs(config);
        infos[1] = Requantize(infos[1], 0, .25f);
        Array.Clear(values[1]);
        values[1][5] = 1;
        values[1][32 + 5] = 1;
        Array.Fill(values[2], (sbyte)-128);
        foreach (float threshold in new[] { .25f, MathF.BitIncrement(.25f), MathF.BitDecrement(.25f) })
        {
            var profile = new YoloDetectionProfile(Config(confidence: threshold));
            var actual = Decode(profile, infos, values, Transform);
            Assert.Equal(Reference(profile, infos, values, Transform), actual);
            if (threshold > .25f) { Assert.Empty(actual); }
            else { Assert.Equal(0, Assert.Single(actual).ClassId); }
        }
    }

    [Theory]
    [InlineData(float.Epsilon)]
    [InlineData(1.17549435E-38f)]
    public void TinyPositiveScalesPreserveClassTiesAndInclusiveThresholds(float scale)
    {
        var config = Config(confidence: scale);
        var (infos, values) = Outputs(config);
        infos[1] = Requantize(infos[1], -128, scale);
        Array.Fill(values[1], (sbyte)-128);
        values[1][5] = -127;
        values[1][32 + 5] = -127;
        var profile = new YoloDetectionProfile(config);
        Assert.Equal(0, Assert.Single(Decode(profile, infos, values, Transform)).ClassId);
        Assert.Equal(Reference(profile, infos, values, Transform), Decode(profile, infos, values, Transform));
        values[1][32 + 5] = -126;
        Assert.Equal(1, Assert.Single(Decode(profile, infos, values, Transform)).ClassId);
        foreach (float threshold in new[] { scale * 2, MathF.BitIncrement(scale * 2) })
        {
            profile = new YoloDetectionProfile(Config(confidence: threshold));
            Assert.Equal(Reference(profile, infos, values, Transform), Decode(profile, infos, values, Transform));
        }
    }

    [Theory]
    [InlineData(-128)]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(126)]
    [InlineData(127)]
    public void FiniteAffineValuesRemainStrictlyOrderedAtZeroPointAndScaleExtremes(int zeroPoint)
    {
        foreach (float scale in new[] { float.Epsilon, float.Epsilon * 1024, 1.17549435E-38f,
            .0039215689f, .1234567f, float.MaxValue / 255 })
        {
            float previous = (-128 - zeroPoint) * scale;
            for (int raw = -127; raw <= 127; raw++)
            {
                float value = (raw - zeroPoint) * scale;
                if (float.IsFinite(previous) && float.IsFinite(value)) { Assert.True(value > previous); }
                previous = value;
            }
        }
    }

    [Theory]
    [InlineData(0f, 1)]
    [InlineData(.45f, 300)]
    [InlineData(1f, 300)]
    public void CanonicalEqualScoreNmsAndGlobalLimitMatchReference(float nms, int limit)
    {
        var config = Config(nms: nms, limit: limit);
        var profile = new YoloDetectionProfile(config);
        var (infos, values) = Outputs(config);
        foreach (YoloDetectionHead head in config.Heads)
        {
            infos[head.ClassOutputIndex] = Requantize(infos[head.ClassOutputIndex], 0, .25f);
            Array.Fill(values[head.ClassOutputIndex], (sbyte)2);
        }
        Assert.Equal(Reference(profile, infos, values, Transform), Decode(profile, infos, values, Transform));
    }

    [Fact]
    public void ExplicitTensorPermutationIsHonored()
    {
        var config = Config();
        var (infos, values) = Outputs(config);
        values[1][32 + 5] = 0;
        (infos[0], infos[1]) = (Reindex(infos[1], 0), Reindex(infos[0], 1));
        (values[0], values[1]) = (values[1], values[0]);
        var mapped = new YoloDetectionConfig(64, 32, 3, YoloDetectionOutputFormat.RknnDfl,
            [new(1, 0, 2, 8), new(3, 4, 5, 16), new(6, 7, 8, 32)]);
        var profile = new YoloDetectionProfile(mapped);
        Assert.Single(Decode(profile, infos, values, Transform));
        Assert.Equal(Reference(profile, infos, values, Transform), Decode(profile, infos, values, Transform));
    }

    [Fact]
    public async Task SharedProfileKeepsPerCallQuantizationAndBuffersIsolated()
    {
        var config = Config(multiLabel: true);
        var profile = new YoloDetectionProfile(config);
        var first = Outputs(config);
        var second = Outputs(config, 1);
        var random = new Random(832);
        foreach (sbyte[] buffer in first.Values.Concat(second.Values))
        {
            for (int i = 0; i < buffer.Length; i++) { buffer[i] = (sbyte)random.Next(-128, 128); }
        }
        var expectedFirst = Reference(profile, first.Infos, first.Values, Transform);
        var expectedSecond = Reference(profile, second.Infos, second.Values, Transform);
        await Task.WhenAll(Enumerable.Range(0, 6).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 30; i++)
            {
                var set = (i + worker) % 2 == 0 ? first : second;
                var expected = (i + worker) % 2 == 0 ? expectedFirst : expectedSecond;
                Assert.Equal(expected, Decode(profile, set.Infos, set.Values, Transform));
            }
        })));
        var snapshot = Decode(profile, first.Infos, first.Values, Transform);
        foreach (sbyte[] buffer in first.Values) { Array.Clear(buffer); }
        Assert.Equal(expectedFirst, snapshot);
    }

    [Fact]
    public void CountPointerLengthLayoutTypeAndQuantizationErrorsFailBeforeReading()
    {
        var config = Config();
        var profile = new YoloDetectionProfile(config);
        var (infos, values) = Outputs(config);
        using var pinned = new PinnedOutputs(values);
        Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, [], [], Transform));
        nint originalAddress = pinned.Addresses[0];
        pinned.Addresses[0] = 0;
        Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, pinned.Addresses, pinned.Counts, Transform));
        pinned.Addresses[0] = originalAddress;
        pinned.Counts[0]--;
        Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, pinned.Addresses, pinned.Counts, Transform));
        pinned.Counts[0]++;
        Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, pinned.Addresses, pinned.Counts, Transform with { ScaleX = 0 }));
        TensorInfo valid = infos[0];
        foreach (TensorInfo invalid in new[]
        {
            new TensorInfo(0, "NHWC", [1, 64, 4, 8], valid.ElementCount, 1, 2, 2, 0, .1f),
            new TensorInfo(0, "UINT8", valid.Dimensions, valid.ElementCount, 0, 3, 2, 0, .1f),
            new TensorInfo(0, "not-affine", valid.Dimensions, valid.ElementCount, 0, 2, 0, 0, .1f),
            Requantize(valid, -129, .1f), Requantize(valid, 128, .1f),
            Requantize(valid, 0, 0), Requantize(valid, 0, float.NaN)
        })
        {
            infos[0] = invalid;
            Assert.Throws<ArgumentException>(() => profile.DecodeInt8(infos, pinned.Addresses, pinned.Counts, Transform));
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void ExtremeScaleChecksActualUnusedTailAndAllowsFiniteValues(int output)
    {
        var config = Config();
        var profile = new YoloDetectionProfile(config);
        var (infos, values) = Outputs(config);
        infos[output] = Requantize(infos[output], 0, float.MaxValue);
        Array.Clear(values[output]);
        Assert.Equal(Reference(profile, infos, values, Transform), Decode(profile, infos, values, Transform));
        values[output][^1] = -128;
        Assert.Throws<ArgumentException>(() => Reference(profile, infos, values, Transform));
        Assert.Throws<ArgumentException>(() => Decode(profile, infos, values, Transform));
    }

    [Fact]
    public void UnifiedPredictOverloadsReturnDetectionResults()
    {
        foreach (Type input in new[] { typeof(DmaFrame), typeof(DmaImage) })
        {
            var method = typeof(RknnPipeline).GetMethod("Predict", [input]);
            Assert.NotNull(method);
            Assert.Equal(typeof(DetectionResult), method.ReturnType);
        }
    }

    private static TensorInfo Requantize(TensorInfo info, int zeroPoint, float scale) => new(info.Index,
        info.Name, info.Dimensions, info.ElementCount, info.Layout, info.DataType, info.Quantization, zeroPoint, scale);

    private static TensorInfo Reindex(TensorInfo info, uint index) => new(index,
        info.Name, info.Dimensions, info.ElementCount, info.Layout, info.DataType, info.Quantization, info.ZeroPoint, info.Scale);

    private sealed class PinnedOutputs : IDisposable
    {
        private readonly GCHandle[] _pins;
        internal nint[] Addresses { get; }
        internal uint[] Counts { get; }

        internal PinnedOutputs(sbyte[][] values)
        {
            _pins = new GCHandle[values.Length];
            Addresses = new nint[values.Length];
            Counts = new uint[values.Length];
            try
            {
                for (int i = 0; i < values.Length; i++)
                {
                    _pins[i] = GCHandle.Alloc(values[i], GCHandleType.Pinned);
                    Addresses[i] = _pins[i].AddrOfPinnedObject();
                    Counts[i] = (uint)values[i].Length;
                }
            }
            catch { Dispose(); throw; }
        }

        public void Dispose()
        {
            for (int i = 0; i < _pins.Length; i++) { if (_pins[i].IsAllocated) { _pins[i].Free(); } }
        }
    }
}
