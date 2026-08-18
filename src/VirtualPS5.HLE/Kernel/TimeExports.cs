using System.Diagnostics;
using System.Threading;
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

        Console.Error.WriteLine(
            $"[VPS5][KERNEL] sceKernelGetProcessTime -> {microseconds}");

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

        Console.Error.WriteLine(
            $"[VPS5][KERNEL] sceKernelGetProcessTimeCounter -> {elapsedTicks}");

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "BNowx2l588E",
        ExportName = "sceKernelGetProcessTimeCounterFrequency",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGetProcessTimeCounterFrequency(CpuContext ctx)
    {
        var frequency = Stopwatch.Frequency;

        ctx[CpuRegister.Rax] =
            unchecked((ulong)frequency);

        Console.Error.WriteLine(
            $"[VPS5][KERNEL] sceKernelGetProcessTimeCounterFrequency -> {frequency}");

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "QBi7HCK03hw",
        ExportName = "sceKernelClockGettime",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelClockGettime(CpuContext ctx)
    {
        var clockId = unchecked((int)ctx[CpuRegister.Rdi]);
        var timespecAddress = ctx[CpuRegister.Rsi];

        if (timespecAddress == 0)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        long seconds;
        long nanoseconds;

        if (clockId == 0)
        {
            // CLOCK_REALTIME
            var now = DateTimeOffset.UtcNow;

            seconds = now.ToUnixTimeSeconds();
            nanoseconds =
                (now.Ticks % TimeSpan.TicksPerSecond) * 100;
        }
        else
        {
            // Monotonic-style clock.
            var elapsedTicks =
                Stopwatch.GetTimestamp() - ProcessStartTimestamp;

            seconds =
                elapsedTicks / Stopwatch.Frequency;

            nanoseconds =
                (elapsedTicks % Stopwatch.Frequency) *
                1_000_000_000L /
                Stopwatch.Frequency;
        }

        if (!ctx.TryWriteUInt64(
                timespecAddress,
                unchecked((ulong)seconds)) ||
            !ctx.TryWriteUInt64(
                timespecAddress + 8,
                unchecked((ulong)nanoseconds)))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][KERNEL] sceKernelClockGettime clock={clockId} " +
            $"-> {seconds}s {nanoseconds}ns");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "ejekcaNQNq0",
        ExportName = "sceKernelGettimeofday",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelGettimeofday(CpuContext ctx)
    {
        var timevalAddress = ctx[CpuRegister.Rdi];

        if (timevalAddress == 0)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var now = DateTimeOffset.UtcNow;

        var seconds =
            now.ToUnixTimeSeconds();

        var microseconds =
            (now.Ticks % TimeSpan.TicksPerSecond) / 10;

        if (!ctx.TryWriteUInt64(
                timevalAddress,
                unchecked((ulong)seconds)) ||
            !ctx.TryWriteUInt64(
                timevalAddress + 8,
                unchecked((ulong)microseconds)))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][KERNEL] sceKernelGettimeofday " +
            $"-> {seconds}s {microseconds}us");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        Nid = "1jfXLRVzisc",
        ExportName = "sceKernelUsleep",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelUsleep(CpuContext ctx)
    {
        var microseconds =
            ctx[CpuRegister.Rdi];

        Console.Error.WriteLine(
            $"[VPS5][KERNEL] sceKernelUsleep({microseconds})");

        SleepMicroseconds(microseconds);

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    private static void SleepMicroseconds(ulong microseconds)
    {
        if (microseconds == 0)
        {
            return;
        }

        var start = Stopwatch.GetTimestamp();

        var targetTicks =
            microseconds *
            (double)Stopwatch.Frequency /
            1_000_000.0;

        /*
         * For longer waits, give most of the time back to the host
         * scheduler and spin only near the end.
         */
        if (microseconds >= 2_000)
        {
            var sleepMilliseconds =
                (long)(microseconds / 1_000) - 1;

            if (sleepMilliseconds > 0)
            {
                Thread.Sleep(
                    TimeSpan.FromMilliseconds(
                        sleepMilliseconds));
            }
        }

        while ((Stopwatch.GetTimestamp() - start) < targetTicks)
        {
            Thread.SpinWait(64);
        }
    }
}