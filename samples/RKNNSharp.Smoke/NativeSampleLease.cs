using Microsoft.Win32.SafeHandles;
using RKNNSharp;

namespace RKNNSharp.Smoke;

/// <summary>A single reference owner, never a copied disposable GstFrame struct.</summary>
internal sealed class NativeSampleLease : IDisposable
{
    private readonly SampleHandle _handle;
    private readonly DmaFrame _descriptor;
    internal long FrameId { get; }
    internal ulong PtsNs { get; }
    internal long PulledAt { get; }

    internal NativeSampleLease(ref GstFrame frame, long frameId, ColorSpace? colorSpace)
    {
        _descriptor = GStreamerCapture.Borrow(in frame, colorSpace);
        FrameId = frameId; PtsNs = frame.PtsNs; PulledAt = System.Diagnostics.Stopwatch.GetTimestamp();
        _handle = new SampleHandle(frame);
        frame = default; // Ownership transfer: the caller can no longer release the same token.
    }

    internal T WithFrame<T>(Func<DmaFrame, T> action)
    {
        bool added = false;
        try { _handle.DangerousAddRef(ref added); return action(_descriptor); }
        finally { if (added) { _handle.DangerousRelease(); } }
    }

    public void Dispose() => _handle.Dispose();

    private sealed class SampleHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        private GstFrame _frame;
        internal SampleHandle(GstFrame frame) : base(true) { _frame = frame; SetHandle(frame.Token); }
        protected override bool ReleaseHandle() { GStreamerCapture.Release(ref _frame); return true; }
    }
}
