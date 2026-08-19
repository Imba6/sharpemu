// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// libc ASCII case family: strncasecmp compares up to n bytes case-insensitively;
// tolower/toupper fold a single ASCII letter and pass everything else through.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelCaseFamilyTests
{
    private const ulong MemoryBase = 0xD_0000_0000;
    private const ulong LeftAddress = MemoryBase + 0x40;
    private const ulong RightAddress = MemoryBase + 0x80;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void PutCString(FakeCpuMemory memory, ulong address, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Assert.True(memory.TryWrite(address, bytes));
        Assert.True(memory.TryWrite(address + (ulong)bytes.Length, new byte[] { 0 }));
    }

    private static long Strncasecmp(string a, string b, ulong n)
    {
        var ctx = NewContext(out var memory);
        PutCString(memory, LeftAddress, a);
        PutCString(memory, RightAddress, b);
        ctx[CpuRegister.Rdi] = LeftAddress;
        ctx[CpuRegister.Rsi] = RightAddress;
        ctx[CpuRegister.Rdx] = n;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Strncasecmp(ctx));
        return unchecked((int)ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData("MAP01", "map01", 8UL, true)]   // WAD lump name, case-insensitive
    [InlineData("PLAYPAL", "playpal", 8UL, true)]
    [InlineData("abc", "abd", 8UL, false)]
    [InlineData("abc", "abd", 2UL, true)]        // only first 2 compared
    [InlineData("", "", 8UL, true)]
    [InlineData("Doom", "DOOM", 4UL, true)]
    public void Strncasecmp_Compares(string a, string b, ulong n, bool equal)
    {
        var result = Strncasecmp(a, b, n);
        Assert.Equal(equal, result == 0);
    }

    [Fact]
    public void Strncasecmp_ZeroLength_AlwaysEqual()
    {
        Assert.Equal(0, Strncasecmp("abc", "xyz", 0));
    }

    [Fact]
    public void Strncasecmp_OrdersLikeC()
    {
        Assert.True(Strncasecmp("a", "b", 4) < 0);
        Assert.True(Strncasecmp("b", "a", 4) > 0);
    }

    private static int Tolower(int c)
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)c);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Tolower(ctx));
        return unchecked((int)ctx[CpuRegister.Rax]);
    }

    private static int Toupper(int c)
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = unchecked((ulong)(uint)c);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Toupper(ctx));
        return unchecked((int)ctx[CpuRegister.Rax]);
    }

    [Theory]
    [InlineData('A', 'a')]
    [InlineData('Z', 'z')]
    [InlineData('a', 'a')]
    [InlineData('5', '5')]
    [InlineData('!', '!')]
    public void Tolower_Folds(int input, int expected) => Assert.Equal(expected, Tolower(input));

    [Theory]
    [InlineData('a', 'A')]
    [InlineData('z', 'Z')]
    [InlineData('A', 'A')]
    [InlineData('5', '5')]
    [InlineData('!', '!')]
    public void Toupper_Folds(int input, int expected) => Assert.Equal(expected, Toupper(input));

    [Fact]
    public void Case_Eof_PassesThrough()
    {
        Assert.Equal(-1, Tolower(-1));
        Assert.Equal(-1, Toupper(-1));
    }
}
