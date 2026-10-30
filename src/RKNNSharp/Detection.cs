namespace RKNNSharp;

public readonly record struct BoundingBox(float Left, float Top, float Right, float Bottom)
{
    public float Width => Right - Left;
    public float Height => Bottom - Top;
    public bool IsValid => float.IsFinite(Left) && float.IsFinite(Top) && float.IsFinite(Right) &&
        float.IsFinite(Bottom) && Width > 0 && Height > 0 && float.IsFinite(Width * Height);
}

public readonly record struct Detection(int ClassId, float Confidence, BoundingBox Box);
public sealed record DetectionResult(IReadOnlyList<Detection> Detections, LetterboxTransform Transform, RunTimings Timings, double PostprocessMs);
