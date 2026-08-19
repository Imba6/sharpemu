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

    private static double Log(double x)
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x100), Generation.Gen5);
        ctx.SetXmmRegister(0, unchecked((ulong)BitConverter.DoubleToInt64Bits(x)), 0xBEEF);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibmExports.Log(ctx));
        ctx.GetXmmRegister(0, out var low, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)low));
    }

    [Fact]
    public void Log_One_IsZero() => Assert.Equal(0.0, Log(1.0));

    [Fact]
    public void Log_E_IsOne() => Assert.Equal(1.0, Log(Math.E), 12);

    [Fact]
    public void Log_KnownValue() => Assert.Equal(Math.Log(10.0), Log(10.0), 12);

    [Fact]
    public void Log_Zero_IsNegativeInfinity() => Assert.True(double.IsNegativeInfinity(Log(0.0)));

    [Fact]
    public void Log_Negative_IsNan() => Assert.True(double.IsNaN(Log(-1.0)));

    [Fact]
    public void Log_Nan_IsNan() => Assert.True(double.IsNaN(Log(double.NaN)));

    [Fact]
    public void Log_PositiveInfinity_IsPositiveInfinity() =>
        Assert.True(double.IsPositiveInfinity(Log(double.PositiveInfinity)));
}
