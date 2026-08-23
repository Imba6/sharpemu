// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SharpEmu.HLE.Diagnostics;

// Pure, allocation-light formatter for a read-only synchronization-stall
// snapshot. The runtime collects live state into these records and emits them
// as "sync_snapshot.*" lines; the generic offline tool
// scripts/analyze_waitfor_graph.py parses the exact grammar produced here to
// build an authoritative wait-for graph (including host-parked waiters whose
// identity the historical-log reconstruction cannot recover).
//
// This type contains NO title-specific knowledge and performs NO synchronization
// or scheduling; it only formats records handed to it. The line grammar is a
// stable contract shared with the Python parser — keep the two in lockstep.
//
// Where an identity genuinely cannot be recovered the collector passes the
// literal token "UNKNOWN"; this formatter never invents a handle.

/// <summary>One guest logical thread at snapshot time.</summary>
public readonly record struct SyncSnapshotThread(
    ulong Handle,
    ulong Pthread,
    string Name,
    string State,
    ulong Rip,
    ulong ResumeRip,
    int HostTid,
    string Worker,       // "inline" | "worker:N" | "none"
    string Wait,         // "sema" | "mutex" | "equeue" | "eventflag" | "cond" | "none"
    ulong Obj,           // wait-object id/handle/address, 0 if none
    string Key,          // wake key (may be empty)
    int Need,
    string Timeout,      // "infinite" | "none" | microseconds
    bool Parked,
    bool Reentrant,
    long WaitMs);

/// <summary>One semaphore at snapshot time. LastSignaler may be "UNKNOWN".</summary>
public readonly record struct SyncSnapshotSema(
    ulong Handle,
    string Name,
    long Count,
    long Max,
    int Waiters,
    string LastSignaler,
    string LastSignalerName);

/// <summary>One pthread mutex at snapshot time. Owner may be "UNKNOWN"/0.</summary>
public readonly record struct SyncSnapshotMutex(
    ulong Obj,
    string Name,
    string Owner,
    string OwnerName,
    int Recursion,
    int Waiters,
    bool Abandoned);

/// <summary>One event queue at snapshot time.</summary>
public readonly record struct SyncSnapshotEqueue(
    ulong Handle,
    string Name,
    int Events,
    int Waiters);

/// <summary>One event flag at snapshot time.</summary>
public readonly record struct SyncSnapshotEventFlag(
    ulong Handle,
    string Name,
    ulong Bits,
    int Waiters);

// --------------------------------------------------------------------------- #
// Raw registry entries returned by the per-primitive read-only enumerators.
// These carry the monitor-gate identity (GateId) so the collector can bind a
// host-parked waiter to its wait object without any reverse index.
// --------------------------------------------------------------------------- #

/// <summary>Raw semaphore registry entry (LastSignaler is diagnostic-only).</summary>
public readonly record struct SemaphoreSnapshotEntry(
    ulong Handle,
    string Name,
    int Count,
    int Max,
    int Waiters,
    string WakeKey,
    long GateId,
    ulong LastSignalerHandle);

/// <summary>Raw event-flag registry entry.</summary>
public readonly record struct EventFlagSnapshotEntry(
    ulong Handle,
    string Name,
    ulong Bits,
    int Waiters,
    string WakeKey,
    long GateId);

/// <summary>All state gathered for one snapshot.</summary>
public sealed class SyncSnapshotData
{
    public string Reason { get; init; } = "manual";
    public long Seq { get; init; }
    public IReadOnlyList<SyncSnapshotThread> Threads { get; init; } = System.Array.Empty<SyncSnapshotThread>();
    public IReadOnlyList<SyncSnapshotSema> Semaphores { get; init; } = System.Array.Empty<SyncSnapshotSema>();
    public IReadOnlyList<SyncSnapshotMutex> Mutexes { get; init; } = System.Array.Empty<SyncSnapshotMutex>();
    public IReadOnlyList<SyncSnapshotEqueue> Equeues { get; init; } = System.Array.Empty<SyncSnapshotEqueue>();
    public IReadOnlyList<SyncSnapshotEventFlag> EventFlags { get; init; } = System.Array.Empty<SyncSnapshotEventFlag>();
}

