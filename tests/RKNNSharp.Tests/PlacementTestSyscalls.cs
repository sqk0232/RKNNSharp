using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Xunit;

namespace RKNNSharp.Tests;

// Shared only by the model substitutes created within a single test.
internal sealed unsafe class PlacementTestScheduler
{
    private readonly ConcurrentDictionary<int, Values> _threads = new();
    internal Values Current
    {
        get
        {
            int id = Environment.CurrentManagedThreadId;
            if (_threads.TryGetValue(id, out var values)) { return values; }
            values = new Values();
            Assert.True(_threads.TryAdd(id, values));
            return values;
        }
    }

    internal sealed class Values
    {
        internal CpuSet Affinity;
        internal SchedAttr Attributes = new() { Size = 56, UtilMin = 17, UtilMax = 900, Flags = 1, Nice = 3 };
        internal Values() { Affinity.Words[0] = 0xff; Affinity.Words[10] = 0xa5; }
    }
}

// Each substitute owns counters/failure behavior; its dictionary models current-thread syscalls.
internal sealed unsafe class PlacementTestSyscalls(PlacementTestScheduler? scheduler = null)
{
    private readonly ConcurrentDictionary<int, Counts> _counts = new();
    internal PlacementTestScheduler Scheduler { get; } = scheduler ?? new PlacementTestScheduler();
    internal string? Failure { get; init; }
    internal bool ThrowSetAttr { get; init; }
    internal bool FailRestoreAffinity { get; init; }
    internal bool FailRestoreAttr { get; init; }
    internal Barrier? FirstFailureBarrier { get; init; }
    internal int GetAffinityCount => _counts.Values.Sum(c => c.GetAffinity);
    internal int SetAffinityCount => _counts.Values.Sum(c => c.SetAffinity);
    internal int GetAttrCount => _counts.Values.Sum(c => c.GetAttr);
    internal int SetAttrCount => _counts.Values.Sum(c => c.SetAttr);
    internal int TotalCalls => GetAffinityCount + SetAffinityCount + GetAttrCount + SetAttrCount;
    internal IReadOnlyDictionary<int, Counts> ThreadCounts => _counts;
    internal Counts Current
    {
        get
        {
            int id = Environment.CurrentManagedThreadId;
            if (_counts.TryGetValue(id, out var counts)) { return counts; }
            counts = new Counts();
            Assert.True(_counts.TryAdd(id, counts));
            return counts;
        }
    }

    internal PlacementState CreateState(CallerThreadPlacement? placement, ILogger logger) =>
        new(placement, logger, GetAffinity, SetAffinity, GetAttr, SetAttr);

    private int GetAffinity(int pid, nuint size, ulong* mask)
    {
        if (pid != 0 || size != 128) { throw new InvalidOperationException("Affinity syscall must address only the caller with a full CPU set."); }
        Current.GetAffinity++;
        if (Failure == "get-affinity") { return Fail(1); }
        var current = Scheduler.Current;
        for (int i = 0; i < 16; i++) { mask[i] = current.Affinity.Words[i]; }
        return 0;
    }

    private int SetAffinity(int pid, nuint size, ulong* mask)
    {
        if (pid != 0 || size != 128) { throw new InvalidOperationException("Affinity syscall must address only the caller with a full CPU set."); }
        var counts = Current;
        counts.SetAffinity++;
        if (Failure == "set-affinity")
        {
            if (counts.SetAffinity == 1 && FirstFailureBarrier is { } barrier) { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); }
            return Fail(22);
        }
        if (FailRestoreAffinity && counts.SetAffinity % 2 == 0) { return Fail(22); }
        var current = Scheduler.Current;
        for (int i = 0; i < 16; i++) { current.Affinity.Words[i] = mask[i]; }
        return 0;
    }

    private int GetAttr(int pid, SchedAttr* attributes)
    {
        if (pid != 0 || attributes->Size != 56) { throw new InvalidOperationException("Attribute syscall must address only the caller with sched_attr size 56."); }
        Current.GetAttr++;
        if (Failure == "get-attr") { return Fail(38); }
        *attributes = Scheduler.Current.Attributes;
        return 0;
    }

    private int SetAttr(int pid, SchedAttr* attributes)
    {
        if (pid != 0 || attributes->Size != 56) { throw new InvalidOperationException("Attribute syscall must address only the caller with sched_attr size 56."); }
        var counts = Current;
        counts.SetAttr++;
        counts.LastSetAttrFlags = attributes->Flags;
        if (ThrowSetAttr) { throw new InvalidOperationException("Simulated syscall delegate failure."); }
        if (Failure == "set-attr")
        {
            if (counts.SetAttr == 1 && FirstFailureBarrier is { } barrier) { Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10))); }
            return Fail(1);
        }
        if (FailRestoreAttr && counts.SetAttr % 2 == 0) { return Fail(1); }
        // KEEP_ALL preserves the unrelated scheduling fields and flags.
        Scheduler.Current.Attributes.UtilMin = attributes->UtilMin;
        return 0;
    }

    private static int Fail(int errno) { Marshal.SetLastPInvokeError(errno); return -1; }

    internal sealed class Counts
    {
        internal int GetAffinity, SetAffinity, GetAttr, SetAttr;
        internal ulong LastSetAttrFlags;
    }
}
