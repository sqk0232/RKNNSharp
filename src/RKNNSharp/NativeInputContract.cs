using System.Globalization;

namespace RKNNSharp;

// 与原生 RGA 输入路径保持一致, 供统一入口和两个检测 profile 共用.
internal static class NativeInputContract
{
    internal const int Nhwc = 1;
    internal const int Int8 = 2;
    internal const int Affine = 2;

    internal static TensorInfo Validate(IReadOnlyList<TensorInfo> inputs, int? width = null, int? height = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count != 1)
        { throw new ArgumentException($"Native input requires one tensor. Actual input count={inputs.Count}.", nameof(inputs)); }
        TensorInfo input = inputs[0];
        var dims = input.Dimensions;
        if (input.Index != 0 || dims.Count != 4 || dims[0] != 1 || dims[3] != 3 ||
            dims[1] is < 2 or > 4096 || dims[2] is < 2 or > 4096 ||
            input.Layout != Nhwc || input.DataType != Int8 || input.Quantization != Affine ||
            input.ZeroPoint != -128 || input.Scale != 1f / 255 ||
            input.ElementCount != (ulong)dims[1] * dims[2] * 3)
        {
            throw new ArgumentException("Native input requires static batch=1, NHWC RGB, affine INT8, " +
                "zeroPoint=-128, exact float scale=1/255, dimensions in 2..4096 and matching element count. " +
                $"Actual model input {Describe(input)}. A concrete supported shape is required for execution.", nameof(inputs));
        }
        if ((width.HasValue && width.Value != dims[2]) || (height.HasValue && height.Value != dims[1]))
        {
            throw new ArgumentException($"Configured input dimensions (配置值)={width}x{height}; " +
                $"model input dimensions (模型读出值)={dims[2]}x{dims[1]}. Actual model input {Describe(input)}.", nameof(inputs));
        }
        return input;
    }

    internal static string Describe(TensorInfo input) =>
        $"name='{input.Name}', index={input.Index}, dimensions=[{string.Join(",", input.Dimensions)}], " +
        $"layout={input.Layout}, dataType={input.DataType}, quantization={input.Quantization}, " +
        $"zeroPoint={input.ZeroPoint}, scale={input.Scale.ToString("R", CultureInfo.InvariantCulture)}, elements={input.ElementCount}";
}
