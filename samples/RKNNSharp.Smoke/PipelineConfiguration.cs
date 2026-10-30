using RKNNSharp;

namespace RKNNSharp.Smoke;

// Sample scheduling only; the core library keeps its synchronous session API.
internal sealed class PipelineConfiguration
{
    internal int SessionsPerCore { get; }
    internal int WorkerCount { get; }
    internal int Capacity { get; }

    internal PipelineConfiguration(int sessionsPerCore = 1, int capacity = 0, int workerCount = 0)
    {
        if (sessionsPerCore is < 1 or > 2) { throw new ArgumentOutOfRangeException(nameof(sessionsPerCore)); }
        SessionsPerCore = sessionsPerCore;
        WorkerCount = workerCount == 0 ? sessionsPerCore * 3 : workerCount;
        if (WorkerCount is not (1 or 3 or 6) || WorkerCount > sessionsPerCore * 3)
        { throw new ArgumentOutOfRangeException(nameof(workerCount)); }
        Capacity = capacity == 0 ? WorkerCount * 2 : capacity;
        if (Capacity < WorkerCount || Capacity > 24) { throw new ArgumentOutOfRangeException(nameof(capacity)); }
    }

    internal NpuCoreMask CoreForWorker(int worker)
    {
        if ((uint)worker >= WorkerCount) { throw new ArgumentOutOfRangeException(nameof(worker)); }
        return (worker % 3) switch { 0 => NpuCoreMask.Core0, 1 => NpuCoreMask.Core1, _ => NpuCoreMask.Core2 };
    }

    internal int CapacityForWorkers(int workers) => workers == WorkerCount ? Capacity : workers * 2;
    internal int[] ReplayWorkerCounts => WorkerCount switch { 1 => [1], 3 => [1, 3], _ => [1, 3, 6] };
}
