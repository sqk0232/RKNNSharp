using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RKNNSharp;

/// <summary>按元素顺序找出第一个 NaN/Inf。空张量视为全部有限。</summary>
internal static class FiniteTensor
{
    internal static int FirstNonFinite(ReadOnlySpan<float> values)
    {
        if (Vector256.IsHardwareAccelerated) { return Scan(values, 8); }
        if (Vector128.IsHardwareAccelerated) { return Scan(values, 4); }
        return Scan(values, 1);
    }

    internal static int Scan(ReadOnlySpan<float> values, int lanes)
    {
        if (values.Length == 0) { return -1; }
        ref float origin = ref MemoryMarshal.GetReference(values);
        nuint length = (nuint)values.Length;
        nuint index = 0;
        // 指数位全 1 即 Inf/NaN，与 float.IsFinite 的判定相同。
        if (lanes >= 8 && Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> exponent = Vector256.Create(0x7F800000u);
            for (; index + 8 <= length; index += 8)
            {
                Vector256<uint> bits = Vector256.LoadUnsafe(ref origin, index).AsUInt32();
                Vector256<uint> nonFinite = Vector256.Equals(Vector256.BitwiseAnd(bits, exponent), exponent);
                if (nonFinite != Vector256<uint>.Zero)
                {
                    int found = FirstInRange(ref origin, index, index + 8);
                    if (found >= 0) { return found; }
                }
            }
        }
        else if (lanes >= 4 && Vector128.IsHardwareAccelerated)
        {
            Vector128<uint> exponent = Vector128.Create(0x7F800000u);
            for (; index + 4 <= length; index += 4)
            {
                Vector128<uint> bits = Vector128.LoadUnsafe(ref origin, index).AsUInt32();
                Vector128<uint> nonFinite = Vector128.Equals(Vector128.BitwiseAnd(bits, exponent), exponent);
                if (nonFinite != Vector128<uint>.Zero)
                {
                    int found = FirstInRange(ref origin, index, index + 4);
                    if (found >= 0) { return found; }
                }
            }
        }
        for (; index < length; index++)
        {
            if (!float.IsFinite(Unsafe.Add(ref origin, index))) { return (int)index; }
        }
        return -1;
    }

    private static int FirstInRange(ref float origin, nuint begin, nuint end)
    {
        for (nuint index = begin; index < end; index++)
        {
            if (!float.IsFinite(Unsafe.Add(ref origin, index))) { return (int)index; }
        }
        return -1;
    }
}
