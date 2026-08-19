// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// bcmp compares n bytes and returns 0 when they are equal, nonzero otherwise.
public sealed class KernelBcmpTests
{
    private const ulong MemoryBase = 0x9_0000_0000;
    private const ulong LeftAddress = MemoryBase + 0x40;
    private const ulong RightAddress = MemoryBase + 0x80;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static ulong Bcmp(FakeCpuMemory memory, byte[] left, byte[] right, ulong count, out int status)
    {
        var ctx = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(LeftAddress, left));
        Assert.True(memory.TryWrite(RightAddress, right));
        ctx[CpuRegister.Rdi] = LeftAddress;
        ctx[CpuRegister.Rsi] = RightAddress;
        ctx[CpuRegister.Rdx] = count;
        status = KernelMemoryCompatExports.Bcmp(ctx);
        return ctx[CpuRegister.Rax];
    }

    [Fact]
    public void Bcmp_EqualBytes_ReturnsZero()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var result = Bcmp(memory, data, (byte[])data.Clone(), 5, out var status);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, status);
        Assert.Equal(0UL, result);
    }

    [Fact]
    public void Bcmp_DifferentBytes_ReturnsNonzero()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var result = Bcmp(memory, new byte[] { 1, 2, 3 }, new byte[] { 1, 9, 3 }, 3, out var status);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, status);
        Assert.NotEqual(0UL, result);
    }

    [Fact]
    public void Bcmp_ZeroLength_ReturnsZero()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var result = Bcmp(memory, new byte[] { 7 }, new byte[] { 8 }, 0, out var status);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, status);
        Assert.Equal(0UL, result);
    }

    [Fact]
    public void Bcmp_DifferenceOnlyBeyondCount_IgnoredReturnsZero()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var result = Bcmp(memory, new byte[] { 1, 2, 3, 0xFF }, new byte[] { 1, 2, 3, 0x00 }, 3, out _);
        Assert.Equal(0UL, result);
    }

    [Fact]
    public void Bcmp_UnmappedPointer_ReturnsMemoryFault()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0xDEAD_0000_0000;
        ctx[CpuRegister.Rsi] = RightAddress;
        ctx[CpuRegister.Rdx] = 4;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, KernelMemoryCompatExports.Bcmp(ctx));
    }
}
