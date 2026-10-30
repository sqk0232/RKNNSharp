using System.Buffers;
using System.Collections;
using System.Runtime.InteropServices;
using Xunit;

namespace RKNNSharp.Tests;

public sealed unsafe class RunIntoTests
{
    private static TensorInfo[] Infos(params uint[] counts) => counts.Select((count, index) =>
        new TensorInfo((uint)index, $"head{index}", [1, count], count, 0, 2, 2, 0, 1)).ToArray();

    [Fact]
    public void PublicOverloadsKeepTheExistingResultType()
    {
        foreach (var inputType in new[] { typeof(DmaFrame), typeof(DmaImage) })
        {
            var method = typeof(RknnModel).GetMethod("RunInto", [inputType, typeof(IReadOnlyList<Memory<float>>)]);
            Assert.NotNull(method);
            Assert.Equal(typeof(InferenceResult), method.ReturnType);
        }
    }

    [Fact]
    public void NullCountAndShortBuffersAreRejectedBeforeAnyPin()
    {
        Assert.Throws<ArgumentNullException>(() => new PinnedRunOutputs(Infos(4), null!));
        Assert.Throws<ArgumentException>(() => new PinnedRunOutputs(Infos(4), []));
        var first = new TrackingMemory(4, 4096);
        var second = new TrackingMemory(3, 8192);
        Assert.Throws<ArgumentException>(() => new PinnedRunOutputs(Infos(4, 4), [first.View, second.View]));
        Assert.Equal(0, first.PinCalls);
        Assert.Equal(0, second.PinCalls);
    }

    [Fact]
    public void OversizedAndEmptyModelCountsCannotReachNativeDescriptors()
    {
        Assert.Throws<ArgumentException>(() => new PinnedRunOutputs(Infos(0), [Memory<float>.Empty]));
        Assert.Throws<ArgumentException>(() => new PinnedRunOutputs(Infos(uint.MaxValue), [Memory<float>.Empty]));
    }

    [Fact]
    public void DisjointValidSlicesAliasCallerStorageAndLeaveAllTailsUntouched()
    {
        float[] storage = Enumerable.Repeat(-123f, 16).ToArray();
        TensorOutput[] result;
        using (var pinned = new PinnedRunOutputs(Infos(3, 4), [storage.AsMemory(2, 10), storage.AsMemory(5, 8)]))
        {
            new Span<float>((void*)pinned.Addresses[0], (int)pinned.Counts[0]).Fill(1);
            new Span<float>((void*)pinned.Addresses[1], (int)pinned.Counts[1]).Fill(2);
            result = pinned.Outputs;
            Assert.Equal(new uint[] { 3, 4 }, pinned.Counts);
            Assert.True(MemoryMarshal.TryGetArray(result[0].Values, out var first));
            Assert.Same(storage, first.Array); Assert.Equal(2, first.Offset); Assert.Equal(3, first.Count);
            Assert.True(MemoryMarshal.TryGetArray(result[1].Values, out var second));
            Assert.Same(storage, second.Array); Assert.Equal(5, second.Offset); Assert.Equal(4, second.Count);
        }
        for (int i = 0; i < storage.Length; i++) { Assert.Equal(i is >= 2 and < 5 ? 1 : i is >= 5 and < 9 ? 2 : -123, storage[i]); }
        storage[2] = 17;
        Assert.Equal(17, result[0].Values.Span[0]);
        using var consumerPin = result[0].Values.Pin();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.Equal(17, *(float*)consumerPin.Pointer);
    }

    [Fact]
    public void OverlappingValidRangesAreRejectedAndEveryPinIsReleased()
    {
        var first = new TrackingMemory(4, 4096);
        var second = new TrackingMemory(4, 4104);
        Assert.Throws<ArgumentException>(() => new PinnedRunOutputs(Infos(4, 4), [first.View, second.View]));
        Assert.Equal(1, first.UnpinCalls); Assert.Equal(1, second.UnpinCalls);
        Assert.Equal(0, first.ActivePins); Assert.Equal(0, second.ActivePins);
    }

