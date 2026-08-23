// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Regression coverage for the sce* kernel semaphore primitive
// (sceKernelCreateSema / WaitSema / SignalSema / PollSema). These pin the
// invariants that the Cocoon "Baselib 0x86" investigation relied on to prove the
// stall is upstream producer starvation, NOT a broken primitive:
//   * a timed-out wait does not consume the count (self-healing), so a later
//     signal is always honored;
//   * a signal delivered before/without a waiter persists in the count;
//   * tokens are conserved across signal/wait cycles (no lost/duplicated token);
//   * max-count overflow and argument validation behave per PS5 semantics.
// Only non-blocking paths and one bounded (~1 ms) host-fallback timeout are
// exercised, so the suite is deterministic and cannot hang CI.
public sealed class KernelSemaphoreSemanticsTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong HandleAddress = MemoryBase + 0x100;  // out: sema handle
    private const ulong NameAddress = MemoryBase + 0x200;    // "S\0"
    private const ulong TimeoutAddress = MemoryBase + 0x300; // wait timeout (usec)

    private static CpuContext CreateContext()
    {
        var ctx = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        // Name string "S\0" (0x53 then NUL) for CreateSema.
        Assert.True(ctx.TryWriteUInt32(NameAddress, 0x53));
        return ctx;
    }

    private static uint CreateSema(CpuContext ctx, int initialCount, int maxCount, uint attr = 1)
    {
        ctx[CpuRegister.Rdi] = HandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = attr;
        ctx[CpuRegister.Rcx] = unchecked((ulong)(long)initialCount);
        ctx[CpuRegister.R8] = unchecked((ulong)(long)maxCount);
        ctx[CpuRegister.R9] = 0;
        Assert.Equal(0, KernelSemaphoreCompatExports.KernelCreateSema(ctx));
        Assert.True(ctx.TryReadUInt32(HandleAddress, out var handle));
        Assert.NotEqual(0u, handle);
        return handle;
    }

    private static int Signal(CpuContext ctx, uint handle, int count) =>
        KernelSemaphoreCompatExports.KernelSignalSema(ctx, handle, count);

    private static int Poll(CpuContext ctx, uint handle, int need) =>
        KernelSemaphoreCompatExports.KernelPollSema(ctx, handle, need);

    // rdx=0 -> infinite is avoided; pass a finite timeout address holding usec.
    private static int WaitFinite(CpuContext ctx, uint handle, int need, uint timeoutUsec)
    {
        Assert.True(ctx.TryWriteUInt32(TimeoutAddress, timeoutUsec));
        ctx[CpuRegister.Rdi] = handle;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)need);
        ctx[CpuRegister.Rdx] = TimeoutAddress;
        return KernelSemaphoreCompatExports.KernelWaitSema(ctx);
    }

    [Theory]
    [InlineData(0, 0)]     // maxCount <= 0
    [InlineData(-1, 4)]    // initialCount < 0
    [InlineData(5, 4)]     // initialCount > maxCount
    public void CreateSemaRejectsInvalidCounts(int init, int max)
    {
        var ctx = CreateContext();
        ctx[CpuRegister.Rdi] = HandleAddress;
        ctx[CpuRegister.Rsi] = NameAddress;
        ctx[CpuRegister.Rdx] = 1;
        ctx[CpuRegister.Rcx] = unchecked((ulong)(long)init);
        ctx[CpuRegister.R8] = unchecked((ulong)(long)max);
        ctx[CpuRegister.R9] = 0;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelSemaphoreCompatExports.KernelCreateSema(ctx));
    }

    [Fact]
    public void SignalBeforeWaitPersistsInCount()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 8);

        // Signal with no waiter present; the token must persist.
        Assert.Equal(0, Signal(ctx, h, 1));
        // A subsequent wait acquires it immediately via the fast path.
        Assert.Equal(0, WaitFinite(ctx, h, need: 1, timeoutUsec: 1000));
    }

    [Fact]
    public void TokensAreConservedAcrossSignalAndWaitCycles()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 8);

        Assert.Equal(0, Signal(ctx, h, 3));
        // Three single-token acquisitions succeed...
        Assert.Equal(0, WaitFinite(ctx, h, 1, 1000));
        Assert.Equal(0, WaitFinite(ctx, h, 1, 1000));
        Assert.Equal(0, WaitFinite(ctx, h, 1, 1000));
        // ...the fourth finds the count exhausted and times out (does not block
        // forever because the timeout is finite).
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            WaitFinite(ctx, h, 1, 1000));
    }

    // The crux invariant of the 0x86 diagnosis: a timeout must NOT consume the
    // count. After a timed-out wait, the semaphore is undamaged and a later
    // signal is honored on the very next wait.
    [Fact]
    public void TimedOutWaitDoesNotPoisonSemaphore()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 8);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            WaitFinite(ctx, h, 1, 1000));

        // The producer eventually signals; the consumer's next retry succeeds.
        Assert.Equal(0, Signal(ctx, h, 1));
        Assert.Equal(0, WaitFinite(ctx, h, 1, 1000));

        // And the token was consumed exactly once: the following wait times out.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            WaitFinite(ctx, h, 1, 1000));
    }

    [Fact]
    public void SignalOverflowBeyondMaxCountIsRejected()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 2);

        Assert.Equal(0, Signal(ctx, h, 2));                 // count == max
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            Signal(ctx, h, 1));                             // would exceed max
    }

    [Fact]
    public void SignalRejectsNonPositiveCountAndUnknownHandle()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 4);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, Signal(ctx, h, 0));
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, Signal(ctx, 0xDEAD, 1));
    }

    [Fact]
    public void PollConsumesWhenAvailableAndReportsBusyWhenNot()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 1, maxCount: 4);

        Assert.Equal(0, Poll(ctx, h, 1));                   // consumes the token
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_BUSY, Poll(ctx, h, 1));
    }

    [Fact]
    public void WaitRejectsInvalidNeedCountAndUnknownHandle()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 4);

        // need > max
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            WaitFinite(ctx, h, need: 5, timeoutUsec: 1000));
        // need < 1
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            WaitFinite(ctx, h, need: 0, timeoutUsec: 1000));
        // unknown handle
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND,
            WaitFinite(ctx, 0xBEEF, need: 1, timeoutUsec: 1000));
    }

    [Fact]
    public void MultiTokenWaitAcquiresAllOrTimesOut()
    {
        var ctx = CreateContext();
        var h = CreateSema(ctx, initialCount: 0, maxCount: 8);

        Assert.Equal(0, Signal(ctx, h, 2));
        // need=3 with only 2 available -> all-or-nothing -> times out, and must
        // NOT partially consume the 2 tokens that are present.
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_TIMED_OUT,
            WaitFinite(ctx, h, need: 3, timeoutUsec: 1000));
        // The 2 tokens are still there.
        Assert.Equal(0, WaitFinite(ctx, h, need: 2, timeoutUsec: 1000));
    }
}
