using System.Globalization;

namespace RKNNSharp;

public enum PlacementFailureMode
{
    WarnOnceAndContinue = 0,
    Throw = 1
}

/// <summary>仅在同步推理调用期间改变调用线程的调度, 结束时恢复.</summary>
public sealed class CallerThreadPlacement
{
    /// <summary>CPU0..63 的亲和性位图. 更高编号不能用此公开类型表示.</summary>
    public ulong? CpuMask { get; init; }
    public int? UtilClampMin { get; init; }
    public PlacementFailureMode OnFailure { get; init; } = PlacementFailureMode.WarnOnceAndContinue;

    public static CallerThreadPlacement FromCpus(params int[] cpus)
    {
        ArgumentNullException.ThrowIfNull(cpus);
        foreach (int cpu in cpus)
        {
            if (cpu is < 0 or > 1023) { throw new ArgumentOutOfRangeException(nameof(cpus), "CPU must be in 0..1023."); }
        }
        ulong mask = 0;
        foreach (int cpu in cpus)
        {
            if (cpu >= 64) { throw new NotSupportedException("The ulong CpuMask can represent only CPU0..63."); }
            mask |= 1UL << cpu;
        }
        if (mask == 0) { throw new ArgumentException("At least one CPU is required.", nameof(cpus)); }
        return new CallerThreadPlacement { CpuMask = mask };
    }

    /// <summary>读取 Linux sysfs, 选择在线且 cpu_capacity 最大的 CPU. 缺少容量信息时显式失败.</summary>
    public static CallerThreadPlacement HighestCapacityCores()
    {
        if (!OperatingSystem.IsLinux()) { throw new PlatformNotSupportedException("CPU capacity discovery requires Linux sysfs."); }
        return HighestCapacityCores(File.ReadAllText, Directory.EnumerateDirectories("/sys/devices/system/cpu", "cpu*"));
    }

    internal static CallerThreadPlacement HighestCapacityCores(Func<string, string> readFile, IEnumerable<string> directories)
    {
        var online = ParseOnlineCpus(readFile("/sys/devices/system/cpu/online"));
        int maximum = -1;
        var selected = new List<int>();
        foreach (string directory in directories)
        {
            string name = Path.GetFileName(directory);
            if (!name.StartsWith("cpu", StringComparison.Ordinal) ||
                !int.TryParse(name.AsSpan(3), NumberStyles.None, CultureInfo.InvariantCulture, out int cpu) || !online.Contains(cpu)) { continue; }
            int capacity = int.Parse(readFile(Path.Combine(directory, "cpu_capacity")).Trim(), CultureInfo.InvariantCulture);
            if (capacity <= 0) { throw new InvalidDataException("CPU capacity must be positive."); }
            if (capacity > maximum) { maximum = capacity; selected.Clear(); }
            if (capacity == maximum) { selected.Add(cpu); }
        }
        if (selected.Count == 0) { throw new InvalidDataException("No online CPU capacity information was found."); }
        return FromCpus(selected.ToArray());
    }

    internal void Validate(bool isLinux, string? onlineCpus)
    {
        if (!isLinux) { throw new PlatformNotSupportedException("Caller thread placement requires Linux."); }
        if (CpuMask is null && UtilClampMin is null) { throw new ArgumentException("CpuMask or UtilClampMin is required."); }
        if (CpuMask == 0) { throw new ArgumentException("CpuMask must not be zero.", nameof(CpuMask)); }
        if (UtilClampMin is < 0 or > 1024) { throw new ArgumentOutOfRangeException(nameof(UtilClampMin)); }
        if (!Enum.IsDefined(OnFailure)) { throw new ArgumentOutOfRangeException(nameof(OnFailure)); }
        if (CpuMask is ulong mask)
        {
            var online = ParseOnlineCpus(onlineCpus ?? throw new ArgumentNullException(nameof(onlineCpus)));
            for (int cpu = 0; cpu < 64; cpu++)
            {
                if ((mask & (1UL << cpu)) != 0 && !online.Contains(cpu)) { throw new ArgumentException("CpuMask contains an offline CPU.", nameof(CpuMask)); }
            }
        }
    }

    private static HashSet<int> ParseOnlineCpus(string value)
    {
        var result = new HashSet<int>();
        foreach (string part in value.Trim().Split(','))
        {
            string[] range = part.Split('-');
            if (range.Length is < 1 or > 2 || !int.TryParse(range[0], out int first) || first is < 0 or > 1023 ||
                !int.TryParse(range[^1], out int last) || last < first || last > 1023) { throw new InvalidDataException("Invalid Linux online CPU list."); }
            for (int cpu = first; cpu <= last; cpu++) { result.Add(cpu); }
        }
        return result;
    }
}
