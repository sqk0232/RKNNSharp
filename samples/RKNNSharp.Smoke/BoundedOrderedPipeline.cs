using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace RKNNSharp.Smoke;

/// <summary>Sample-only bounded scheduler: permits include completed results awaiting ordered consumption.</summary>
internal static class BoundedOrderedPipeline
{
    internal sealed record Completion<T>(long FrameId, T? Result, Exception? Error);
    internal sealed record Counters(long Submitted, long Consumed, int MaximumInFlight);

    internal static async Task<Counters> RunAsync<TInput, TResult>(
        int workerCount, int capacity,
        Func<long, CancellationToken, ValueTask<TInput?>> pull,
        Func<int, TInput, TResult> execute,
        Action<TInput> release,
        Action<Completion<TResult>> consume,
        CancellationToken cancellationToken) where TInput : class
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(workerCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, workerCount);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var permits = new SemaphoreSlim(capacity, capacity);
        var input = Channel.CreateBounded<(long Id, TInput Value)>(new BoundedChannelOptions(workerCount)
        {
            FullMode = BoundedChannelFullMode.Wait, SingleWriter = true, SingleReader = false,
            AllowSynchronousContinuations = false
        });
        // Logically bounded by permits, including the ordered pending dictionary.
        var completed = Channel.CreateUnbounded<Completion<TResult>>(new UnboundedChannelOptions
        {
            SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false
        });
        Exception? failure = null;
        long submitted = 0, consumed = 0;
        int inFlight = 0, maximumInFlight = 0;

        void Fail(Exception exception)
        {
            Interlocked.CompareExchange(ref failure, exception, null);
            stopping.Cancel();
        }

        var producer = Task.Run(async () =>
        {
            try
            {
                for (long id = 0; ; id++)
                {
                    // Admission happens before pull, so no hidden seventh sample can exist.
                    await permits.WaitAsync(stopping.Token).ConfigureAwait(false);
                    TInput? value = null;
                    bool transferred = false;
                    try
                    {
                        value = await pull(id, stopping.Token).ConfigureAwait(false);
                        if (value is null) { break; }
                        int active = Interlocked.Increment(ref inFlight);
                        int maximum;
                        do { maximum = Volatile.Read(ref maximumInFlight); }
                        while (active > maximum && Interlocked.CompareExchange(ref maximumInFlight, active, maximum) != maximum);
                        await input.Writer.WriteAsync((id, value), stopping.Token).ConfigureAwait(false);
                        transferred = true;
                        Interlocked.Increment(ref submitted);
                    }
                    finally
                    {
                        if (!transferred)
                        {
                            try
                            {
                                if (value is not null)
                                {
                                    try { release(value); }
                                    finally { Interlocked.Decrement(ref inFlight); }
                                }
                            }
                            finally { permits.Release(); }
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception exception) { Fail(exception); }
            finally { input.Writer.TryComplete(); }
        }, CancellationToken.None);

        var workers = Enumerable.Range(0, workerCount).Select(worker => Task.Run(async () =>
        {
            // Drain queued ownership even after cancellation; every admitted ID emits a completion.
            await foreach (var item in input.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
            {
                TResult? result = default;
                Exception? error = null;
                try
                {
                    stopping.Token.ThrowIfCancellationRequested();
                    result = execute(worker, item.Value);
                }
                catch (OperationCanceledException exception) when (stopping.IsCancellationRequested) { error = exception; }
                catch (Exception exception) { error = exception; Fail(exception); }
                finally
                {
                    try { release(item.Value); }
                    catch (Exception exception) { error ??= exception; Fail(exception); }
                }
                completed.Writer.TryWrite(new Completion<TResult>(item.Id, result, error));
            }
        }, CancellationToken.None)).ToArray();

        var finish = Task.Run(async () =>
        {
            try { await producer.ConfigureAwait(false); await Task.WhenAll(workers).ConfigureAwait(false); }
            finally { completed.Writer.TryComplete(); }
        }, CancellationToken.None);

        var pending = new SortedDictionary<long, Completion<TResult>>();
        long nextId = 0;
        await foreach (var completion in completed.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            pending.Add(completion.FrameId, completion);
            while (pending.Remove(nextId, out var ordered))
            {
                try { consume(ordered); }
                catch (Exception exception) { Fail(exception); }
                finally
                {
                    nextId++; Interlocked.Increment(ref consumed);
                    Interlocked.Decrement(ref inFlight);
                    permits.Release();
                }
            }
        }
        await finish.ConfigureAwait(false);
        if (pending.Count != 0 || submitted != consumed) { throw new InvalidOperationException("Pipeline completion IDs contain a gap."); }
        if (failure is not null) { ExceptionDispatchInfo.Capture(failure).Throw(); }
        cancellationToken.ThrowIfCancellationRequested();
        return new Counters(submitted, consumed, maximumInFlight);
    }
}
