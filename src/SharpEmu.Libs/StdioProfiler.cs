// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;
using System.Threading;

namespace SharpEmu.Libs;

/// <summary>
/// Temporary, env-gated instrumentation for the libc stdio path. Enabled with
/// <c>SHARPEMU_PROFILE_STDIO=1</c>. All counters are lock-free; a background
/// timer prints a throttled summary so startup I/O cost can be correlated with
/// the first presented frame in the run log. This is diagnostic scaffolding —
/// it must stay quiet unless explicitly enabled.
/// </summary>
internal static class StdioProfiler
{
    internal static readonly bool Enabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_STDIO"), "1", StringComparison.Ordinal);

    private static long _fopen;
    private static long _fclose;
    private static long _fread;
    private static long _freadBytes;
    private static long _freadMin = long.MaxValue;
    private static long _freadMax;
    private static long _fwrite;
    private static long _fwriteBytes;
    private static long _fseek;
    private static long _ftell;
    private static long _rewind;
    private static long _fgetc;
    private static long _feof;

    // Time spent inside host FileStream operations (Read/Seek/Position/...).
    private static long _hostIoTicks;

    // Time spent inside the compat-memory copy that lands stdio bytes in the
    // caller buffer (guest map or libc host heap).
    private static long _copyTicks;

    // How the compat copy resolved: guest-map fast path vs host-memory fallback.
    private static long _copyGuestHits;
    private static long _copyHostFallbacks;

    private static long _startTimestamp;
    private static Timer? _timer;
    private static readonly object _timerGate = new();

    internal static long StartInterval() => Enabled ? Stopwatch.GetTimestamp() : 0;

    internal static void AddHostIo(long startTimestamp)
    {
        if (Enabled)
        {
            Interlocked.Add(ref _hostIoTicks, Stopwatch.GetTimestamp() - startTimestamp);
        }
    }

    internal static void AddCopy(long startTimestamp, bool guestHit)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Add(ref _copyTicks, Stopwatch.GetTimestamp() - startTimestamp);
        if (guestHit)
        {
            Interlocked.Increment(ref _copyGuestHits);
        }
        else
        {
            Interlocked.Increment(ref _copyHostFallbacks);
        }
    }

    internal static void CountFopen()
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _fopen);
        EnsureTimer();
    }

    internal static void CountFclose() => Bump(ref _fclose);

    internal static void CountFread(long bytes)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _fread);
        Interlocked.Add(ref _freadBytes, bytes);
        UpdateMin(ref _freadMin, bytes);
        UpdateMax(ref _freadMax, bytes);
    }

    internal static void CountFwrite(long bytes)
    {
        if (!Enabled)
        {
            return;
        }

        Interlocked.Increment(ref _fwrite);
        Interlocked.Add(ref _fwriteBytes, bytes);
    }

    internal static void CountFseek() => Bump(ref _fseek);
    internal static void CountFtell() => Bump(ref _ftell);
    internal static void CountRewind() => Bump(ref _rewind);
    internal static void CountFgetc() => Bump(ref _fgetc);
    internal static void CountFeof() => Bump(ref _feof);

    private static void Bump(ref long field)
    {
        if (Enabled)
        {
            Interlocked.Increment(ref field);
        }
    }

    private static void UpdateMin(ref long field, long value)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref field);
            if (value >= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref field, value, seen) != seen);
    }

    private static void UpdateMax(ref long field, long value)
    {
        long seen;
        do
        {
            seen = Interlocked.Read(ref field);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref field, value, seen) != seen);
    }

    private static void EnsureTimer()
    {
        if (_timer is not null)
        {
            return;
        }

        lock (_timerGate)
        {
            if (_timer is not null)
            {
                return;
            }

            _startTimestamp = Stopwatch.GetTimestamp();
            _timer = new Timer(_ => Dump(), null, 1000, 1000);
        }
    }

    private static void Dump()
    {
        var elapsedMs = (Stopwatch.GetTimestamp() - Volatile.Read(ref _startTimestamp)) * 1000.0 / Stopwatch.Frequency;
        var hostIoMs = Interlocked.Read(ref _hostIoTicks) * 1000.0 / Stopwatch.Frequency;
        var copyMs = Interlocked.Read(ref _copyTicks) * 1000.0 / Stopwatch.Frequency;
        var reads = Interlocked.Read(ref _fread);
        var bytes = Interlocked.Read(ref _freadBytes);
        var min = Interlocked.Read(ref _freadMin);
        var max = Interlocked.Read(ref _freadMax);
        var avg = reads > 0 ? bytes / reads : 0;

        Console.Error.WriteLine(
            $"[STDIO-PROFILE] t={elapsedMs,8:F1}ms " +
            $"fopen={Interlocked.Read(ref _fopen)} fclose={Interlocked.Read(ref _fclose)} " +
            $"fread={reads} bytes={bytes} avg={avg} min={(min == long.MaxValue ? 0 : min)} max={max} " +
            $"fwrite={Interlocked.Read(ref _fwrite)} " +
            $"fseek={Interlocked.Read(ref _fseek)} ftell={Interlocked.Read(ref _ftell)} rewind={Interlocked.Read(ref _rewind)} " +
            $"fgetc={Interlocked.Read(ref _fgetc)} feof={Interlocked.Read(ref _feof)} " +
            $"hostIo={hostIoMs:F1}ms copy={copyMs:F1}ms " +
            $"copyGuest={Interlocked.Read(ref _copyGuestHits)} copyHost={Interlocked.Read(ref _copyHostFallbacks)}");
    }
}
