using Xunit;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RKNNSharp.Tests;

public sealed class YoloDetectionConfigTests
{
    private static YoloDetectionHead[] Heads() => [new(0, 1, 2, 8), new(3, 4, 5, 16), new(6, 7, 8, 32)];

    [Fact]
    public void CapturesExplicitMappingsAndLabelsWithoutMutableAliases()
    {
        var heads = Heads(); string[] labels = ["person", "vehicle"];
        var config = new YoloDetectionConfig(640, 640, 2, YoloDetectionOutputFormat.RknnDfl, heads,
            multiLabel: true, classNames: labels);
        heads[0] = new(90, 91, 92, 8); labels[0] = "changed";
        Assert.Equal(0, config.Heads[0].BoxOutputIndex); Assert.Equal("person", config.ClassNames[0]);
        Assert.Equal(16, config.RegMax); Assert.True(config.MultiLabel); Assert.Equal(300, config.MaxDetections);
    }

    [Fact]
    public void AllowsNonSequentialMappingsAndOptionalScoreSum()
    {
        YoloDetectionHead[] heads = [new(4, 5, null, 8), new(0, 1, null, 16), new(2, 3, null, 32)];
        var config = new YoloDetectionConfig(640, 384, 80, YoloDetectionOutputFormat.RknnDfl, heads);
        Assert.Equal(4, config.Heads[0].BoxOutputIndex); Assert.Null(config.Heads[0].ScoreSumOutputIndex);
        Assert.Empty(config.ClassNames);
    }

    [Fact]
    public void JsonConfigRoundTripsExplicitContractAndEmptyOptionalLabels()
    {
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        options.Converters.Add(new JsonStringEnumConverter());
        var original = new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, Heads(), multiLabel: true);
        var decoded = JsonSerializer.Deserialize<YoloDetectionConfig>(JsonSerializer.Serialize(original, options), options)!;
        Assert.Equal(original.InputWidth, decoded.InputWidth); Assert.Equal(original.OutputFormat, decoded.OutputFormat);
        Assert.Equal(original.Heads, decoded.Heads); Assert.True(decoded.MultiLabel); Assert.Empty(decoded.ClassNames);
    }

    [Fact]
    public void RejectsMissingDuplicateAndInvalidBranchMappings()
    {
        Assert.Throws<ArgumentNullException>(() => new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, null!));
        var invalid = new[] {
            Heads()[..2],
            new YoloDetectionHead[] {new(0,1,2,8),new(3,4,5,8),new(6,7,8,32)},
            new YoloDetectionHead[] {new(-1,1,2,8),new(3,4,5,16),new(6,7,8,32)},
            new YoloDetectionHead[] {new(0,1,2,8),new(2,4,5,16),new(6,7,8,32)},
            new YoloDetectionHead[] {new(0,0,2,8),new(3,4,5,16),new(6,7,8,32)},
            new YoloDetectionHead[] {null!,new(3,4,5,16),new(6,7,8,32)} };
        foreach (var heads in invalid)
        { Assert.Throws<ArgumentException>(() => new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, heads)); }
    }

    [Fact]
    public void RejectsInvalidDimensionsNumericPolicyAndLabels()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(641, 640, 80, YoloDetectionOutputFormat.RknnDfl, Heads()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(640, 640, int.MaxValue, YoloDetectionOutputFormat.RknnDfl, Heads()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(640, 640, 80, (YoloDetectionOutputFormat)99, Heads()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, Heads(), regMax: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, Heads(), confidenceThreshold: float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, Heads(), nmsThreshold: float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => new YoloDetectionConfig(640, 640, 80, YoloDetectionOutputFormat.RknnDfl, Heads(), maxDetections: 0));
        Assert.Throws<ArgumentException>(() => new YoloDetectionConfig(640, 640, 2, YoloDetectionOutputFormat.RknnDfl, Heads(), classNames: ["one"]));
        Assert.Throws<ArgumentException>(() => new YoloDetectionConfig(640, 640, 2, YoloDetectionOutputFormat.RknnDfl, Heads(), classNames: ["one", " "]));
    }
}
