// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Globalization;

namespace SharpEmu.HLE.Diagnostics;

// Pure assembly of a synchronization snapshot from raw, registry-agnostic inputs.
// Kept separate from the live collector so the merge/identity logic — the tricky
// part that resolves a host-parked waiter's wait object by matching monitor-gate
// identities — is fully unit-testable without any runtime state.
//
// Two waiter populations are merged:
//   * cooperative guest threads (in the scheduler's thread table): identity and
//     wait object are both known; the wait object is derived from the thread's
//     BlockWakeKey.
//   * host-parked external-executor waiters (NOT in the thread table; this is the
//     historically anonymous guest=0 case): identity is only the synthetic
//     per-host-thread handle, and the wait object is recovered by matching the
//     park's monitor-gate id against each sync object's gate id. When no match is
//     found the wait object is reported as unknown (0) — never fabricated.

/// <summary>A cooperative (scheduler-tracked) guest thread, raw inputs.</summary>
public readonly record struct CoopThreadInput(
    ulong Handle,
    ulong Pthread,
    string Name,
    string State,          // "Blocked" | "Ready" | "Running" | ...
    ulong Rip,
    ulong ResumeRip,
    int HostTid,
    string Worker,
    string BlockWakeKey,   // e.g. "sceKernelWaitSema:00000086"; empty if not blocked
    string BlockReason,
    string Timeout,
    bool HasContinuation,
    long WaitMs);

/// <summary>A host-parked waiter, raw inputs (the anonymous guest=0 population).</summary>
public readonly record struct HostParkInput(
    ulong ThreadHandle,
    string Name,           // synthetic "Thread-{id}" or resolved name; never fabricated as a guest name
    long GateId,           // RuntimeHelpers.GetHashCode of the monitor gate it parked on
    ulong Rip,
    string Timeout,
    long WaitMs);

/// <summary>A sync object's gate identity, for host-park wait-object resolution.</summary>
public readonly record struct GateBinding(
    long GateId,
    string WaitType,       // "sema" | "eventflag" | ...
    ulong Obj,
    string Key);

public static class SyncSnapshotAssembler
{
    /// <summary>
    /// Parse a scheduler BlockWakeKey into (waitType, objHandle). Returns false
    /// when the key is empty/unrecognised. Recognised generic forms:
    ///   sceKernelWaitSema:{handle:X8}
    ///   sceKernelWaitEqueue:{handle:X16}:{gen:X16}
    ///   event_flag:0x{handle:X16}
    /// </summary>
    public static bool TryParseWakeKey(string? key, out string waitType, out ulong obj)
    {
        waitType = "none";
        obj = 0;
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }
        int colon = key.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        string prefix = key.Substring(0, colon);
        string rest = key.Substring(colon + 1);
        // A second ':' (equeue) delimits the generation; the handle is the first.
        int second = rest.IndexOf(':');
        string handlePart = second >= 0 ? rest.Substring(0, second) : rest;
        if (handlePart.StartsWith("0x", System.StringComparison.OrdinalIgnoreCase))
        {
            handlePart = handlePart.Substring(2);
        }
        if (!ulong.TryParse(handlePart, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out obj))
        {
            return false;
        }
        waitType = prefix switch
        {
            "sceKernelWaitSema" => "sema",
            "sceKernelWaitEqueue" => "equeue",
            "event_flag" => "eventflag",
            "pthread_mutex" => "mutex",
            _ => "cond",
        };
        return true;
    }

    private static bool IsBlocked(string state) =>
        string.Equals(state, "Blocked", System.StringComparison.OrdinalIgnoreCase);

    /// <summary>Build the thread record list (cooperative + host-parked) for a snapshot.</summary>
    public static IReadOnlyList<SyncSnapshotThread> AssembleThreads(
        IEnumerable<CoopThreadInput> coop,
        IEnumerable<HostParkInput> hostParks,
        IEnumerable<GateBinding> gateBindings)
    {
        var byGate = new Dictionary<long, GateBinding>();
        foreach (var g in gateBindings)
        {
            byGate[g.GateId] = g;
        }

        var threads = new List<SyncSnapshotThread>();

        foreach (var c in coop)
        {
            string wait = "none";
            ulong obj = 0;
            if (IsBlocked(c.State) && TryParseWakeKey(c.BlockWakeKey, out var wt, out var o))
            {
                wait = wt;
                obj = o;
            }
            threads.Add(new SyncSnapshotThread(
                Handle: c.Handle,
                Pthread: c.Pthread,
                Name: c.Name,
                State: c.State,
                Rip: c.Rip,
                ResumeRip: c.ResumeRip,
                HostTid: c.HostTid,
                Worker: string.IsNullOrEmpty(c.Worker) ? "inline" : c.Worker,
                Wait: wait,
                Obj: obj,
                Key: c.BlockWakeKey ?? string.Empty,
                Need: 1,
                Timeout: string.IsNullOrEmpty(c.Timeout) ? "none" : c.Timeout,
                Parked: false,
                Reentrant: false,
                WaitMs: c.WaitMs));
        }

        foreach (var p in hostParks)
        {
            string wait = "unknown";
            ulong obj = 0;
            string key = string.Empty;
            if (byGate.TryGetValue(p.GateId, out var binding))
            {
                wait = binding.WaitType;
                obj = binding.Obj;
                key = binding.Key;
            }
            threads.Add(new SyncSnapshotThread(
                Handle: p.ThreadHandle,
                Pthread: p.ThreadHandle,   // handle IS the pthread pointer in this runtime
                Name: string.IsNullOrEmpty(p.Name) ? "UNKNOWN" : p.Name,
                State: "Blocked",
                Rip: p.Rip,
                ResumeRip: 0,
                HostTid: 0,
                Worker: "host-park",
                Wait: wait,
                Obj: obj,
                Key: key,
                Need: 1,
                Timeout: string.IsNullOrEmpty(p.Timeout) ? "none" : p.Timeout,
                Parked: true,
                Reentrant: false,
                WaitMs: p.WaitMs));
        }

        return threads;
    }
}
