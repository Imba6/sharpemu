// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.IO;
using SharpEmu.HLE.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

// Unit tests for the read-only sync-snapshot formatter + assembler. The line
// grammar asserted here is the contract shared with
// scripts/analyze_waitfor_graph.py (mirror any change in both places).
public sealed class SyncSnapshotTests
{
    [Fact]
    public void FormatBeginEnd_MatchGrammar()
    {
        Assert.Equal("[LOADER][DIAG] sync_snapshot.begin reason=stall seq=7",
            SyncSnapshotFormatter.FormatBegin("stall", 7));
        Assert.Equal("[LOADER][DIAG] sync_snapshot.end seq=7 threads=3 blocked=2",
            SyncSnapshotFormatter.FormatEnd(7, 3, 2));
    }

    [Fact]
    public void FormatThread_QuotesNameAndEncodesFlags()
    {
        var t = new SyncSnapshotThread(
            Handle: 0xBB, Pthread: 0x200, Name: "Background Job.Worker 0",
            State: "Blocked", Rip: 0x800D18129, ResumeRip: 0, HostTid: 9,
            Worker: "host-park", Wait: "sema", Obj: 0x86, Key: "sceKernelWaitSema:00000086",
            Need: 1, Timeout: "1000", Parked: true, Reentrant: false, WaitMs: 6100);
        var line = SyncSnapshotFormatter.FormatThread(t);
        Assert.Contains("handle=0xBB", line);
        Assert.Contains("name='Background Job.Worker 0'", line);
        Assert.Contains("wait=sema obj=0x86", line);
        Assert.Contains("parked=1 reentrant=0", line);
        Assert.Contains("key='sceKernelWaitSema:00000086'", line);
    }

    [Fact]
    public void FormatSema_EmitsUnknownSignalerVerbatim()
    {
        var s = new SyncSnapshotSema(0x86, "Baselib_SystemSemaphore", 0, 2147483647, 1,
            "UNKNOWN", "UNKNOWN");
        var line = SyncSnapshotFormatter.FormatSema(s);
        Assert.Contains("handle=0x86", line);
        Assert.Contains("last_signaler=UNKNOWN", line);
    }

    [Fact]
    public void Write_ProducesParseableBlock()
    {
        var data = new SyncSnapshotData
        {
            Reason = "manual",
            Seq = 1,
            Threads = new[]
            {
                new SyncSnapshotThread(0xAA, 0x100, "Loading.PreloadManager", "Blocked",
                    0x800D17CFB, 0, 7, "inline", "sema", 0x84, "k84", 1, "infinite",
                    true, false, 6200),
            },
            Semaphores = new[]
            {
                new SyncSnapshotSema(0x84, "Baselib_SystemSemaphore", 0, 99, 1, "0xBB",
                    "Background Job.Worker 0"),
            },
        };
        var sw = new StringWriter();
        SyncSnapshotFormatter.Write(sw, data);
        var text = sw.ToString();
        Assert.Contains("sync_snapshot.begin reason=manual seq=1", text);
        Assert.Contains("sync_snapshot.thread handle=0xAA", text);
        Assert.Contains("sync_snapshot.sema handle=0x84", text);
        Assert.Contains("sync_snapshot.end seq=1 threads=1 blocked=1", text);
    }

    [Theory]
    [InlineData("sceKernelWaitSema:00000086", "sema", 0x86UL)]
    [InlineData("sceKernelWaitEqueue:0000000000000200:0000000000000001", "equeue", 0x200UL)]
    [InlineData("event_flag:0x0000000000000042", "eventflag", 0x42UL)]
    public void TryParseWakeKey_RecognisesGenericForms(string key, string type, ulong obj)
    {
        Assert.True(SyncSnapshotAssembler.TryParseWakeKey(key, out var wt, out var o));
        Assert.Equal(type, wt);
        Assert.Equal(obj, o);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("sceKernelWaitSema:")]
    public void TryParseWakeKey_RejectsUnparseable(string key)
    {
        Assert.False(SyncSnapshotAssembler.TryParseWakeKey(key, out _, out _));
    }

    // The crux: a host-parked (formerly anonymous guest=0) waiter's wait object is
    // recovered by matching its monitor-gate id to a semaphore's gate id.
    [Fact]
    public void AssembleThreads_RecoversHostParkedWaitObjectByGateMatch()
    {
        var coop = new[]
        {
            new CoopThreadInput(0xAA, 0x100, "Loading.PreloadManager", "Blocked",
                0x800D17CFB, 0, 7, "inline", "sceKernelWaitSema:00000084", "sceKernelWaitSema",
                "infinite", true, 6200),
        };
        var parks = new[]
        {
            // The 0x86 consumer, host-parked, no guest identity — only synthetic name
            // and the gate id (777) of the 0x86 semaphore's monitor.
            new HostParkInput(0xBB, "Thread-BB", GateId: 777, Rip: 0x800D18129, Timeout: "1000", WaitMs: 6100),
        };
        var gates = new[]
        {
            new GateBinding(GateId: 777, WaitType: "sema", Obj: 0x86, Key: "sceKernelWaitSema:00000086"),
        };

        var threads = SyncSnapshotAssembler.AssembleThreads(coop, parks, gates);

        var pm = Assert.Single(threads, t => t.Name == "Loading.PreloadManager");
        Assert.Equal("sema", pm.Wait);
        Assert.Equal(0x84UL, pm.Obj);          // from BlockWakeKey

        var consumer = Assert.Single(threads, t => t.Name == "Thread-BB");
        Assert.True(consumer.Parked);
        Assert.Equal("sema", consumer.Wait);   // recovered via gate match
        Assert.Equal(0x86UL, consumer.Obj);
    }

    [Fact]
    public void AssembleThreads_LeavesUnmatchedHostParkObjectUnknownNotFabricated()
    {
        var parks = new[]
        {
            new HostParkInput(0xCC, "Thread-CC", GateId: 999, Rip: 0x9, Timeout: "1000", WaitMs: 50),
        };
        var threads = SyncSnapshotAssembler.AssembleThreads(
            System.Array.Empty<CoopThreadInput>(), parks, System.Array.Empty<GateBinding>());
        var t = Assert.Single(threads);
        Assert.Equal("unknown", t.Wait);   // no gate binding -> honest unknown
        Assert.Equal(0UL, t.Obj);
    }
}
