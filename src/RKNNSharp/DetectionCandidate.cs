namespace RKNNSharp;

// The insertion ordinal makes List.Sort preserve the decoder's equal-score policy.
internal readonly record struct DetectionCandidate(Detection Value, int Ordinal) : IComparable<DetectionCandidate>
{
    public int CompareTo(DetectionCandidate other)
    {
        int confidence = other.Value.Confidence.CompareTo(Value.Confidence);
        return confidence != 0 ? confidence : Ordinal.CompareTo(other.Ordinal);
    }
}
