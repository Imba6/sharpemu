// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// libc time(): returns seconds since the Unix epoch and stores the same value
// through tloc when it is non-null.
public sealed class KernelTimeTests
{
    private const ulong MemoryBase = 0x7_0000_0000;
    private const ulong TlocAddress = MemoryBase + 0x40;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static long UnixNow() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    [Fact]
    public void Time_NullTloc_ReturnsCurrentSeconds()
    {
        var ctx = NewContext(out _);
        var before = UnixNow();
        ctx[CpuRegister.Rdi] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelRuntimeCompatExports.Time(ctx));

        var after = UnixNow();
        var returned = unchecked((long)ctx[CpuRegister.Rax]);
        Assert.InRange(returned, before, after);
    }

    [Fact]
    public void Time_WithTloc_StoresSameValueAsReturned()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = TlocAddress;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelRuntimeCompatExports.Time(ctx));

        var returned = unchecked((long)ctx[CpuRegister.Rax]);
        Assert.True(ctx.TryReadUInt64(TlocAddress, out var stored));
        Assert.Equal(returned, unchecked((long)stored));
        Assert.InRange(returned, UnixNow() - 5, UnixNow() + 5);
    }

    [Fact]
    public void Time_BadTloc_ReturnsMinusOneWithoutCrashing()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0xDEAD_0000_0000; // outside the mapped region

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelRuntimeCompatExports.Time(ctx));
        Assert.Equal(unchecked((ulong)-1L), ctx[CpuRegister.Rax]);
    }
}
