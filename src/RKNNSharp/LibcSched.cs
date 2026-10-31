using System.Runtime.InteropServices;

namespace RKNNSharp;

internal static unsafe partial class LibcSched
{
    internal const ulong SchedFlagUtilClampMin = 0x20;
    internal const ulong SchedFlagKeepAll = 0x18;
    internal unsafe delegate int AffinityCall(int pid, nuint size, ulong* mask);
    internal unsafe delegate int AttributeCall(int pid, SchedAttr* attributes);

    [LibraryImport("libc", SetLastError = true)]
    internal static partial int sched_getaffinity(int pid, nuint size, ulong* mask);
    [LibraryImport("libc", SetLastError = true)]
    internal static partial int sched_setaffinity(int pid, nuint size, ulong* mask);
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint GetAttrSyscall(nint number, int pid, SchedAttr* attributes, uint size, uint flags);
    [LibraryImport("libc", EntryPoint = "syscall", SetLastError = true)]
    private static partial nint SetAttrSyscall(nint number, int pid, SchedAttr* attributes, uint flags);

    internal static int GetAttr(SchedAttr* attributes) =>
        (int)GetAttrSyscall(AttrSyscallNumber(false), 0, attributes, (uint)sizeof(SchedAttr), 0);
    internal static int SetAttr(SchedAttr* attributes) =>
        (int)SetAttrSyscall(AttrSyscallNumber(true), 0, attributes, 0);
    private static nint AttrSyscallNumber(bool set) => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => set ? 274 : 275,
        Architecture.X64 => set ? 314 : 315,
        _ => throw new PlatformNotSupportedException("sched_attr syscall numbers are supported only on Linux ARM64/x64.")
    };
}

[StructLayout(LayoutKind.Sequential)]
internal struct SchedAttr
{
    internal uint Size, Policy;
    internal ulong Flags;
    internal int Nice;
    internal uint Priority;
    internal ulong Runtime, Deadline, Period;
    internal uint UtilMin, UtilMax;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CpuSet
{
    internal fixed ulong Words[16];
}
