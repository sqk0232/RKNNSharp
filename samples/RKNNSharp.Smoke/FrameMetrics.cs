namespace RKNNSharp.Smoke;

internal readonly record struct FrameMetrics(
    double PreprocessMs, double InferenceMs, double RuntimeOutputsMs, double ManagedOutputsMs,
    double DecodeMs, double CopyMs, double DrawMs, double TotalMs, long AllocatedBytes, int Gen0, int Gen1, int Gen2);
