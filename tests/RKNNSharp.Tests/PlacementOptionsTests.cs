using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RKNNSharp.Tests;

public sealed class PlacementOptionsTests
{
    [Fact]
    public void OptionsDefaultsPreserveExistingBehavior()
    {
        var options = new RknnModelOptions();
        Assert.Equal(NpuCoreMask.Auto, options.CoreMask);
        Assert.Equal(FloatOutputMode.SdkExact, options.OutputMode);
        Assert.Null(options.CallerThreadPlacement);
        Assert.Null(options.LoggerFactory);
        Assert.Null(options.ExpectedModelSha256);
        Assert.Equal(PlacementFailureMode.WarnOnceAndContinue, new CallerThreadPlacement().OnFailure);
        Assert.NotNull(typeof(RknnModel).GetConstructor([typeof(string), typeof(RknnModelOptions)]));
        Assert.Throws<ArgumentNullException>(() => new RknnModel("missing", (RknnModelOptions)null!));
    }

    [Fact]
    public void ValidationRequiresAtLeastOnePlacementOption()
    {
        Assert.Throws<ArgumentException>(() => new CallerThreadPlacement().Validate(true, "0-7"));
        Assert.Throws<ArgumentException>(() => new CallerThreadPlacement { CpuMask = 0 }.Validate(true, "0-7"));
        Assert.Throws<ArgumentException>(() => new CallerThreadPlacement { CpuMask = 0x10 }.Validate(true, "0-3,5-7"));
        new CallerThreadPlacement { CpuMask = 0xa5 }.Validate(true, "0,2,5,7");
        new CallerThreadPlacement { UtilClampMin = 0 }.Validate(true, null);
        new CallerThreadPlacement { UtilClampMin = 1024 }.Validate(true, null);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CallerThreadPlacement { OnFailure = (PlacementFailureMode)2, CpuMask = 1 }.Validate(true, "0-7"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1025)]
    public void InvalidUtilClampIsRejected(int value) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CallerThreadPlacement { UtilClampMin = value }.Validate(true, null));

    [Fact]
    public void NonLinuxPlacementIsRejectedBeforeNativeOrFileAccess()
    {
        Assert.Throws<PlatformNotSupportedException>(() => new CallerThreadPlacement { CpuMask = 1 }.Validate(false, null));
        if (!OperatingSystem.IsLinux())
        {
            Assert.Throws<PlatformNotSupportedException>(() => new RknnModel("missing", new RknnModelOptions { CallerThreadPlacement = new CallerThreadPlacement { CpuMask = 1 } }));
            Assert.Throws<PlatformNotSupportedException>(CallerThreadPlacement.HighestCapacityCores);
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1024)]
    public void CpuIndicesOutsideCpuSetAreRejected(int cpu) => Assert.Throws<ArgumentOutOfRangeException>(() => CallerThreadPlacement.FromCpus(cpu));

    [Theory]
    [InlineData(64)]
    [InlineData(1023)]
    public void UnrepresentableIndicesAreNeverShiftTruncated(int cpu) => Assert.Throws<NotSupportedException>(() => CallerThreadPlacement.FromCpus(cpu));

    [Fact]
    public void CpuMaskConstructionIncludesBit63AndRejectsEmptyInput()
    {
        Assert.Equal(0x8000000000000001UL, CallerThreadPlacement.FromCpus(0, 63, 0).CpuMask);
        Assert.Throws<ArgumentException>(() => CallerThreadPlacement.FromCpus());
        Assert.Throws<ArgumentNullException>(() => CallerThreadPlacement.FromCpus(null!));
    }

    [Fact]
    public void CapacityReaderSelectsFourBigCoresAndExcludesOfflineCpus()
    {
        string[] directories = Enumerable.Range(0, 8).Select(i => $"/sys/devices/system/cpu/cpu{i}").Append("/sys/devices/system/cpu/cpufreq").ToArray();
        string Read(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.EndsWith("online", StringComparison.Ordinal) ? "0-7\n" :
                normalized.Contains("cpu0/", StringComparison.Ordinal) || normalized.Contains("cpu1/", StringComparison.Ordinal) ||
                normalized.Contains("cpu2/", StringComparison.Ordinal) || normalized.Contains("cpu3/", StringComparison.Ordinal) ? "414\n" : "1024\n";
        }
        Assert.Equal(0xf0UL, CallerThreadPlacement.HighestCapacityCores(Read, directories).CpuMask);
        string Offline(string path) => path.EndsWith("online", StringComparison.Ordinal) ? "0-3,5-7" : Read(path);
        Assert.Equal(0xe0UL, CallerThreadPlacement.HighestCapacityCores(Offline, directories).CpuMask);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisabledSourceGeneratedLogsAllocateNothingAndNeverCallLog(bool customLogger)
    {
        var disabled = new PlacementTests.CountingLogger(false);
        ILogger logger = customLogger ? disabled : NullLogger.Instance;
        for (int i = 0; i < 1000; i++) { WriteAllLogs(logger); }
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100000; i++) { WriteAllLogs(logger); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Equal(0, disabled.Calls);
    }

    private static void WriteAllLogs(ILogger logger)
    {
        Log.SessionCreated(logger, NpuCoreMask.Auto, FloatOutputMode.SdkExact);
        Log.PlacementEnabled(logger, 0xf0, 600);
        Log.PlacementFailed(logger, "sched_setattr", 1);
        Log.PlacementRestoreFailed(logger, "sched_setaffinity", 22);
        Log.DirectFloat(logger, true);
    }
}
