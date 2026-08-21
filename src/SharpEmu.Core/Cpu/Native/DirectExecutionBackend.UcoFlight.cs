// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    // ------------------------------------------------------------------
    // UCO flight recorder (SHARPEMU_UCO_FLIGHT=1)
    //
    // The "attempted to call a UnmanagedCallersOnly method from managed code"
    // FailFast is emitted via __fastfail (int 29h): it bypasses the process VEH
    // and every managed teardown hook, so nothing can dump state ON the crash.
    // Instead each guest-execution boundary writes a cheap, lock-free record into
    // a per-managed-thread ring; a low-rate background thread periodically flushes
    // all rings to stderr. The last [UCOFR] block printed before the CLR's
    // "Fatal error." line is the state <=flush-interval before the crash — enough
    // to identify which guest thread was executing inline and what host<->guest
    // boundary it was crossing. Entirely inert unless the env var is set.
    // ------------------------------------------------------------------
    internal static readonly bool UcoFlightEnabled =
        string.Equals(Environment.GetEnvironmentVariable("SHARPEMU_UCO_FLIGHT"), "1", StringComparison.Ordinal);

    private static readonly ConcurrentDictionary<int, UcoFlightThread> UcoFlightThreads = new();
    private static int _ucoFlightStarted;
    private static readonly Stopwatch UcoFlightClock = Stopwatch.StartNew();

    internal sealed class UcoFlightThread
    {
        internal const int RingSize = 12;
        // event kinds
        internal const int GuestEnter = 1;
        internal const int GuestExit = 2;
        internal const int ImportEnter = 3;
        internal const int CallbackEnter = 4;
        internal const int CallbackExit = 5;

        internal volatile string? GuestName;
        internal volatile int NestDepth;
        internal long Seq;

        // Ring of the most recent boundary events (single writer = this thread).
        internal readonly int[] Kind = new int[RingSize];
        internal readonly long[] Tick = new long[RingSize];
        internal readonly string?[] Label = new string?[RingSize];
        internal readonly ulong[] GuestRip = new ulong[RingSize];
        internal int Head;
    }

    [ThreadStatic]
    private static UcoFlightThread? _ucoThreadLocal;

    private static UcoFlightThread UcoCurrent()
    {
        var local = _ucoThreadLocal;
        if (local is not null)
        {
            return local;
        }

        local = UcoFlightThreads.GetOrAdd(Environment.CurrentManagedThreadId, static _ => new UcoFlightThread());
        _ucoThreadLocal = local;
        return local;
    }

    private static void UcoRecord(UcoFlightThread t, int kind, string? label, ulong guestRip)
    {
        var i = t.Head;
        t.Kind[i] = kind;
        t.Tick[i] = UcoFlightClock.ElapsedMilliseconds;
        t.Label[i] = label;
        t.GuestRip[i] = guestRip;
        t.Head = (i + 1) % UcoFlightThread.RingSize;
        t.Seq++;
    }

    internal static void UcoGuestEnter(string name, int nestDepth)
    {
        if (!UcoFlightEnabled)
        {
            return;
        }

        var t = UcoCurrent();
        t.GuestName = name;
        t.NestDepth = nestDepth;
        UcoRecord(t, UcoFlightThread.GuestEnter, name, 0);
        UcoEnsureFlightThread();
    }

    internal static void UcoGuestExit(string name)
    {
        if (!UcoFlightEnabled)
        {
            return;
        }

        UcoRecord(UcoCurrent(), UcoFlightThread.GuestExit, name, 0);
    }

    internal static void UcoImport(string nid, string? export, ulong guestRip)
    {
        if (!UcoFlightEnabled)
        {
            return;
        }

        UcoRecord(UcoCurrent(), UcoFlightThread.ImportEnter, export ?? nid, guestRip);
    }

    private static void UcoEnsureFlightThread()
    {
        if (Interlocked.Exchange(ref _ucoFlightStarted, 1) != 0)
        {
            return;
        }

        var flight = new Thread(UcoFlightLoop)
        {
            IsBackground = true,
            Name = "SharpEmu-uco-flight",
        };
        flight.Start();
    }

    private static void UcoFlightLoop()
    {
        var sb = new StringBuilder(4096);
        while (true)
        {
            Thread.Sleep(50);
            sb.Clear();
            sb.Append("[UCOFR] flush t=").Append(UcoFlightClock.ElapsedMilliseconds).Append('\n');
            foreach (var kv in UcoFlightThreads)
            {
                var t = kv.Value;
                var name = t.GuestName;
                if (name is null)
                {
                    continue;
                }

                sb.Append("  mtid=").Append(kv.Key)
                  .Append(" guest='").Append(name).Append('\'')
                  .Append(" nest=").Append(t.NestDepth)
                  .Append(" seq=").Append(Interlocked.Read(ref t.Seq))
                  .Append(" ring=[");
                // Print the ring oldest->newest ending at Head-1.
                var head = t.Head;
                for (var k = 0; k < UcoFlightThread.RingSize; k++)
                {
                    var idx = (head + k) % UcoFlightThread.RingSize;
                    if (t.Label[idx] is null)
                    {
                        continue;
                    }

                    sb.Append(UcoKindTag(t.Kind[idx]))
                      .Append(t.Label[idx]);
                    if (t.GuestRip[idx] != 0)
                    {
                        sb.Append("@0x").Append(t.GuestRip[idx].ToString("X"));
                    }

                    sb.Append('(').Append(t.Tick[idx]).Append(") ");
                }

                sb.Append("]\n");
            }

            Console.Error.Write(sb.ToString());
            Console.Error.Flush();
        }
    }

    private static string UcoKindTag(int kind) => kind switch
    {
        UcoFlightThread.GuestEnter => "G+",
        UcoFlightThread.GuestExit => "G-",
        UcoFlightThread.ImportEnter => "I:",
        UcoFlightThread.CallbackEnter => "C+",
        UcoFlightThread.CallbackExit => "C-",
        _ => "?",
    };
}
