using System.Collections.Concurrent;
using RKNNSharp.Smoke;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class PipelineTests
{
    [Fact]
    public async Task SlowFirstRequestKeepsPendingResultsBoundedAndOrdered()
    {
        var tracker = new Tracker();
        var observed = new List<long>();
        var result = await BoundedOrderedPipeline.RunAsync<Lease, long>(3, 6,
            (id, _) => ValueTask.FromResult(id < 200 ? tracker.Create(id) : null),
            (_, lease) => { if (lease.Id == 0) { Thread.Sleep(100); } return lease.Id; },
            lease => lease.Dispose(),
            completion => { Assert.Null(completion.Error); Assert.Equal(completion.FrameId, completion.Result); observed.Add(completion.FrameId); },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Enumerable.Range(0, 200).Select(i => (long)i), observed);
        Assert.Equal(200, result.Submitted); Assert.Equal(200, result.Consumed);
        Assert.InRange(result.MaximumInFlight, 1, 6);
        Assert.Equal(tracker.Acquired, tracker.Released);
    }

    [Fact]
    public async Task ExecutionFailureDrainsEveryAdmittedOwnerWithoutIdGaps()
    {
        var tracker = new Tracker();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await BoundedOrderedPipeline.RunAsync<Lease, long>(3, 6,
            (id, _) => ValueTask.FromResult(id < 200 ? tracker.Create(id) : null),
            (_, lease) => lease.Id == 4 ? throw new InvalidOperationException("expected failure") : lease.Id,
            lease => lease.Dispose(), _ => { }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(tracker.Acquired >= 5);
        Assert.Equal(tracker.Acquired, tracker.Released);
    }

    [Fact]
    public async Task CancellationStopsAdmissionButJoinsAndReleasesAllWork()
    {
        var tracker = new Tracker();
        using var cancellation = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await BoundedOrderedPipeline.RunAsync<Lease, long>(3, 6,
            (id, _) => ValueTask.FromResult<Lease?>(tracker.Create(id)),
            (_, lease) => { Thread.Sleep(5); return lease.Id; },
            lease => lease.Dispose(), _ => { }, cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(tracker.Acquired, tracker.Released);
    }

    [Theory]
    [InlineData(3, 6)]
    [InlineData(6, 12)]
    [InlineData(6, 24)]
    public async Task ReplayKeepsSourceIdentityAndExclusiveWorkerOwnership(int workerCount, int capacity)
    {
        var sources = Enumerable.Range(0, 6).Select(id => new ReplaySource(id)).ToArray();
        int[] activeWorkers = new int[workerCount];
        int released = 0;
        long nextId = 0;
        var counters = await BoundedOrderedPipeline.RunAsync<ReplayInput, (long RequestId, ReplaySource Source)>(workerCount, capacity,
            (id, _) => ValueTask.FromResult(id < 300 ? new ReplayInput(id, sources[(int)(id % 6)]) : null),
            (worker, request) =>
            {
                Assert.Equal(0, Interlocked.Exchange(ref activeWorkers[worker], 1));
                try
                {
                    if ((request.RequestId & 3) == 0) { Thread.Sleep(1); }
                    return (request.RequestId, request.Source);
                }
                finally { Interlocked.Exchange(ref activeWorkers[worker], 0); }
            },
            _ => Interlocked.Increment(ref released),
            completion =>
            {
                Assert.Null(completion.Error);
                Assert.Equal(nextId++, completion.FrameId);
                Assert.Equal(completion.FrameId, completion.Result.RequestId);
                Assert.Same(sources[(int)(completion.FrameId % 6)], completion.Result.Source);
            }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(300, counters.Consumed);
        Assert.Equal(300, released);
        Assert.InRange(counters.MaximumInFlight, 1, capacity);
    }

    [Fact]
    public void SixSessionConfigurationPinsIndependentWorkersAndBoundsCapacity()
    {
        var configuration = new PipelineConfiguration(2);
        Assert.Equal(6, configuration.WorkerCount);
        Assert.Equal(12, configuration.Capacity);
        Assert.Equal(new[] { 1, 3, 6 }, configuration.ReplayWorkerCounts);
        Assert.Equal(new[] { NpuCoreMask.Core0, NpuCoreMask.Core1, NpuCoreMask.Core2,
            NpuCoreMask.Core0, NpuCoreMask.Core1, NpuCoreMask.Core2 },
            Enumerable.Range(0, 6).Select(configuration.CoreForWorker));
        Assert.Equal(2, configuration.CapacityForWorkers(1));
        Assert.Equal(6, configuration.CapacityForWorkers(3));
        Assert.Equal(24, new PipelineConfiguration(2, 24).CapacityForWorkers(6));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineConfiguration(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineConfiguration(2, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineConfiguration(2, 25));
        Assert.Throws<ArgumentOutOfRangeException>(() => configuration.CoreForWorker(6));
        Assert.Equal(1, new PipelineConfiguration(1, 0, 1).WorkerCount);
        Assert.Equal(2, new PipelineConfiguration(1, 0, 1).Capacity);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PipelineConfiguration(1, 0, 6));
    }

    [Fact]
    public async Task ConsumerFailureAlsoReleasesQueuedOwners()
    {
        var tracker = new Tracker();
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await BoundedOrderedPipeline.RunAsync<Lease, long>(3, 6,
            (id, _) => ValueTask.FromResult<Lease?>(tracker.Create(id)), (_, lease) => lease.Id,
            lease => lease.Dispose(), completion => { if (completion.FrameId == 1) { throw new InvalidOperationException("expected consumer failure"); } },
            CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(tracker.Acquired, tracker.Released);
    }

    private sealed record ReplaySource(int Id);
    private sealed record ReplayInput(long RequestId, ReplaySource Source);

    private sealed class Tracker
    {
        private readonly ConcurrentDictionary<long, bool> _released = new();
        internal int Acquired;
        internal int Released;
        internal Lease Create(long id) { Interlocked.Increment(ref Acquired); return new Lease(id, this); }
        internal void Release(long id)
        {
            if (!_released.TryAdd(id, true)) { throw new InvalidOperationException("Input released twice."); }
            Interlocked.Increment(ref Released);
        }
    }

    private sealed class Lease(long id, Tracker tracker) : IDisposable
    {
        internal long Id => id;
        public void Dispose() => tracker.Release(id);
    }
}
