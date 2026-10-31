using Microsoft.Extensions.Logging;

namespace RKNNSharp;

internal static partial class Log
{
    [LoggerMessage(1001, LogLevel.Information, "RKNN session created with core mask {CoreMask} and output mode {OutputMode}.")]
    internal static partial void SessionCreated(ILogger logger, NpuCoreMask coreMask, FloatOutputMode outputMode);

    [LoggerMessage(1101, LogLevel.Information, "Caller thread placement enabled: CPU mask {CpuMask}, utilization minimum {UtilClampMin}.")]
    internal static partial void PlacementEnabled(ILogger logger, ulong? cpuMask, int? utilClampMin);

    [LoggerMessage(1102, LogLevel.Warning, "Caller thread placement failed during {Operation}, errno {Errno}; placement disabled for this model.")]
    internal static partial void PlacementFailed(ILogger logger, string operation, int errno);

    [LoggerMessage(1103, LogLevel.Warning, "Caller thread placement restore failed during {Operation}, errno {Errno}.")]
    internal static partial void PlacementRestoreFailed(ILogger logger, string operation, int errno);

    [LoggerMessage(1201, LogLevel.Debug, "Direct float output support: {Enabled}.")]
    internal static partial void DirectFloat(ILogger logger, bool enabled);
}
