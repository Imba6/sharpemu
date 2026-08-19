// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Libm;

using Xunit;

namespace SharpEmu.Libs.Tests.Libc;

// ceil takes its double argument in XMM0 and returns in XMM0. Verify the value
// semantics and the special cases the C library preserves.
public sealed class LibmExportsTests
{
    private static double Ceil(double x)
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x100), Generation.Gen5);
        ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(x)), 0xDEAD);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibmExports.Ceil(ctx));
        ctx.GetXmmRegister(0, out var low, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)low));
    }

    [Theory]
    [InlineData(2.3, 3.0)]
    [InlineData(2.0, 2.0)]
    [InlineData(-2.3, -2.0)]
    [InlineData(-2.0, -2.0)]
    [InlineData(0.0, 0.0)]
    [InlineData(0.0001, 1.0)]
    [InlineData(1e15 + 0.5, 1e15 + 1.0)]
    [InlineData(-0.9, 0.0)]
    public void Ceil_Values(double input, double expected)
    {
        Assert.Equal(expected, Ceil(input));
    }

    [Fact]
    public void Ceil_NegativeFraction_ReturnsNegativeZero()
    {
        var result = Ceil(-0.5);
        Assert.Equal(0.0, result);
        // C ceil preserves the sign: -0.5 -> -0.0.
        Assert.True(double.IsNegative(result));
    }

    [Fact]
    public void Ceil_Nan_ReturnsNan()
    {
        Assert.True(double.IsNaN(Ceil(double.NaN)));
    }

    [Fact]
    public void Ceil_Infinities_Preserved()
    {
        Assert.True(double.IsPositiveInfinity(Ceil(double.PositiveInfinity)));
        Assert.True(double.IsNegativeInfinity(Ceil(double.NegativeInfinity)));
    }
}
