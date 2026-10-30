using System.Buffers;

namespace RKNNSharp;

// One synchronous RunInto call only. Owns pin handles, never the caller's buffers/MemoryManager.
internal sealed unsafe class PinnedRunOutputs : IDisposable
{
    private readonly MemoryHandle[] _handles;
    private int _acquired;
    internal TensorOutput[] Outputs { get; }
    internal nint[] Addresses { get; }
    internal uint[] Counts { get; }

    internal PinnedRunOutputs(IReadOnlyList<TensorInfo> infos, IReadOnlyList<Memory<float>> outputBuffers)
    {
        ArgumentNullException.ThrowIfNull(outputBuffers);
        if (outputBuffers.Count != infos.Count) { throw new ArgumentException("Output buffer count must exactly match model outputs.", nameof(outputBuffers)); }
        Outputs = new TensorOutput[infos.Count];
        Addresses = new nint[infos.Count];
        Counts = new uint[infos.Count];
        _handles = new MemoryHandle[infos.Count];
        var views = new Memory<float>[infos.Count];
        // Capture every list entry once; pin and publish this same exact valid view.
        for (int i = 0; i < infos.Count; i++)
        {
            uint count = infos[i].ElementCount;
            if (count == 0 || count > int.MaxValue || count > uint.MaxValue / sizeof(float))
            { throw new ArgumentException($"Head {i} element count cannot be represented by the native float buffer contract.", nameof(outputBuffers)); }
            Memory<float> memory = outputBuffers[i];
            if (memory.Length < count) { throw new ArgumentException($"Head {i} requires at least {count} float elements.", nameof(outputBuffers)); }
            views[i] = memory.Slice(0, (int)count);
            Outputs[i] = new TensorOutput(infos[i], views[i]);
            Counts[i] = count;
        }
        try
        {
            for (int i = 0; i < views.Length; i++)
            {
                _handles[i] = views[i].Pin();
                _acquired = i + 1;
                nuint begin = (nuint)_handles[i].Pointer;
                nuint bytes = checked((nuint)Counts[i] * sizeof(float));
                if (begin == 0 || begin > nuint.MaxValue - bytes)
                { throw new ArgumentException($"Head {i} returned a null or overflowing pinned address.", nameof(outputBuffers)); }
                Addresses[i] = (nint)begin;
                for (int j = 0; j < i; j++)
                {
                    nuint previous = (nuint)Addresses[j];
                    nuint previousBytes = checked((nuint)Counts[j] * sizeof(float));
                    if (begin < previous + previousBytes && previous < begin + bytes)
                    { throw new ArgumentException($"Output heads {j} and {i} have overlapping valid buffers.", nameof(outputBuffers)); }
                }
            }
        }
        catch (Exception failure)
        {
            try { Dispose(); }
            catch (Exception cleanup) { throw new AggregateException("Pinning output buffers and releasing acquired pins both failed.", failure, cleanup); }
            throw;
        }
    }

    public void Dispose()
    {
        int acquired = _acquired;
        _acquired = 0;
        List<Exception>? failures = null;
        for (int i = acquired - 1; i >= 0; i--)
        {
            try { _handles[i].Dispose(); }
            catch (Exception error) { (failures ??= []).Add(error); }
            finally { _handles[i] = default; Addresses[i] = 0; }
        }
        if (failures is not null) { throw new AggregateException("One or more output pins could not be released.", failures); }
    }
}
