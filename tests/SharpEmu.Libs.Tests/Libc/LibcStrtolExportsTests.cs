// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

using SharpEmu.HLE;
using SharpEmu.Libs.LibcStrtol;

using Xunit;

namespace SharpEmu.Libs.Tests.Libc;

public sealed class LibcStrtolExportsTests
{
    private const ulong Base = 0x4_0000_0000;
    private const ulong StringAddress = Base + 0x100;
    private const ulong EndPtrAddress = Base + 0x080;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(Base, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static void PutString(FakeCpuMemory memory, ulong address, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Assert.True(memory.TryWrite(address, bytes));
        Assert.True(memory.TryWrite(address + (ulong)bytes.Length, new byte[] { 0 }));
    }

    private static ulong Run(Func<CpuContext, int> export, string input, int @base, out ulong endptr, ulong endptrSlot = EndPtrAddress)
    {
        var context = NewContext(out var memory);
        PutString(memory, StringAddress, input);
        context[CpuRegister.Rdi] = StringAddress;
        context[CpuRegister.Rsi] = endptrSlot;
        context[CpuRegister.Rdx] = unchecked((ulong)(long)@base);

        var status = export(context);
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, status);

        endptr = 0;
        if (endptrSlot != 0)
        {
            Assert.True(context.TryReadUInt64(endptrSlot, out endptr));
        }

        return context[CpuRegister.Rax];
    }

    // Offset from the string start where scanning stopped.
    private static ulong Consumed(ulong endptr) => endptr - StringAddress;

    [Theory]
    [InlineData("0", 10, 0UL, 1UL)]
    [InlineData("123", 10, 123UL, 3UL)]
    [InlineData("   42", 10, 42UL, 5UL)]
    [InlineData("\t\n 7abc", 10, 7UL, 4UL)]
    [InlineData("+99", 10, 99UL, 3UL)]
    [InlineData("2147483648", 10, 2147483648UL, 10UL)]
    public void Strtoull_DecimalAndWhitespace(string input, int @base, ulong expected, ulong consumed)
    {
        var result = Run(LibcStrtolExports.Strtoull, input, @base, out var endptr);
        Assert.Equal(expected, result);
        Assert.Equal(consumed, Consumed(endptr));
    }

    [Theory]
    [InlineData("0x1A", 0, 26UL, 4UL)]
    [InlineData("0X10", 16, 16UL, 4UL)]
    [InlineData("1A", 16, 26UL, 2UL)]
    [InlineData("017", 0, 15UL, 3UL)]        // octal via base-0
    [InlineData("017", 8, 15UL, 3UL)]
    [InlineData("101", 2, 5UL, 3UL)]
    [InlineData("zz", 36, 35UL * 36 + 35, 2UL)]
    [InlineData("deadBEEF", 16, 0xDEADBEEFUL, 8UL)]
    public void Strtoull_BasesAndPrefixes(string input, int @base, ulong expected, ulong consumed)
    {
        var result = Run(LibcStrtolExports.Strtoull, input, @base, out var endptr);
        Assert.Equal(expected, result);
        Assert.Equal(consumed, Consumed(endptr));
    }

    [Fact]
    public void Strtoull_ZeroXWithoutHexDigit_ConsumesOnlyLeadingZero()
    {
        // "0x" not followed by a hex digit: the '0' is the value, endptr points at 'x'.
        var result = Run(LibcStrtolExports.Strtoull, "0xZ", 0, out var endptr);
        Assert.Equal(0UL, result);
        Assert.Equal(1UL, Consumed(endptr));
    }

    [Fact]
    public void Strtoull_NoConversion_EndptrIsOriginal()
    {
        var result = Run(LibcStrtolExports.Strtoull, "   xyz", 10, out var endptr);
        Assert.Equal(0UL, result);
        Assert.Equal(0UL, Consumed(endptr)); // endptr == original nptr
    }

    [Fact]
    public void Strtoull_MaxValue_NoOverflow()
    {
        var result = Run(LibcStrtolExports.Strtoull, "18446744073709551615", 10, out var endptr);
        Assert.Equal(ulong.MaxValue, result);
        Assert.Equal(20UL, Consumed(endptr));
    }

    [Fact]
    public void Strtoull_Overflow_ReturnsUlongMax()
    {
        var result = Run(LibcStrtolExports.Strtoull, "18446744073709551616", 10, out var endptr);
        Assert.Equal(ulong.MaxValue, result);
        Assert.Equal(20UL, Consumed(endptr)); // still scans the whole number
    }

    [Fact]
    public void Strtoull_NegativeSign_WrapsModulo()
    {
        var result = Run(LibcStrtolExports.Strtoull, "-1", 10, out _);
        Assert.Equal(ulong.MaxValue, result);
    }

    [Fact]
    public void Strtoul_HexOverflow_ReturnsUlongMax()
    {
        var result = Run(LibcStrtolExports.Strtoul, "0x1FFFFFFFFFFFFFFFF", 0, out _);
        Assert.Equal(ulong.MaxValue, result);
    }

    [Theory]
    [InlineData("-5", 10, unchecked((ulong)(-5L)))]
    [InlineData("2147483648", 10, 2147483648UL)]
    [InlineData("9223372036854775807", 10, 9223372036854775807UL)]        // LLONG_MAX
    [InlineData("-9223372036854775808", 10, 0x8000000000000000UL)]        // LLONG_MIN
    public void Strtoll_SignedValues(string input, int @base, ulong expectedBits)
    {
        var result = Run(LibcStrtolExports.Strtoll, input, @base, out _);
        Assert.Equal(expectedBits, result);
    }

    [Fact]
    public void Strtoll_PositiveOverflow_ClampsToLlongMax()
    {
        var result = Run(LibcStrtolExports.Strtoll, "9223372036854775808", 10, out var endptr);
        Assert.Equal(0x7FFFFFFFFFFFFFFFUL, result); // LLONG_MAX
        Assert.Equal(19UL, Consumed(endptr));
    }

    [Fact]
    public void Strtoll_NegativeOverflow_ClampsToLlongMin()
    {
        var result = Run(LibcStrtolExports.Strtoll, "-9223372036854775809", 10, out _);
        Assert.Equal(0x8000000000000000UL, result); // LLONG_MIN
    }

    [Fact]
    public void Strtol_MatchesStrtoll()
    {
        var a = Run(LibcStrtolExports.Strtol, "-12345", 10, out _);
        var b = Run(LibcStrtolExports.Strtoll, "-12345", 10, out _);
        Assert.Equal(b, a);
        Assert.Equal(unchecked((ulong)(-12345L)), a);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(37)]
    [InlineData(-1)]
    public void Strtoull_InvalidBase_NoConversion(int @base)
    {
        var result = Run(LibcStrtolExports.Strtoull, "123", @base, out var endptr);
        Assert.Equal(0UL, result);
        Assert.Equal(0UL, Consumed(endptr));
    }

    [Fact]
    public void Strtoull_NullPointer_DoesNotCrash()
    {
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = 0;
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 10;

        var status = LibcStrtolExports.Strtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, status);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Strtoull_UnmappedPointer_DoesNotCrash()
    {
        var context = NewContext(out _);
        context[CpuRegister.Rdi] = 0xDEAD_0000_0000; // outside the fake region
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = 10;

        var status = LibcStrtolExports.Strtoull(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, status);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void Strtoull_NullEndptr_IsIgnored()
    {
        var result = Run(LibcStrtolExports.Strtoull, "77", 10, out _, endptrSlot: 0);
        Assert.Equal(77UL, result);
    }
}
