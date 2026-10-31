using System.Runtime.InteropServices;

namespace RKNNSharp;

// Stack-only storage keeps the complete 1024-bit original affinity, including CPUs above 63.
internal unsafe ref struct PlacementScope
{
    private CpuSet _originalAffinity;
    private SchedAttr _originalAttr;
    private bool _affinityChanged, _attrChanged;
    private PlacementState? _state;

    internal static PlacementScope Enter(PlacementState state)
    {
        var scope = new PlacementScope { _state = state };
        if (!state.ShouldEnter) { return scope; }
        CallerThreadPlacement requested = state.Placement!;
        string operation = "sched_getaffinity";
        try
        {
            if (requested.CpuMask.HasValue && state.GetAffinity(scope._originalAffinity.Words) != 0)
            {
                Fail(ref scope, operation);
                return scope;
            }
            if (requested.UtilClampMin.HasValue)
            {
                operation = "sched_getattr";
                scope._originalAttr.Size = (uint)sizeof(SchedAttr);
                if (state.GetAttr(&scope._originalAttr) != 0) { Fail(ref scope, operation); return scope; }
            }
            if (requested.CpuMask is ulong mask)
            {
                operation = "sched_setaffinity";
                CpuSet target = default;
                target.Words[0] = mask;
                if (state.SetAffinity(target.Words) != 0) { Fail(ref scope, operation); return scope; }
                scope._affinityChanged = true;
            }
            if (requested.UtilClampMin is int minimum)
            {
                operation = "sched_setattr";
                SchedAttr target = scope._originalAttr;
                target.Size = (uint)sizeof(SchedAttr);
                target.Flags |= LibcSched.SchedFlagKeepAll | LibcSched.SchedFlagUtilClampMin;
                target.UtilMin = (uint)minimum;
                if (state.SetAttr(&target) != 0) { Fail(ref scope, operation); return scope; }
                scope._attrChanged = true;
            }
            return scope;
        }
        catch
        {
            // Enter can throw before a using variable exists. Undo earlier successful changes here too.
            scope.Dispose();
            throw;
        }
    }

    private static void Fail(scoped ref PlacementScope scope, string operation)
    {
        int errno = Marshal.GetLastPInvokeError();
        scope.Dispose(); // Roll back any earlier successful change before either continuing or throwing.
        scope._state!.Fail(operation, errno);
    }

    public void Dispose()
    {
        if (_attrChanged)
        {
            _attrChanged = false;
            SchedAttr restore = _originalAttr;
            restore.Size = (uint)sizeof(SchedAttr);
            restore.Flags |= LibcSched.SchedFlagKeepAll | LibcSched.SchedFlagUtilClampMin;
            if (_state!.SetAttr(&restore) != 0) { _state.WarnRestore("sched_setattr", Marshal.GetLastPInvokeError()); }
        }
        if (_affinityChanged)
        {
            _affinityChanged = false;
            fixed (ulong* original = _originalAffinity.Words)
            {
                if (_state!.SetAffinity(original) != 0) { _state.WarnRestore("sched_setaffinity", Marshal.GetLastPInvokeError()); }
            }
        }
    }
}
