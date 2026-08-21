// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Native Guest Execution V2 stage 3: the decision of whether a guest run executes
// on a native worker (off the CLR-managed stack) or inline. tbb_thead is always
// routed; V2 additionally routes ordinary TOP-LEVEL pthread runs while keeping
// nested/reentrant runs (module init, 0x1E host-park delivery, callbacks) inline;
// with V2 off, ordinary runs stay inline (default behavior). The deeper block/
// resume / worker-migration behavior is proven by prototypes/NativeGuestV2 and the
// real-title validation; this pins the routing gate.
public sealed class NativeGuestV2RoutingTests
{
    [Theory]
    // tbb_thead: always a native worker, regardless of V2 or reentrancy.
    [InlineData("tbb_thead", false, false, true)]
    [InlineData("tbb_thead", true, false, true)]
    [InlineData("tbb_thead", false, true, true)]
    [InlineData("tbb_thead", true, true, true)]
    // V2 OFF: ordinary guest runs stay inline (current default behavior).
    [InlineData("Job.Worker 3", false, false, false)]
    [InlineData("Job.Worker 3", true, false, false)]
    [InlineData("GfxDeviceWorker", false, false, false)]
    // V2 ON: ordinary TOP-LEVEL run -> native worker; nested/reentrant -> inline.
    [InlineData("Job.Worker 3", false, true, true)]
    [InlineData("Job.Worker 3", true, true, false)]
    [InlineData("Loading.AsyncRead", false, true, true)]
    [InlineData("kernel exception 0x1E host park", true, true, false)]
    public void ShouldRunGuestOnNativeWorker_MatchesStage3Policy(
        string name, bool reentrant, bool v2Enabled, bool expected)
    {
        Assert.Equal(expected, DirectExecutionBackend.ShouldRunGuestOnNativeWorker(name, reentrant, v2Enabled));
    }

    [Fact]
    public void Reentrant_NestedRuns_StayInline_UnderV2()
    {
        // Essential invariant: the 0x1E host-park delivery path
        // (TryDeliverQueuedGuestException -> TryCallGuestFunction, reentrant) and any
        // nested module-init/callback must NOT be routed to a second worker under V2
        // -- that was the earlier experiment that broke the Boehm suspend handshake.
        Assert.False(DirectExecutionBackend.ShouldRunGuestOnNativeWorker("main", reentrant: true, v2Enabled: true));
        Assert.False(DirectExecutionBackend.ShouldRunGuestOnNativeWorker("Audio", reentrant: true, v2Enabled: true));
    }
}
