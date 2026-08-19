// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.LibcRand48;

using Xunit;

namespace SharpEmu.Libs.Tests.Libc;

// srand48 seeds the shared 48-bit drand48 state; lrand48 draws a non-negative
// 31-bit value from it. The reference sequences below were computed independently
// from the standard recurrence X = (0x5DEECE66D*X + 0xB) mod 2^48 with the
// srand48 seeding X = (seed << 16) | 0x330E. Every test seeds first, so the shared
// global state makes them order-independent.
//
// The collection attribute serializes these tests so a concurrent test in another
// class cannot interleave a draw into the shared state mid-sequence.
[Collection("rand48-serial")]
public sealed class LibcRand48ExportsTests
{
    private static CpuContext NewContext() =>
        new(new FakeCpuMemory(0x1_0000_0000, 0x100), Generation.Gen5);

    private static void Srand48(CpuContext ctx, long seed)
    {
        ctx[CpuRegister.Rdi] = unchecked((ulong)seed);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRand48Exports.Srand48(ctx));
    }

    private static ulong Lrand48(CpuContext ctx)
    {
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcRand48Exports.Lrand48(ctx));
        return ctx[CpuRegister.Rax];
    }

    private static ulong[] Draw(CpuContext ctx, int count)
    {
        var result = new ulong[count];
        for (var i = 0; i < count; i++)
        {
            result[i] = Lrand48(ctx);
        }

        return result;
    }

    [Theory]
    [InlineData(0L, new ulong[] { 366850414, 1610402240, 206956554, 1869309841, 1239749840 })]
    [InlineData(1L, new ulong[] { 89400484, 976015093, 1792756325 })]
    [InlineData(12345L, new ulong[] { 483889296, 1973930609, 444188209 })]
    public void Seeded_ProducesReferenceSequence(long seed, ulong[] expected)
    {
        var ctx = NewContext();
        Srand48(ctx, seed);
        Assert.Equal(expected, Draw(ctx, expected.Length));
    }

    [Fact]
    public void OnlyLow32BitsOfSeedAreUsed()
    {
        var ctx = NewContext();
        Srand48(ctx, 12345L);
        var a = Draw(ctx, 3);

        // Setting arbitrary high bits must not change the sequence.
        Srand48(ctx, unchecked((long)0xFFFF_FFFF_0000_3039UL)); // low 32 bits == 12345
        var b = Draw(ctx, 3);

        Assert.Equal(a, b);
    }

    [Fact]
    public void Reseeding_ReproducesSequence()
    {
        var ctx = NewContext();
        Srand48(ctx, 777L);
        var first = Draw(ctx, 8);
        Srand48(ctx, 777L);
        var second = Draw(ctx, 8);
        Assert.Equal(first, second);
    }

    [Fact]
    public void Draws_AreNonNegative31Bit()
    {
        var ctx = NewContext();
        Srand48(ctx, 424242L);
        foreach (var value in Draw(ctx, 200))
        {
            Assert.InRange(value, 0UL, 0x7FFF_FFFFUL);
        }
    }
}
