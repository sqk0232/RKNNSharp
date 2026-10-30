using Xunit;

namespace RKNNSharp.Tests;

public sealed class YoloDetectionProfileTests
{
    private static YoloDetectionConfig Config(bool sums = true, bool multiLabel = false, int limit = 300,
        float confidence = .25f, float nms = .45f) => new(32, 32, 2, YoloDetectionOutputFormat.RknnDfl,
        Enumerable.Range(0, 3).Select(head => new YoloDetectionHead(head * (sums ? 3 : 2),
            head * (sums ? 3 : 2) + 1, sums ? head * 3 + 2 : null, 8 << head)).ToArray(),
        confidenceThreshold: confidence, nmsThreshold: nms, multiLabel: multiLabel, maxDetections: limit);

    private static List<TensorOutput> Outputs(YoloDetectionConfig config)
    {
        var result = new List<TensorOutput>();
        foreach (var head in config.Heads)
        {
            Add(head.BoxOutputIndex, 64, head.Stride);
            Add(head.ClassOutputIndex, 2, head.Stride);
            if (head.ScoreSumOutputIndex is int sum) { Add(sum, 1, head.Stride); }
        }
        return result.OrderBy(output => output.Info.Index).ToList();

        void Add(int index, int channels, int stride)
        {
            uint grid = (uint)(32 / stride), count = (uint)channels * grid * grid;
            result.Add(new(new((uint)index, $"head{index}", [1, (uint)channels, grid, grid], count, 0, 2, 2, -128, .01f), new float[count]));
        }
    }

    private static void Set(List<TensorOutput> outputs, int output, int channel, int cell, float value)
    {
        float[] values = outputs[output].Values.ToArray();
        int plane = (int)(outputs[output].Info.Dimensions[2] * outputs[output].Info.Dimensions[3]);
        values[channel * plane + cell] = value;
        outputs[output] = outputs[output] with { Values = values };
    }

    private static void Concentrate(List<TensorOutput> outputs, int output, int cell, params int[] bins)
    {
        for (int side = 0; side < 4; side++)
        {
            for (int bin = 0; bin < 16; bin++) { Set(outputs, output, side * 16 + bin, cell, bin == bins[side] ? 1000 : -1000); }
        }
    }

    private static readonly LetterboxTransform Identity = new(32, 32, 1, 1, 0, 0);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UniformDflAndProbabilityThresholdUseExpectedContract(bool sums)
    {
        var config = Config(sums);
        var outputs = Outputs(config);
        Set(outputs, 1, 1, 5, .25f);
        var detection = Assert.Single(new YoloDetectionProfile(config).Decode(outputs, Identity));
        Assert.Equal(1, detection.ClassId);
        Assert.Equal(.25f, detection.Confidence);
        // Uniform 16-bin DFL expectation=7.5 at center (12,12), then source clipping.
        Assert.Equal(new BoundingBox(0, 0, 32, 32), detection.Box);
    }

    [Fact]
    public void DflSideOrderAndStableExtremeSoftmaxRestoreLetterbox()
    {
        var config = Config();
        var outputs = Outputs(config);
        // Cell (2,2) at stride8: center=(20,20), ltrb=(1,2,0,1).
        Concentrate(outputs, 0, 10, 1, 2, 0, 1);
        Set(outputs, 1, 0, 10, .9f);
        var detection = Assert.Single(new YoloDetectionProfile(config).Decode(outputs, new(64, 48, .5f, .5f, 0, 4)));
        Assert.Equal(new BoundingBox(24, 0, 40, 48), detection.Box);
    }

    [Fact]
    public void NoObjectnessNoSecondSigmoidAndScoreSumDoesNotPrefilter()
    {
        var config = Config();
        var outputs = Outputs(config);
        Set(outputs, 1, 0, 5, .249f);
        Assert.Empty(new YoloDetectionProfile(config).Decode(outputs, Identity));
        Set(outputs, 1, 0, 5, .8f);
        Set(outputs, 2, 0, 5, 0); // Quantized sum may disagree near boundaries; official Python ignores it.
        Assert.Equal(.8f, Assert.Single(new YoloDetectionProfile(config).Decode(outputs, Identity)).Confidence);
    }

