// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.HLE.Diagnostics;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.VideoOut;

namespace SharpEmu.Core.Cpu.Native;

// Read-only synchronization-stall snapshot (Stage 14).
//
// Gated behind SHARPEMU_DIAG_SYNC_SNAPSHOT=1 (master enable) and optionally
// SHARPEMU_DIAG_STALL_SNAPSHOT_MS=<ms> (auto-trigger after that long with no
// frame-present progress). When disabled everything here is inert — the
// watchdog thread is not even started. Nothing in this file changes scheduler,
// semaphore, host-park, or 0x1E behavior; it only reads state already present
// and emits sync_snapshot.* lines consumed by scripts/analyze_waitfor_graph.py.
//
// The value over the offline historical reconstruction: it names the previously
// anonymous host-parked (guest=0) waiter and binds it to its wait object by
// matching monitor-gate identities. Where identity is genuinely unrecoverable
// the emitted token is UNKNOWN — never a fabricated handle.
public sealed unsafe partial class DirectExecutionBackend
{
    private static readonly bool _diagSyncSnapshotEnabled = string.Equals(
        Environment.GetEnvironmentVariable("SHARPEMU_DIAG_SYNC_SNAPSHOT"), "1", StringComparison.Ordinal);

    private static readonly int _diagStallSnapshotMs = ParseDiagStallSnapshotMs();

    private long _syncSnapshotSeq;

    // 1 = armed (may fire), 0 = already fired for the current stall episode. Re-armed
    // when frame-present progress resumes, so one stall yields exactly one snapshot.
    private int _syncStallSnapshotArmed = 1;

    private static int ParseDiagStallSnapshotMs() =>
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DIAG_STALL_SNAPSHOT_MS"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms) && ms > 0
            ? ms
            : 0;

    /// <summary>
    /// Starts the auto-trigger watchdog when SHARPEMU_DIAG_STALL_SNAPSHOT_MS is set
    /// (and the master enable is on). No-op otherwise. Called alongside the fatal
    /// stall watchdog; it never terminates the process and never intervenes.
    /// </summary>
    private void StartSyncStallSnapshotWatchdog()
    {
        if (!_diagSyncSnapshotEnabled || _diagStallSnapshotMs <= 0)
        {
            return;
        }

        var thread = new Thread(SyncStallSnapshotLoop)
        {
            IsBackground = true,
            Name = "sync-stall-snapshot",
        };
        thread.Start();
    }

