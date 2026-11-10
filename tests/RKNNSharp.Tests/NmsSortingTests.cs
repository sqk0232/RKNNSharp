using Xunit;

namespace RKNNSharp.Tests;

public sealed class NmsSortingTests
{
    [Theory]
    [InlineData(false, 0f, 1)]
    [InlineData(false, .45f, 17)]
    [InlineData(false, 1f, 300)]
    [InlineData(true, 0f, 1)]
    [InlineData(true, .45f, 17)]
    [InlineData(true, 1f, 300)]
    public void InPlaceSortMatchesStableReferenceWithManyTies(bool dfl, float threshold, int limit)
    {
        var random = new Random(416);
        Detection[] original = Enumerable.Range(0, 97).Select(index =>
        {
            int left = random.Next(0, 8), top = random.Next(0, 8);
            return new Detection(index % 3, random.Next(1, 5) * .2f, new(left, top, left + 5, top + 5));
        }).ToArray();
        var candidates = original.Select((value, index) => new DetectionCandidate(value, index)).ToList();
        var expected = new List<Detection>();
        foreach (Detection candidate in original.OrderByDescending(value => value.Confidence))
        {
            if (expected.Any(previous => previous.ClassId == candidate.ClassId &&
                Iou(previous.Box, candidate.Box, dfl) > threshold)) { continue; }
            expected.Add(candidate);
            if (expected.Count == limit) { break; }
        }

        IReadOnlyList<Detection> actual;
        if (dfl)
        {
            var config = new YoloDetectionConfig(32, 32, 3, YoloDetectionOutputFormat.RknnDfl,
                [new(0, 1, null, 8), new(2, 3, null, 16), new(4, 5, null, 32)],
                nmsThreshold: threshold, maxDetections: limit);
            actual = new YoloDetectionProfile(config).Suppress(candidates);
        }
        else { actual = YoloV5Profile.NonMaximumSuppression(candidates, threshold, limit); }

        Assert.Equal(expected, actual);
        Assert.Equal(original.Select((value, index) => new DetectionCandidate(value, index))
            .OrderByDescending(value => value.Value.Confidence), candidates);
        candidates.Clear();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void V5HelperStillFiltersInvalidCandidatesAndPreservesItsInput()
    {
        Detection[] input = [new(0, float.NaN, new(0, 0, 10, 10)),
            new(0, .9f, new(0, 0, 0, 10)), new(0, .5f, new(0, 0, 10, 10)),
            new(1, .5f, new(0, 0, 10, 10))];
        Detection[] snapshot = input.ToArray();
        Assert.Equal(input.Skip(2), YoloV5Profile.NonMaximumSuppression(input, .45f, 300));
        Assert.Equal(snapshot, input);
    }

    private static float Iou(BoundingBox a, BoundingBox b, bool dfl)
    {
        if (!dfl) { return YoloV5Profile.IntersectionOverUnion(a, b); }
        double width = Math.Max(0, Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left) + .00001);
        double height = Math.Max(0, Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top) + .00001);
        double intersection = width * height;
        double union = (double)a.Width * a.Height + (double)b.Width * b.Height - intersection;
        return union > 0 ? (float)(intersection / union) : 0;
    }
}