    [Fact]
    public void DequantizedClassScoresAreNotClipped()
    {
        var config = Config();
        var outputs = Outputs(config);
        Set(outputs, 1, 0, 5, 1.0001f);
        Assert.Equal(1.0001f, Assert.Single(new YoloDetectionProfile(config).Decode(outputs, Identity)).Confidence);
    }

    [Fact]
    public void LowestClassWinsTieAndMultiLabelIsExplicit()
    {
        var config = Config();
        var outputs = Outputs(config);
        Set(outputs, 1, 0, 5, .8f);
        Set(outputs, 1, 1, 5, .8f);
        Assert.Equal(0, Assert.Single(new YoloDetectionProfile(config).Decode(outputs, Identity)).ClassId);
        Assert.Equal(new[] { 0, 1 }, new YoloDetectionProfile(Config(multiLabel: true)).Decode(outputs, Identity).Select(detection => detection.ClassId));
    }

    [Fact]
    public void NmsIsClassAwareAndRunsBeforeRestoreClipping()
    {
        var config = Config();
        var outputs = Outputs(config);
        Set(outputs, 1, 0, 0, .9f);
        Set(outputs, 1, 0, 1, .8f);
        Set(outputs, 1, 1, 2, .7f);
        var detections = new YoloDetectionProfile(config).Decode(outputs, Identity);
        Assert.Equal(new[] { 0, 1 }, detections.Select(detection => detection.ClassId));
        Assert.Equal(new[] { .9f, .7f }, detections.Select(detection => detection.Confidence));
    }

    [Fact]
    public void EqualScoreNmsKeepsCanonicalEarlierCellRegardlessOfHeadMappingOrder()
    {
        var config = Config();
        var outputs = Outputs(config);
        Concentrate(outputs, 0, 5, 1, 1, 1, 1); // [4,4,20,20], earlier row-major cell.
        Concentrate(outputs, 0, 6, 2, 1, 1, 1); // [4,4,28,20], IoU=2/3.
        Set(outputs, 1, 0, 5, .8f);
        Set(outputs, 1, 0, 6, .8f);
        Assert.Equal(new BoundingBox(4, 4, 20, 20), Assert.Single(new YoloDetectionProfile(config).Decode(outputs, Identity)).Box);
        var reversed = new YoloDetectionConfig(32, 32, 2, YoloDetectionOutputFormat.RknnDfl, config.Heads.Reverse().ToArray());
        Assert.Equal(new BoundingBox(4, 4, 20, 20), Assert.Single(new YoloDetectionProfile(reversed).Decode(outputs, Identity)).Box);
    }

    [Fact]
    public void NmsThresholdEqualityKeepsNonOverlappingBoxesAndLimitIsGlobal()
    {
        var config = Config(nms: 0, limit: 1);
        var outputs = Outputs(config);
        Concentrate(outputs, 0, 0, 0, 0, 1, 1);
        Concentrate(outputs, 0, 15, 1, 1, 0, 0);
        Set(outputs, 1, 0, 0, .7f);
        Set(outputs, 1, 0, 15, .9f);
        Assert.Equal(.9f, Assert.Single(new YoloDetectionProfile(config).Decode(outputs, Identity)).Confidence);
        Assert.Equal(2, new YoloDetectionProfile(Config(nms: 0)).Decode(outputs, Identity).Count);
    }

    [Fact]
    public void PaddingOnlyAndDegenerateBoxesDisappear()
    {
        var config = Config();
        var outputs = Outputs(config);
        Concentrate(outputs, 0, 0, 0, 0, 1, 1);
        Set(outputs, 1, 0, 0, .8f);
        Assert.Empty(new YoloDetectionProfile(config).Decode(outputs, new(32, 16, 1, 1, 0, 16)));
        Concentrate(outputs, 0, 0, 0, 0, 0, 0);
        Assert.Empty(new YoloDetectionProfile(config).Decode(outputs, Identity));
    }

