using Microsoft.Extensions.Logging;

namespace RKNNSharp;

// One state belongs to one model. Only warning/disable flags are shared by its calls;
// the original thread scheduling values always live in PlacementScope on the caller's stack.
internal sealed unsafe class PlacementState(
    CallerThreadPlacement? placement,
    ILogger logger,
    LibcSched.AffinityCall? getAffinity = null,
    LibcSched.AffinityCall? setAffinity = null,
    LibcSched.AttributeCall? getAttr = null,
    LibcSched.AttributeCall? setAttr = null)
{
    private int _placementDisabled;
    private int _restoreWarning;

    internal CallerThreadPlacement? Placement { get; } = placement;
    internal bool ShouldEnter => Placement is not null && Volatile.Read(ref _placementDisabled) == 0;

    // pid=0 addresses only the calling Linux thread, including when a model is reused by a thread pool.
    internal int GetAffinity(ulong* mask) => getAffinity is { } call ? call(0, 128, mask) : LibcSched.sched_getaffinity(0, 128, mask);
    internal int SetAffinity(ulong* mask) => setAffinity is { } call ? call(0, 128, mask) : LibcSched.sched_setaffinity(0, 128, mask);
    internal int GetAttr(SchedAttr* attributes) => getAttr is { } call ? call(0, attributes) : LibcSched.GetAttr(attributes);
    internal int SetAttr(SchedAttr* attributes) => setAttr is { } call ? call(0, attributes) : LibcSched.SetAttr(attributes);

    internal void Fail(string operation, int errno)
    {
        if (Placement!.OnFailure == PlacementFailureMode.Throw)
        {
            throw new RknnException($"Caller thread placement {operation} failed, errno {errno}.");
        }
        if (Interlocked.Exchange(ref _placementDisabled, 1) == 0 && logger.IsEnabled(LogLevel.Warning))
        {
            Log.PlacementFailed(logger, operation, errno);
        }
    }

    internal void WarnRestore(string operation, int errno)
    {
        if (Interlocked.Exchange(ref _restoreWarning, 1) == 0 && logger.IsEnabled(LogLevel.Warning))
        {
            Log.PlacementRestoreFailed(logger, operation, errno);
        }
    }
}
