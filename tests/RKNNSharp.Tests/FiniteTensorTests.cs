using Xunit;

namespace RKNNSharp.Tests;

public sealed class FiniteTensorTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(8)]
    public void ScannerMatchesIsFiniteAtEveryIndex(int lanes)
    {
        if (lanes == 8 && !System.Runtime.Intrinsics.Vector256.IsHardwareAccelerated) { return; }
        if (lanes == 4 && !System.Runtime.Intrinsics.Vector128.IsHardwareAccelerated) { return; }
        foreach (int length in new[] { 0, 1, 3, 4, 5, 7, 8, 9, 15, 16, 17, 31, 32 })
        {
            var finite = new float[length];
            Assert.Equal(-1, FiniteTensor.Scan(finite, lanes));
            if (length == 0) { continue; }
            finite[length / 2] = float.Epsilon;
            finite[0] = -0f;
            Assert.Equal(-1, FiniteTensor.Scan(finite, lanes));
            foreach (float bad in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
            {
                for (int index = 0; index < length; index++)
                {
                    var values = new float[length];
                    Array.Fill(values, -12.5f);
                    if (index > 0) { values[index - 1] = bad; }
                    values[index] = bad;
                    Assert.Equal(index == 0 ? 0 : index - 1, FiniteTensor.Scan(values, lanes));
                    values[index - (index == 0 ? 0 : 1)] = 1f;
                    values[index] = bad;
                    Assert.Equal(index, FiniteTensor.Scan(values, lanes));
                }
            }
        }
    }
}
