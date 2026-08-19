// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// pthread_sigmask changes the calling thread's blocked-signal mask and reports the
// previous mask through oldset. Signals are not delivered to guest threads here, so
// the observable contract is the mask round-trip and the return code (0 or errno).
// The mask is [ThreadStatic]; each test runs its whole scenario on one thread.
public sealed class PthreadSigmaskTests
{
    private const ulong MemoryBase = 0x6_0000_0000;
    private const ulong SetAddress = MemoryBase + 0x40;
    private const ulong OldAddress = MemoryBase + 0x60;

    private const int SigBlock = 1;
    private const int SigUnblock = 2;
    private const int SigSetmask = 3;
    private const int Efault = 14;
    private const int Einval = 22;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static ulong Call(CpuContext ctx, int how, ulong set, ulong oldset)
    {
        ctx[CpuRegister.Rdi] = unchecked((ulong)(long)how);
        ctx[CpuRegister.Rsi] = set;
        ctx[CpuRegister.Rdx] = oldset;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelPthreadCompatExports.PosixPthreadSigmask(ctx));
        return ctx[CpuRegister.Rax];
    }

    private static void WriteMask(FakeCpuMemory memory, ulong address, ulong low, ulong high)
    {
        Assert.True(memory.TryWrite(address, System.BitConverter.GetBytes(low)));
        Assert.True(memory.TryWrite(address + 8, System.BitConverter.GetBytes(high)));
    }

    private static (ulong low, ulong high) ReadMask(CpuContext ctx, ulong address)
    {
        Assert.True(ctx.TryReadUInt64(address, out var low));
        Assert.True(ctx.TryReadUInt64(address + 8, out var high));
        return (low, high);
    }

    [Fact]
    public void SetMaskThenQuery_RoundTripsThroughOldset()
    {
        var ctx = NewContext(out var memory);
        // Reset this thread's mask to empty first so prior tests don't leak in.
        WriteMask(memory, SetAddress, 0, 0);
        Assert.Equal(0UL, Call(ctx, SigSetmask, SetAddress, 0));

        // Set a concrete mask; oldset should report the empty mask we just set.
        WriteMask(memory, SetAddress, 0x1234, 0xABCD);
        Assert.Equal(0UL, Call(ctx, SigSetmask, SetAddress, OldAddress));
        Assert.Equal((0UL, 0UL), ReadMask(ctx, OldAddress));

        // Query only (null set): oldset now reports the mask we set.
        Assert.Equal(0UL, Call(ctx, SigBlock, 0, OldAddress));
        Assert.Equal((0x1234UL, 0xABCDUL), ReadMask(ctx, OldAddress));
    }

    [Fact]
    public void Block_OrsBits_Unblock_ClearsBits()
    {
        var ctx = NewContext(out var memory);
        WriteMask(memory, SetAddress, 0, 0);
        Call(ctx, SigSetmask, SetAddress, 0);

        WriteMask(memory, SetAddress, 0b0011, 0);
        Call(ctx, SigBlock, SetAddress, 0);
        WriteMask(memory, SetAddress, 0b0110, 0);
        Call(ctx, SigBlock, SetAddress, 0);

        // Current mask should be 0b0111. Read via query.
        Call(ctx, SigBlock, 0, OldAddress);
        Assert.Equal((0b0111UL, 0UL), ReadMask(ctx, OldAddress));

        // Unblock two bits.
        WriteMask(memory, SetAddress, 0b0010, 0);
        Call(ctx, SigUnblock, SetAddress, 0);
        Call(ctx, SigBlock, 0, OldAddress);
        Assert.Equal((0b0101UL, 0UL), ReadMask(ctx, OldAddress));

        // Cleanup for other tests sharing this thread.
        WriteMask(memory, SetAddress, 0, 0);
        Call(ctx, SigSetmask, SetAddress, 0);
    }

    [Fact]
    public void InvalidHow_WithSet_ReturnsEinval()
    {
        var ctx = NewContext(out var memory);
        WriteMask(memory, SetAddress, 0x1, 0);
        Assert.Equal((ulong)Einval, Call(ctx, 99, SetAddress, 0));
    }

    [Fact]
    public void InvalidHow_WithNullSet_IsIgnored()
    {
        var ctx = NewContext(out _);
        // Null set => query only; how is ignored, so no EINVAL.
        Assert.Equal(0UL, Call(ctx, 99, 0, OldAddress));
    }

    [Fact]
    public void BadOldsetPointer_ReturnsEfault()
    {
        var ctx = NewContext(out _);
        Assert.Equal((ulong)Efault, Call(ctx, SigSetmask, 0, 0xDEAD_0000_0000));
    }

    [Fact]
    public void BadSetPointer_ReturnsEfault()
    {
        var ctx = NewContext(out _);
        Assert.Equal((ulong)Efault, Call(ctx, SigSetmask, 0xDEAD_0000_0000, 0));
    }
}
