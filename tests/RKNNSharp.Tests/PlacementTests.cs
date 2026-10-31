using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RKNNSharp.Tests;

public sealed unsafe class PlacementTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScopeRestoresAllAffinityWordsAndUtilClampWithoutAllocations(bool customLogger)
    {
        var calls = new PlacementTestSyscalls();
        var disabledLogger = new CountingLogger(false);
        var state = calls.CreateState(new CallerThreadPlacement { CpuMask = 0xf0, UtilClampMin = 600 },
            customLogger ? disabledLogger : NullLogger.Instance);
        var current = calls.Scheduler.Current;
        for (int i = 0; i < 1000; i++) { using var warmup = PlacementScope.Enter(state); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        bool correct = true;
        for (int i = 0; i < 100000; i++)
        {
            using (var scope = PlacementScope.Enter(state))
            {
                correct &= current.Affinity.Words[0] == 0xf0 && current.Affinity.Words[10] == 0 && current.Attributes.UtilMin == 600;
            }
            correct &= current.Affinity.Words[0] == 0xff && current.Affinity.Words[10] == 0xa5 && current.Attributes.UtilMin == 17;
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(correct);
        Assert.Equal(0, allocated);
        Assert.Equal(202000, calls.SetAffinityCount);
        Assert.Equal(202000, calls.SetAttrCount);
        Assert.Equal(1UL, current.Attributes.Flags);
        Assert.Equal(3, current.Attributes.Nice);
        Assert.Equal(900u, current.Attributes.UtilMax);
        Assert.Equal(1UL | LibcSched.SchedFlagKeepAll | LibcSched.SchedFlagUtilClampMin, calls.Current.LastSetAttrFlags);
        Assert.Equal(0, disabledLogger.Calls);
        Assert.Equal(56, Marshal.SizeOf<SchedAttr>());
        Assert.Equal(128, Marshal.SizeOf<CpuSet>());
    }

    [Fact]
    public void DefaultPlacementNeverCallsSyscallsOrAllocates()
    {
        var calls = new PlacementTestSyscalls();
        var state = calls.CreateState(null, NullLogger.Instance);
        Assert.False(state.ShouldEnter);
        for (int i = 0; i < 1000; i++) { using var warmup = PlacementScope.Enter(state); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { using var scope = PlacementScope.Enter(state); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, calls.TotalCalls);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void UtilClampOnlyDoesNotTouchAffinityAndRestoresMinimum()
    {
        var calls = new PlacementTestSyscalls();
        var state = calls.CreateState(new CallerThreadPlacement { UtilClampMin = 1024 }, NullLogger.Instance);
        using (var scope = PlacementScope.Enter(state)) { Assert.Equal(1024u, calls.Scheduler.Current.Attributes.UtilMin); }
        Assert.Equal(17u, calls.Scheduler.Current.Attributes.UtilMin);
        Assert.Equal(0, calls.GetAffinityCount + calls.SetAffinityCount);
        Assert.Equal(2, calls.SetAttrCount);
    }

    [Theory]
    [InlineData("get-affinity")]
    [InlineData("get-attr")]
    [InlineData("set-affinity")]
    [InlineData("set-attr")]
    public void FailureWarnsOnceDisablesModelAndRollsBackPartialChanges(string failure)
    {
        var calls = new PlacementTestSyscalls { Failure = failure };
        var logger = new CountingLogger();
        var state = calls.CreateState(new CallerThreadPlacement { CpuMask = 0xf0, UtilClampMin = 600 }, logger);
        using (var scope = PlacementScope.Enter(state)) { }
        int count = calls.TotalCalls;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { using var scope = PlacementScope.Enter(state); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(count, calls.TotalCalls);
        Assert.False(state.ShouldEnter);
        Assert.Equal(1, logger.Calls);
        Assert.Equal(1102, logger.LastEvent.Id);
        Assert.Equal(LogLevel.Warning, logger.LastLevel);
        Assert.Contains("errno", logger.LastMessage);
        Assert.Equal(0xffUL, calls.Scheduler.Current.Affinity.Words[0]);
        Assert.Equal(0xa5UL, calls.Scheduler.Current.Affinity.Words[10]);
        Assert.Equal(17u, calls.Scheduler.Current.Attributes.UtilMin);
        Assert.Equal(failure == "set-attr" ? 2 : failure == "set-affinity" ? 1 : 0, calls.SetAffinityCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ThrowModeRollsBackBeforeExceptionAndDoesNotDisable(bool delegateThrows)
    {
        var calls = new PlacementTestSyscalls { Failure = delegateThrows ? null : "set-attr", ThrowSetAttr = delegateThrows };
        var state = calls.CreateState(new CallerThreadPlacement { CpuMask = 0xf0, UtilClampMin = 600, OnFailure = PlacementFailureMode.Throw }, NullLogger.Instance);
        if (delegateThrows) { Assert.Throws<InvalidOperationException>(() => EnterAndDispose(state)); }
        else
        {
            var error = Assert.Throws<RknnException>(() => EnterAndDispose(state));
            Assert.Contains("sched_setattr", error.Message);
            Assert.Contains("errno 1", error.Message);
        }
        Assert.True(state.ShouldEnter);
        Assert.Equal(0xffUL, calls.Scheduler.Current.Affinity.Words[0]);
        Assert.Equal(0xa5UL, calls.Scheduler.Current.Affinity.Words[10]);
        Assert.Equal(2, calls.SetAffinityCount); // Rollback and Enter's catch must not restore twice.
    }

    private static void EnterAndDispose(PlacementState state) { using var scope = PlacementScope.Enter(state); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RestoreFailureWarnsOncePerInstanceAndNeverThrows(bool attributes)
    {
        var firstCalls = new PlacementTestSyscalls { FailRestoreAffinity = !attributes, FailRestoreAttr = attributes };
        var secondCalls = new PlacementTestSyscalls { FailRestoreAffinity = !attributes, FailRestoreAttr = attributes };
        var firstLogger = new CountingLogger();
        var secondLogger = new CountingLogger();
        var options = new CallerThreadPlacement { CpuMask = 0xf0, UtilClampMin = 600, OnFailure = PlacementFailureMode.Throw };
        var first = firstCalls.CreateState(options, firstLogger);
        var second = secondCalls.CreateState(options, secondLogger);
        for (int i = 0; i < 3; i++)
        {
            using (var scope = PlacementScope.Enter(first)) { }
            using (var scope = PlacementScope.Enter(second)) { }
        }
        Assert.Equal(1, firstLogger.Calls);
        Assert.Equal(1, secondLogger.Calls);
        Assert.Equal(1103, firstLogger.LastEvent.Id);
        Assert.Equal(1103, secondLogger.LastEvent.Id);
        Assert.True(first.ShouldEnter);
        Assert.True(second.ShouldEnter);
        Assert.Equal(6, firstCalls.SetAffinityCount);
        Assert.Equal(6, secondCalls.SetAffinityCount);
        // Deliberately failed restores cannot satisfy full-restoration assertions.
        if (attributes) { Assert.Equal(0xffUL, firstCalls.Scheduler.Current.Affinity.Words[0]); }
        else { Assert.Equal(17u, firstCalls.Scheduler.Current.Attributes.UtilMin); }
    }

    [Fact]
    public void ScopeDisposesWhenInferenceBodyThrowsAndCanBeDisposedTwice()
    {
        var calls = new PlacementTestSyscalls();
        var state = calls.CreateState(new CallerThreadPlacement { CpuMask = 0xf0, UtilClampMin = 600 }, NullLogger.Instance);
        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using var scope = PlacementScope.Enter(state);
            throw new InvalidOperationException("Simulated inference failure.");
        }));
        var second = PlacementScope.Enter(state);
        second.Dispose();
        second.Dispose();
        Assert.Equal(0xffUL, calls.Scheduler.Current.Affinity.Words[0]);
        Assert.Equal(17u, calls.Scheduler.Current.Attributes.UtilMin);
        Assert.Equal(4, calls.SetAffinityCount);
        Assert.Equal(4, calls.SetAttrCount);
    }

    [Fact]
    public void NestedModelsRestoreTheCallingThreadsPreviousPlacement()
    {
        var scheduler = new PlacementTestScheduler();
        var firstCalls = new PlacementTestSyscalls(scheduler);
        var secondCalls = new PlacementTestSyscalls(scheduler);
        var first = firstCalls.CreateState(new CallerThreadPlacement { CpuMask = 1, UtilClampMin = 100 }, NullLogger.Instance);
        var second = secondCalls.CreateState(new CallerThreadPlacement { CpuMask = 2, UtilClampMin = 200 }, NullLogger.Instance);
        using (var outer = PlacementScope.Enter(first))
        {
            using (var inner = PlacementScope.Enter(second))
            {
                Assert.Equal(2UL, scheduler.Current.Affinity.Words[0]);
                Assert.Equal(200u, scheduler.Current.Attributes.UtilMin);
            }
            Assert.Equal(1UL, scheduler.Current.Affinity.Words[0]);
            Assert.Equal(100u, scheduler.Current.Attributes.UtilMin);
        }
        Assert.Equal(0xffUL, scheduler.Current.Affinity.Words[0]);
        Assert.Equal(17u, scheduler.Current.Attributes.UtilMin);
    }

    internal sealed class CountingLogger(bool enabled = true) : ILogger
    {
        internal int Calls;
        internal EventId LastEvent;
        internal LogLevel LastLevel;
        internal string? LastMessage;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => enabled;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref Calls);
            LastEvent = eventId;
            LastLevel = logLevel;
            LastMessage = formatter(state, exception);
        }
    }
}
