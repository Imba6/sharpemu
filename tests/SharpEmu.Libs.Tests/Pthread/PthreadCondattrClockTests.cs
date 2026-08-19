// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// scePthreadCondattrSetclock records the clock the caller selected for a condition
// variable attribute. It accepts the FreeBSD clock ids and rejects everything else
// with EINVAL, and rejects a null attribute pointer.
public sealed class PthreadCondattrClockTests
{
    private const ulong MemoryBase = 0x5_0000_0000;
    private const ulong AttrAddress = MemoryBase + 0x40;

    private const int Ok = (int)OrbisGen2Result.ORBIS_GEN2_OK;
    private const int InvalidArgument = (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;

    private const int ClockRealtime = 0;
    private const int ClockMonotonic = 4;

    private static CpuContext NewContext() =>
        new(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);

    private static int Setclock(CpuContext ctx, ulong attr, int clock)
    {
        ctx[CpuRegister.Rdi] = attr;
        ctx[CpuRegister.Rsi] = unchecked((ulong)(long)clock);
        return KernelPthreadCompatExports.PthreadCondattrSetclock(ctx);
    }

    private static int Init(CpuContext ctx, ulong attr)
    {
        ctx[CpuRegister.Rdi] = attr;
        return KernelPthreadCompatExports.PthreadCondattrInit(ctx);
    }

    private static int Destroy(CpuContext ctx, ulong attr)
    {
        ctx[CpuRegister.Rdi] = attr;
        return KernelPthreadCompatExports.PthreadCondattrDestroy(ctx);
    }

    private static int? StoredClock(ulong attr)
    {
        var field = typeof(KernelPthreadCompatExports)
            .GetField("_condAttrStates", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var dict = (IDictionary)field!.GetValue(null)!;
        return dict.Contains(attr) ? (int)dict[attr]! : null;
    }

    [Fact]
    public void Setclock_ValidClock_StoresAndReturnsOk()
    {
        var ctx = NewContext();
        Assert.Equal(Ok, Init(ctx, AttrAddress));
        Assert.Equal(ClockRealtime, StoredClock(AttrAddress));

        Assert.Equal(Ok, Setclock(ctx, AttrAddress, ClockMonotonic));
        Assert.Equal(ClockMonotonic, StoredClock(AttrAddress));

        Assert.Equal(Ok, Destroy(ctx, AttrAddress));
        Assert.Null(StoredClock(AttrAddress));
    }

    [Theory]
    [InlineData(0)]  // CLOCK_REALTIME
    [InlineData(1)]  // CLOCK_VIRTUAL
    [InlineData(2)]  // CLOCK_PROF
    [InlineData(4)]  // CLOCK_MONOTONIC
    [InlineData(5)]  // CLOCK_UPTIME
    [InlineData(13)] // CLOCK_SECOND
    public void Setclock_AcceptedClocks_ReturnOk(int clock)
    {
        var ctx = NewContext();
        Assert.Equal(Ok, Setclock(ctx, AttrAddress, clock));
        Assert.Equal(clock, StoredClock(AttrAddress));
        Destroy(ctx, AttrAddress);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(14)] // CLOCK_THREAD_CPUTIME_ID — not accepted for condattr
    [InlineData(-1)]
    [InlineData(99)]
    public void Setclock_RejectedClocks_ReturnEinval(int clock)
    {
        var ctx = NewContext();
        Assert.Equal(InvalidArgument, Setclock(ctx, AttrAddress, clock));
        Assert.Null(StoredClock(AttrAddress));
    }

    [Fact]
    public void Setclock_NullAttr_ReturnsEinval()
    {
        var ctx = NewContext();
        Assert.Equal(InvalidArgument, Setclock(ctx, 0, ClockMonotonic));
    }

    [Fact]
    public void Setclock_WithoutInit_UpsertsClock()
    {
        // FreeBSD setclock writes the attribute field regardless of init tracking.
        var ctx = NewContext();
        var attr = AttrAddress + 0x10;
        Assert.Equal(Ok, Setclock(ctx, attr, ClockMonotonic));
        Assert.Equal(ClockMonotonic, StoredClock(attr));
        Destroy(ctx, attr);
    }
}
