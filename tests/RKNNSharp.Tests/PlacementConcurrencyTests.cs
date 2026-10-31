using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace RKNNSharp.Tests;

// A separate class may run alongside all other placement tests: no shared stubs or collection serialization.
public sealed unsafe class PlacementConcurrencyTests(ITestOutputHelper output)
{
    [Fact]
    public void SixModelStatesAcrossEightThreadsIsolateFailuresWarningsAndRestoreEveryThread()
    {
        const int threadCount = 8, rounds = 40, modelCount = 6;
        var scheduler = new PlacementTestScheduler();
        using var start = new Barrier(threadCount);
        using var affinityFailure = new Barrier(threadCount);
        using var attrFailure = new Barrier(threadCount);
        var calls = new PlacementTestSyscalls[modelCount];
        var loggers = new PlacementTests.CountingLogger[modelCount];
        var states = new PlacementState[modelCount];
        for (int i = 0; i < modelCount; i++)
        {
            calls[i] = new PlacementTestSyscalls(scheduler)
            {
                Failure = i < 4 ? (i % 2 == 0 ? "set-affinity" : "set-attr") : null,
                FirstFailureBarrier = i == 0 ? affinityFailure : i == 1 ? attrFailure : null
            };
            loggers[i] = new PlacementTests.CountingLogger();
            states[i] = calls[i].CreateState(new CallerThreadPlacement
            {
                CpuMask = 1UL << (i + 1),
                UtilClampMin = 100 + i,
                OnFailure = i < 2 ? PlacementFailureMode.WarnOnceAndContinue : PlacementFailureMode.Throw
            }, loggers[i]);
        }
        var errors = new ConcurrentQueue<Exception>();
        var threads = new Thread[threadCount];
        var bodyExceptions = new int[threadCount];
        var placementExceptions = new int[threadCount];
        var visits = new int[threadCount, modelCount];
        for (int worker = 0; worker < threadCount; worker++)
        {
            int index = worker;
            threads[worker] = new Thread(() =>
            {
                try
                {
                    var current = scheduler.Current;
                    // Each worker starts with a different full CPU set and unrelated scheduling parameters.
                    for (int word = 0; word < 16; word++) { current.Affinity.Words[word] = (ulong)(1000 * (index + 1) + word); }
                    current.Attributes = new SchedAttr
                    {
                        Size = 56, Policy = 3, Flags = 1, Nice = index, Priority = 7,
                        Runtime = (ulong)(20 + index), Deadline = 30, Period = 40,
                        UtilMin = (uint)(10 + index), UtilMax = (uint)(900 + index)
                    };
                    CpuSet originalAffinity = current.Affinity;
                    SchedAttr originalAttributes = current.Attributes;
                    Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(10)));
                    for (int round = 0; round < rounds; round++)
                    {
                        for (int step = 0; step < modelCount; step++)
                        {
                            // All threads visit every state; rotating between rounds increases interleaving.
                            int model = (step + round) % modelCount;
                            bool throwBody = model >= 4 && (round + index) % 3 == 0;
                            visits[index, model]++;
                            try
                            {
                                using var scope = PlacementScope.Enter(states[model]);
                                if (model is 2 or 3) { Assert.Fail("Throw-mode syscall failure must escape Enter."); }
                                if (model >= 4)
                                {
                                    Assert.Equal(1UL << (model + 1), current.Affinity.Words[0]);
                                    for (int word = 1; word < 16; word++) { Assert.Equal(0UL, current.Affinity.Words[word]); }
                                    Assert.Equal((uint)(100 + model), current.Attributes.UtilMin);
                                }
                                Thread.Yield();
                                if (throwBody) { throw new InvalidOperationException("Simulated inference body failure."); }
                            }
                            catch (RknnException) when (model is 2 or 3) { placementExceptions[index]++; }
                            catch (InvalidOperationException) when (throwBody) { bodyExceptions[index]++; }
                            for (int word = 0; word < 16; word++) { Assert.Equal(originalAffinity.Words[word], current.Affinity.Words[word]); }
                            Assert.Equal(originalAttributes, current.Attributes);
                            Thread.Yield();
                        }
                    }
                }
                catch (Exception error) { errors.Enqueue(error); }
            }) { IsBackground = true, Name = $"placement-test-{worker}" };
            threads[worker].Start();
        }
        foreach (Thread thread in threads) { Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Placement worker did not finish."); }
        Assert.Empty(errors);
        for (int worker = 0; worker < threadCount; worker++)
        {
            Assert.Equal(rounds * 2, placementExceptions[worker]);
            Assert.True(bodyExceptions[worker] > 0);
            for (int model = 0; model < modelCount; model++) { Assert.Equal(rounds, visits[worker, model]); }
        }
        for (int model = 0; model < modelCount; model++)
        {
            Assert.Equal(threadCount, calls[model].ThreadCounts.Count);
            foreach (var count in calls[model].ThreadCounts.Values)
            {
                int attempts = model < 2 ? 1 : rounds;
                Assert.Equal(attempts, count.GetAffinity);
                Assert.Equal(attempts, count.GetAttr);
                Assert.Equal(attempts * (model % 2 == 0 && model < 4 ? 1 : 2), count.SetAffinity);
                Assert.Equal(model < 4 ? (model % 2 == 0 ? 0 : attempts) : attempts * 2, count.SetAttr);
            }
            Assert.Equal(model >= 2, states[model].ShouldEnter);
            Assert.Equal(model < 2 ? 1 : 0, loggers[model].Calls);
            if (model < 2)
            {
                Assert.Equal(1102, loggers[model].LastEvent.Id);
                Assert.Equal(LogLevel.Warning, loggers[model].LastLevel);
            }
            output.WriteLine($"Model {model}: threads={calls[model].ThreadCounts.Count}, getAffinity={calls[model].GetAffinityCount}, setAffinity={calls[model].SetAffinityCount}, getAttr={calls[model].GetAttrCount}, setAttr={calls[model].SetAttrCount}, warnings={loggers[model].Calls}, enabled={states[model].ShouldEnter}");
        }
        output.WriteLine($"All {threadCount} threads restored all 16 affinity words and all sched_attr fields after {threadCount * rounds * modelCount} visits; placement exceptions={placementExceptions.Sum()}, body exceptions={bodyExceptions.Sum()}.");
    }
}