    [Fact]
    public void ThrowingLaterPinReleasesEarlierPinsWithoutDisposingCallerOwners()
    {
        var first = new TrackingMemory(4, 4096);
        var second = new TrackingMemory(4, 8192, failPin: true);
        var third = new TrackingMemory(4, 12288);
        Assert.Throws<NotSupportedException>(() => new PinnedRunOutputs(Infos(4, 4, 4), [first.View, second.View, third.View]));
        Assert.Equal(1, first.PinCalls); Assert.Equal(1, first.UnpinCalls); Assert.Equal(0, first.ActivePins);
        Assert.Equal(1, second.PinCalls); Assert.Equal(0, second.UnpinCalls);
        Assert.Equal(0, third.PinCalls);
        Assert.Equal(0, first.OwnerDisposeCalls); Assert.Equal(0, second.OwnerDisposeCalls);
    }

    [Fact]
    public void NullAndOverflowingPinnedPointersAreRejectedAndUnpinned()
    {
        foreach (nint address in new nint[] { 0, unchecked((nint)(nuint.MaxValue - 3)) })
        {
            var memory = new TrackingMemory(2, address);
            Assert.Throws<ArgumentException>(() => new PinnedRunOutputs(Infos(2), [memory.View]));
            Assert.Equal(1, memory.UnpinCalls); Assert.Equal(0, memory.ActivePins);
        }
    }

    [Fact]
    public void CaptureReadsEachListEntryOnlyOnce()
    {
        float[] first = new float[3], second = new float[4];
        var list = new ChangingList([first.AsMemory(), second.AsMemory()]);
        using var pinned = new PinnedRunOutputs(Infos(3, 4), list);
        Assert.Equal(1, list.CountReads);
        Assert.Equal(new[] { 1, 1 }, list.Reads);
        Assert.True(MemoryMarshal.TryGetArray(pinned.Outputs[0].Values, out var firstView));
        Assert.Same(first, firstView.Array);
    }

    [Fact]
    public void DisposeIsIdempotentAndClearsAcquiredHandles()
    {
        var memory = new TrackingMemory(4, 4096);
        var pinned = new PinnedRunOutputs(Infos(4), [memory.View]);
        pinned.Dispose(); pinned.Dispose();
        Assert.Equal(1, memory.UnpinCalls); Assert.Equal(0, memory.ActivePins); Assert.Equal(0, memory.OwnerDisposeCalls);
        Assert.Equal(0, pinned.Addresses[0]);
    }

    [Fact]
    public void AFailingUnpinDoesNotPreventReleasingOtherAcquiredPins()
    {
        var first = new TrackingMemory(4, 4096);
        var second = new TrackingMemory(4, 8192, failUnpin: true);
        var pinned = new PinnedRunOutputs(Infos(4, 4), [first.View, second.View]);
        Assert.Throws<AggregateException>(pinned.Dispose);
        Assert.Equal(1, first.UnpinCalls); Assert.Equal(1, second.UnpinCalls);
        Assert.Equal(0, first.ActivePins); Assert.Equal(0, second.ActivePins);
        pinned.Dispose();
        Assert.Equal(1, second.UnpinCalls);
    }

    private sealed class ChangingList(Memory<float>[] values) : IReadOnlyList<Memory<float>>
    {
        internal int CountReads { get; private set; }
        internal int[] Reads { get; } = new int[values.Length];
        public int Count { get { CountReads++; return values.Length; } }
        public Memory<float> this[int index] => ++Reads[index] == 1 ? values[index] : Memory<float>.Empty;
        public IEnumerator<Memory<float>> GetEnumerator() => throw new NotSupportedException();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class TrackingMemory(int length, nint address, bool failPin = false, bool failUnpin = false) : MemoryManager<float>
    {
        private readonly float[] _values = new float[length];
        internal Memory<float> View => CreateMemory(length);
        internal int PinCalls, UnpinCalls, ActivePins, OwnerDisposeCalls;
        public override Span<float> GetSpan() => _values;
        public override MemoryHandle Pin(int elementIndex = 0)
        {
            PinCalls++;
            if (failPin) { throw new NotSupportedException("This output manager does not support Pin."); }
            ActivePins++;
            return new MemoryHandle((void*)(address + elementIndex * sizeof(float)), default, this);
        }
        public override void Unpin()
        {
            UnpinCalls++; ActivePins--;
            if (failUnpin) { throw new InvalidOperationException("Injected unpin failure."); }
        }
        protected override void Dispose(bool disposing) => OwnerDisposeCalls++;
    }
}
