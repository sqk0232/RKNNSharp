namespace RKNNSharp;

// Validates the frozen native preprocessing policy, without narrowing public transform semantics.
internal static class NativeLetterboxGeometry
{
    internal static LetterboxTransform Create(DmaFrame source, TensorInfo input, in NativeRunInfo info)
    {
        var dimensions = input.Dimensions;
        if (input.Layout != 1 || dimensions.Count != 4 || dimensions[0] != 1 || dimensions[3] != 3 ||
            dimensions[1] is 0 or > 8192 || dimensions[2] is 0 or > 8192 ||
            source.Width is 0 or > 8192 || source.Height is 0 or > 8192)
        {
            throw new RknnException("Native letterbox requires static NHWC RGB model dimensions and valid source dimensions.");
        }
        int targetWidth = (int)dimensions[2], targetHeight = (int)dimensions[1];
        // C++ std::round is half away from zero. min2 is a native hardware policy, not a general transform rule.
        double ratio = Math.Min((double)targetWidth / source.Width, (double)targetHeight / source.Height);
        int expectedWidth = Math.Max(2, (int)Math.Round(source.Width * ratio, MidpointRounding.AwayFromZero));
        int expectedHeight = Math.Max(2, (int)Math.Round(source.Height * ratio, MidpointRounding.AwayFromZero));
        if (info.ResizeWidth < 2 || info.ResizeWidth > targetWidth || info.ResizeHeight < 2 || info.ResizeHeight > targetHeight ||
            info.ResizeWidth != expectedWidth || info.ResizeHeight != expectedHeight ||
            info.PadLeft != (targetWidth - info.ResizeWidth) / 2 || info.PadTop != (targetHeight - info.ResizeHeight) / 2 ||
            !float.IsFinite(info.ScaleX) || !float.IsFinite(info.ScaleY) || info.ScaleX <= 0 || info.ScaleY <= 0 ||
            info.ScaleX != (float)info.ResizeWidth / source.Width || info.ScaleY != (float)info.ResizeHeight / source.Height)
        {
            throw new RknnException("Native letterbox dimensions, centered padding or actual scales are inconsistent.");
        }
        // Return the native values unchanged; the two actual scales can differ after integer rounding.
        return new LetterboxTransform((int)source.Width, (int)source.Height, info.ScaleX, info.ScaleY, info.PadLeft, info.PadTop);
    }
}
