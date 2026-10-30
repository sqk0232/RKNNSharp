using Xunit;

namespace RKNNSharp.Tests;

public sealed class YoloV5Tests
{
    private static List<TensorOutput> Heads(int classes = 80, int size = 640)
    {
        var result = new List<TensorOutput>();
        for (int head = 0; head < 3; head++)
        {
            uint grid = (uint)(size / (8 << head)), channels = (uint)(3 * (5 + classes));
            uint count = channels * grid * grid;
            var info = new TensorInfo((uint)head, $"head{head}", [1, channels, grid, grid], count, 0, 2, 2, 83 - head, 0.09f + head * 0.01f);
            var values = new float[count];
            Array.Fill(values, -1000);
            result.Add(new TensorOutput(info, values));
        }
        return result;
    }

    private static void Candidate(List<TensorOutput> heads, int head, int anchor, int x, int y, int classId, float objectLogit = 0, float classLogit = 0)
    {
        var copy = heads[head].Values.ToArray();
        int grid = (int)heads[head].Info.Dimensions[2], plane = grid * grid, cell = y * grid + x;
        int attributes = (int)heads[head].Info.Dimensions[1] / 3;
        int start = anchor * attributes * plane;
        for (int k = 0; k < 4; k++) { copy[start + k * plane + cell] = 0; }
        copy[start + 4 * plane + cell] = objectLogit;
        copy[start + (5 + classId) * plane + cell] = classLogit;
        heads[head] = heads[head] with { Values = copy };
    }

    [Fact]
    public void ZeroLogitCandidateDecodesCorrectAnchorChannelAndCoordinates()
    {
        var heads = Heads();
        Candidate(heads, 0, 0, 10, 20, 3);
        var results = YoloV5Profile.CreateOfficial640Example().Decode(heads, new(640, 640, 1, 1, 0, 0));
        var result = Assert.Single(results);
        Assert.Equal(3, result.ClassId);
        Assert.Equal(0.25f, result.Confidence);
        Assert.Equal(new BoundingBox(79, 157.5f, 89, 170.5f), result.Box);
        heads = Heads();
        Candidate(heads, 0, 1, 10, 20, 79);
        result = Assert.Single(YoloV5Profile.CreateOfficial640Example().Decode(heads, new(640, 640, 1, 1, 0, 0)));
        Assert.Equal(new BoundingBox(76, 149, 92, 179), result.Box);
        Assert.Equal(79, result.ClassId);
    }

    [Fact]
    public void ProductThresholdAndExtremeLogitsAreHandledOnce()
    {
        var heads = Heads();
        float logit04 = MathF.Log(0.4f / 0.6f);
        Candidate(heads, 0, 0, 10, 20, 0, logit04, logit04);
        Assert.Empty(YoloV5Profile.CreateOfficial640Example().Decode(heads, new(640, 640, 1, 1, 0, 0)));
        Assert.Equal(0, YoloV5Profile.Sigmoid(-1000));
        Assert.Equal(1, YoloV5Profile.Sigmoid(1000));
        Assert.Equal(0.5f, YoloV5Profile.Sigmoid(0));
        Candidate(heads, 2, 2, 10, 10, 4, 1000, 1000);
        var result = Assert.Single(YoloV5Profile.CreateOfficial640Example().Decode(heads, new(640, 640, 1, 1, 0, 0)));
        Assert.Equal(1, result.Confidence);
        Assert.Equal(4, result.ClassId);
    }

    [Fact]
    public void InvalidShapeTruncatedOrNonFiniteBuffersFailFast()
    {
        var profile = YoloV5Profile.CreateOfficial640Example();
        var transform = new LetterboxTransform(640, 640, 1, 1, 0, 0);
        var heads = Heads();
        heads[0] = heads[0] with { Values = new float[1] };
        Assert.Throws<ArgumentException>(() => profile.Decode(heads, transform));
        heads = Heads();
        var values = heads[0].Values.ToArray(); values[0] = float.NaN;
        heads[0] = heads[0] with { Values = values };
        Assert.Throws<ArgumentException>(() => profile.Decode(heads, transform));
        heads = Heads();
        heads[0] = heads[0] with { Info = new(0, "wrong", [1, 80, 80, 255], 1632000, 1, 2, 2, 83, 0.09f) };
        Assert.Throws<ArgumentException>(() => profile.Decode(heads, transform));
    }

    [Fact]
    public void EqualClassLogitsSelectLowestIdAndPaddingBoxesDisappear()
    {
        var profile = YoloV5Profile.CreateOfficial640Example();
        var heads = Heads();
        Candidate(heads, 0, 0, 10, 20, 3);
        var values = heads[0].Values.ToArray();
        values[(5 + 2) * 80 * 80 + 20 * 80 + 10] = 0;
        heads[0] = heads[0] with { Values = values };
        var result = Assert.Single(profile.Decode(heads, new(640, 640, 1, 1, 0, 0)));
        Assert.Equal(2, result.ClassId);
        Assert.Empty(profile.Decode(heads, new(1920, 1080, 1f / 3, 1f / 3, 0, 200)));
    }

    [Fact]
    public void InvalidQuantizationOrHeadOrderIsRejected()
    {
        var profile = YoloV5Profile.CreateOfficial640Example();
        var transform = new LetterboxTransform(640, 640, 1, 1, 0, 0);
        var heads = Heads();
        heads[0] = heads[0] with { Info = new(0, "invalid", [1, 255, 80, 80], 1632000, 0, 2, 2, 83, float.NaN) };
        Assert.Throws<ArgumentException>(() => profile.Decode(heads, transform));
        heads = Heads();
        (heads[0], heads[1]) = (heads[1], heads[0]);
        Assert.Throws<ArgumentException>(() => profile.Decode(heads, transform));
    }

    [Fact]
    public void InputQuantizationAndExplicitAnchorConfigurationAreValidated()
    {
        var profile = YoloV5Profile.CreateOfficial640Example();
        var outputs = Heads().Select(output => output.Info).ToArray();
        var input = new TensorInfo(0, "images", [1, 640, 640, 3], 1228800, 1, 2, 2, -128, 1f / 255);
        profile.ValidateModel([input], outputs);
        Assert.Throws<ArgumentException>(() => profile.ValidateModel([new(0, "wrong scale", [1, 640, 640, 3], 1228800, 1, 2, 2, -128, MathF.BitIncrement(1f / 255))], outputs));
        Assert.Throws<ArgumentException>(() => profile.ValidateModel([new(0, "wrong", [1, 3, 640, 640], 1228800, 0, 2, 2, -128, 1f / 255)], outputs));
        Assert.Throws<ArgumentException>(() => new YoloV5Profile(640, 640, 80, [[new(10, 13)]]));
        Assert.Throws<ArgumentOutOfRangeException>(() => YoloV5Profile.CreateOfficial640Example(float.NaN));
    }
}