    [Fact]
    public void ExplicitOutputPermutationIsHonored()
    {
        var config = Config();
        var outputs = Outputs(config);
        Set(outputs, 1, 1, 5, .8f);
        // Swap equal-layout box/class tensors through explicit mappings, preserving SDK positional indices.
        (outputs[0], outputs[1]) = (outputs[1] with { Info = Reindex(outputs[1].Info, 0) }, outputs[0] with { Info = Reindex(outputs[0].Info, 1) });
        var mapped = new YoloDetectionConfig(32, 32, 2, YoloDetectionOutputFormat.RknnDfl,
            [new(1, 0, 2, 8), new(3, 4, 5, 16), new(6, 7, 8, 32)]);
        Assert.Equal(1, Assert.Single(new YoloDetectionProfile(mapped).Decode(outputs, Identity)).ClassId);
        Assert.Throws<ArgumentException>(() => new YoloDetectionProfile(config).Decode(outputs, Identity));
    }

    [Fact]
    public void WrongCountShapeLayoutIndexQuantizationAndRegMaxFail()
    {
        var config = Config();
        var profile = new YoloDetectionProfile(config);
        var outputs = Outputs(config);
        Assert.Throws<ArgumentException>(() => profile.Decode(outputs.Take(8).ToArray(), Identity));
        var original = outputs[0];
        foreach (var info in new[] {
            new TensorInfo(0, "bad", [1, 16, 4, 4], 256, 0, 2, 2, 0, .1f),
            new TensorInfo(0, "bad", [1, 64, 4, 4], 1024, 1, 2, 2, 0, .1f),
            new TensorInfo(1, "bad", [1, 64, 4, 4], 1024, 0, 2, 2, 0, .1f),
            new TensorInfo(0, "bad", [1, 64, 4, 4], 1024, 0, 2, 2, 0, float.NaN),
            new TensorInfo(0, "bad", [1, 64, 4, 4], 1024, 0, 1, 0, 0, .1f) })
        {
            outputs[0] = original with { Info = info };
            Assert.Throws<ArgumentException>(() => profile.Decode(outputs, Identity));
        }
    }

    [Theory]
    [InlineData(0, float.NaN)]
    [InlineData(1, float.PositiveInfinity)]
    [InlineData(2, float.NegativeInfinity)]
    public void EveryMappedTensorMustBeFinite(int index, float value)
    {
        var config = Config();
        var outputs = Outputs(config);
        Set(outputs, index, 0, 0, value);
        Assert.Throws<ArgumentException>(() => new YoloDetectionProfile(config).Decode(outputs, Identity));
    }

    [Fact]
    public void TruncatedBuffersAndInvalidTransformsFail()
    {
        var config = Config();
        var outputs = Outputs(config);
        outputs[0] = outputs[0] with { Values = new float[1] };
        Assert.Throws<ArgumentException>(() => new YoloDetectionProfile(config).Decode(outputs, Identity));
        Assert.Throws<ArgumentException>(() => new YoloDetectionProfile(config).Decode(Outputs(config), new(32, 32, 0, 1, 0, 0)));
    }

    [Fact]
    public void ModelInputContractRemainsStrict()
    {
        var config = Config();
        var profile = new YoloDetectionProfile(config);
        var infos = Outputs(config).Select(output => output.Info).ToArray();
        profile.ValidateModel([new(0, "input", [1, 32, 32, 3], 3072, 1, 2, 2, -128, 1f / 255)], infos);
        Assert.Throws<ArgumentException>(() => profile.ValidateModel([new(0, "input", [1, 3, 32, 32], 3072, 0, 2, 2, -128, 1f / 255)], infos));
        Assert.Throws<ArgumentException>(() => profile.ValidateModel([new(0, "input", [1, 32, 32, 3], 3072, 1, 2, 2, -127, 1f / 255)], infos));
    }

    private static TensorInfo Reindex(TensorInfo value, uint index) => new(index, value.Name, value.Dimensions,
        value.ElementCount, value.Layout, value.DataType, value.Quantization, value.ZeroPoint, value.Scale);
}