    private void SyncStallSnapshotLoop()
    {
        // "Progress" = a new guest flip was presented. Deliberately NOT import
        // count: a busy spin keeps dispatching imports but presents no frames.
        int lastFlip = VideoOutExports.DiagnosticFlipCount;
        long lastProgressMs = Environment.TickCount64;
        int pollMs = Math.Clamp(_diagStallSnapshotMs / 4, 50, 1000);

        while (!_stallWatchdogStop)
        {
            Thread.Sleep(pollMs);
            if (_stallWatchdogStop)
            {
                break;
            }

            int flip = VideoOutExports.DiagnosticFlipCount;
            if (flip != lastFlip)
            {
                lastFlip = flip;
                lastProgressMs = Environment.TickCount64;
                // Frames are presenting again: re-arm for the next stall episode.
                Volatile.Write(ref _syncStallSnapshotArmed, 1);
                continue;
            }

            long stalledMs = Environment.TickCount64 - lastProgressMs;
            if (stalledMs < _diagStallSnapshotMs)
            {
                continue;
            }

            // A VM legitimately parked in a blocking wait (cond_wait/WaitEqueue)
            // with a ready thread pending is idle by design, not stalled.
            if (HasReadyGuestThread() || IsExpectedBlockingImportStall(out _, out _))
            {
                continue;
            }

            // Rate-limit: one snapshot per stall episode.
            if (Interlocked.Exchange(ref _syncStallSnapshotArmed, 0) == 1)
            {
                try
                {
                    // Reason must not embed '=' (breaks the key=value parser).
                    EmitSyncSnapshot($"stall-{_diagStallSnapshotMs}ms");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[LOADER][DIAG] sync_snapshot.error {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Emits one read-only synchronization snapshot block to stderr. Safe to call
    /// from any thread; only reads state. No-op unless the master enable is set.
    /// </summary>
    internal void EmitSyncSnapshot(string reason)
    {
        if (!_diagSyncSnapshotEnabled)
        {
            return;
        }

        long seq = Interlocked.Increment(ref _syncSnapshotSeq);

        // Cooperative (scheduler-tracked) guest threads.
        var coop = new List<CoopThreadInput>();
        foreach (var t in SnapshotGuestThreads())
        {
            coop.Add(new CoopThreadInput(
                Handle: t.ThreadHandle,
                Pthread: t.ThreadHandle,
                Name: t.Name,
                State: t.State.ToString(),
                Rip: t.LastReturnRip,
                ResumeRip: t.HasBlockedContinuation ? t.BlockedContinuation.Rip : 0,
                HostTid: t.HostThreadId,
                Worker: (t.ExecutionRunner is not null || t.ContinuationRunner is not null) ? "worker" : "inline",
                BlockWakeKey: t.BlockWakeKey ?? string.Empty,
                BlockReason: t.BlockReason ?? string.Empty,
                Timeout: t.BlockDeadlineTimestamp == 0 ? "infinite" : "finite",
                HasContinuation: t.HasBlockedContinuation,
                WaitMs: 0));
        }

        // Host-parked external-executor waiters (the anonymous guest=0 population).
        var parks = new List<HostParkInput>();
        foreach (var p in GuestThreadExecution.SnapshotHostParks())
        {
            parks.Add(new HostParkInput(
                ThreadHandle: p.ThreadHandle,
                Name: ResolveHostParkName(p.ThreadHandle),
                GateId: p.GateId,
                Rip: 0,
                Timeout: "unknown",
                WaitMs: 0));
        }

        // Sync objects + gate bindings for host-park wait-object recovery.
        var semaEntries = KernelSemaphoreCompatExports.SnapshotSemaphores();
        var eventFlagEntries = KernelEventFlagCompatExports.SnapshotEventFlags();

        var gateBindings = new List<GateBinding>(semaEntries.Count + eventFlagEntries.Count);
        var semas = new List<SyncSnapshotSema>(semaEntries.Count);
        foreach (var s in semaEntries)
        {
            gateBindings.Add(new GateBinding(s.GateId, "sema", s.Handle, s.WakeKey));
            string signaler = s.LastSignalerHandle == 0 ? "UNKNOWN" : "0x" + s.LastSignalerHandle.ToString("X", CultureInfo.InvariantCulture);
            string signalerName = s.LastSignalerHandle == 0 ? "UNKNOWN" : ResolveGuestThreadName(coop, s.LastSignalerHandle);
            semas.Add(new SyncSnapshotSema(s.Handle, s.Name, s.Count, s.Max, s.Waiters, signaler, signalerName));
        }

        var eventFlags = new List<SyncSnapshotEventFlag>(eventFlagEntries.Count);
        foreach (var f in eventFlagEntries)
        {
            gateBindings.Add(new GateBinding(f.GateId, "eventflag", f.Handle, f.WakeKey));
            eventFlags.Add(new SyncSnapshotEventFlag(f.Handle, f.Name, f.Bits, f.Waiters));
        }

        var threads = SyncSnapshotAssembler.AssembleThreads(coop, parks, gateBindings);

        var data = new SyncSnapshotData
        {
            Reason = reason,
            Seq = seq,
            Threads = threads,
            Semaphores = semas,
            EventFlags = eventFlags,
        };

        SyncSnapshotFormatter.Write(Console.Error, data);
    }

    private static string ResolveHostParkName(ulong threadHandle)
    {
        string name = KernelPthreadState.GetThreadName(threadHandle);
        return string.IsNullOrEmpty(name) ? "UNKNOWN" : name;
    }

    private static string ResolveGuestThreadName(List<CoopThreadInput> coop, ulong handle)
    {
        foreach (var c in coop)
        {
            if (c.Handle == handle)
            {
                return c.Name;
            }
        }

        string name = KernelPthreadState.GetThreadName(handle);
        return string.IsNullOrEmpty(name) ? "UNKNOWN" : name;
    }
}
