namespace RKNNSharp;

public readonly record struct LetterboxTransform(
    int SourceWidth, int SourceHeight, float ScaleX, float ScaleY, int PadLeft, int PadTop)
{
    public void Validate()
    {
        if (SourceWidth <= 0 || SourceHeight <= 0 || !float.IsFinite(ScaleX) || !float.IsFinite(ScaleY) ||
            ScaleX <= 0 || ScaleY <= 0 || PadLeft < 0 || PadTop < 0)
        {
            throw new ArgumentException("Invalid letterbox transform.");
        }
    }

    public BoundingBox Restore(BoundingBox box)
    {
        Validate();
        return new BoundingBox(
            Math.Clamp((box.Left - PadLeft) / ScaleX, 0, SourceWidth),
            Math.Clamp((box.Top - PadTop) / ScaleY, 0, SourceHeight),
            Math.Clamp((box.Right - PadLeft) / ScaleX, 0, SourceWidth),
            Math.Clamp((box.Bottom - PadTop) / ScaleY, 0, SourceHeight));
    }
}
