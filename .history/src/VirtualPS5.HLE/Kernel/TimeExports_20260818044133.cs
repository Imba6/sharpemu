using System.Diagnostics;
using SharpEmu.HLE;

namespace VirtualPS5.HLE.Kernel;

public static class TimeExports
{
    private static readonly long ProcessStartTimestamp =
        Stopwatch.GetTimestamp();

    [SysAbiExport(
        Nid = "4J2sUJmuHZQ",
        ExportName = "sceKernelGetProcessTime",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTime(CpuContext ctx)
    {
        var elapsedTicks =
            Stopwatch.GetTimestamp() - ProcessStartTimestamp;

        var microseconds =
            elapsedTicks * 1_000_000L / Stopwatch.Frequency;

        ctx[CpuRegister.Rax] =
            unchecked((ulong)Math.Max(0, microseconds));

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "fgxnMeTNUtY",
        ExportName = "sceKernelGetProcessTimeCounter",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTimeCounter(CpuContext ctx)
    {
        var elapsedTicks =
            Stopwatch.GetTimestamp() - ProcessStartTimestamp;

        ctx[CpuRegister.Rax] =
            unchecked((ulong)Math.Max(0, elapsedTicks));

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BNowx2l588E",
        ExportName = "sceKernelGetProcessTimeCounterFrequency",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTimeCounterFrequency(CpuContext ctx)
    {
        ctx[CpuRegister.Rax] =
            unchecked((ulong)Stopwatch.Frequency);

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}