/// <summary>Formats <see cref="SyncSnapshotData"/> into the sync_snapshot.* line grammar.</summary>
public static class SyncSnapshotFormatter
{
    private const string Prefix = "[LOADER][DIAG] sync_snapshot.";

    private static string Hex(ulong v) => "0x" + v.ToString("X", CultureInfo.InvariantCulture);

    // Names are always emitted inside single quotes (the offline parser reads
    // '[^']*'), so spaces are fine; only an embedded single quote would break
    // the scan, so strip those. Empty names become "?" to keep the field present.
    private static string Sanitize(string? s) =>
        string.IsNullOrEmpty(s) ? "?" : s.Replace('\'', '_');

    public static string FormatBegin(string reason, long seq) =>
        $"{Prefix}begin reason={reason} seq={seq.ToString(CultureInfo.InvariantCulture)}";

    public static string FormatEnd(long seq, int threads, int blocked) =>
        $"{Prefix}end seq={seq.ToString(CultureInfo.InvariantCulture)} threads={threads} blocked={blocked}";

    public static string FormatThread(in SyncSnapshotThread t) =>
        $"{Prefix}thread handle={Hex(t.Handle)} pthread={Hex(t.Pthread)} name='{Sanitize(t.Name)}' " +
        $"state={t.State} rip={Hex(t.Rip)} resume_rip={Hex(t.ResumeRip)} host_tid={t.HostTid} " +
        $"worker={t.Worker} wait={t.Wait} obj={Hex(t.Obj)} key='{t.Key}' need={t.Need} " +
        $"timeout={t.Timeout} parked={(t.Parked ? 1 : 0)} reentrant={(t.Reentrant ? 1 : 0)} wait_ms={t.WaitMs}";

    public static string FormatSema(in SyncSnapshotSema s) =>
        $"{Prefix}sema handle={Hex(s.Handle)} name='{Sanitize(s.Name)}' count={s.Count} max={s.Max} " +
        $"waiters={s.Waiters} last_signaler={s.LastSignaler} last_signaler_name='{Sanitize(s.LastSignalerName)}'";

    public static string FormatMutex(in SyncSnapshotMutex m) =>
        $"{Prefix}mutex obj={Hex(m.Obj)} name='{Sanitize(m.Name)}' owner={m.Owner} " +
        $"owner_name='{Sanitize(m.OwnerName)}' recursion={m.Recursion} waiters={m.Waiters} " +
        $"abandoned={(m.Abandoned ? 1 : 0)}";

    public static string FormatEqueue(in SyncSnapshotEqueue e) =>
        $"{Prefix}equeue handle={Hex(e.Handle)} name='{Sanitize(e.Name)}' events={e.Events} waiters={e.Waiters}";

    public static string FormatEventFlag(in SyncSnapshotEventFlag f) =>
        $"{Prefix}eventflag handle={Hex(f.Handle)} name='{Sanitize(f.Name)}' bits={Hex(f.Bits)} waiters={f.Waiters}";

    /// <summary>Writes a complete, parser-consumable snapshot block.</summary>
    public static void Write(TextWriter writer, SyncSnapshotData data)
    {
        writer.WriteLine(FormatBegin(data.Reason, data.Seq));
        int blocked = 0;
        foreach (var t in data.Threads)
        {
            writer.WriteLine(FormatThread(t));
            if (t.Wait is not ("none" or "" or null))
            {
                blocked++;
            }
        }
        foreach (var s in data.Semaphores)
        {
            writer.WriteLine(FormatSema(s));
        }
        foreach (var m in data.Mutexes)
        {
            writer.WriteLine(FormatMutex(m));
        }
        foreach (var e in data.Equeues)
        {
            writer.WriteLine(FormatEqueue(e));
        }
        foreach (var f in data.EventFlags)
        {
            writer.WriteLine(FormatEventFlag(f));
        }
        writer.WriteLine(FormatEnd(data.Seq, data.Threads.Count, blocked));
    }
}
