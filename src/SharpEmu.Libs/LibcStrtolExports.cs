// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.LibcStrtol;

/// <summary>
/// libc integer string conversions: <c>strtol</c>, <c>strtoll</c>, <c>strtoul</c>,
/// and <c>strtoull</c>. All four share one C-standard scanner (leading whitespace,
/// optional sign, base 0 auto-detection, explicit bases 2..36, and the "0x"/"0X"
/// hexadecimal prefix) and differ only in the range they clamp to and how the sign
/// is applied. The scanner reads guest memory a byte at a time through the CPU
/// context, so a bad guest pointer stops the scan cleanly instead of faulting the
/// host.
/// </summary>
public static class LibcStrtolExports
{
    // FreeBSD errno subset (sys/errno.h).
    private const int Einval = 22;
    private const int Erange = 34;

    // Largest magnitude a signed 64-bit result can hold, split by sign:
    // LLONG_MAX for a positive value, |LLONG_MIN| (== LLONG_MAX + 1) for a negative one.
    private const ulong SignedPositiveMax = 0x7FFFFFFFFFFFFFFFUL; // LLONG_MAX
    private const ulong SignedNegativeMax = 0x8000000000000000UL; // -(LLONG_MIN)
    private const ulong UnsignedMax = 0xFFFFFFFFFFFFFFFFUL;       // ULLONG_MAX

    [SysAbiExport(
        Nid = "mXlxhmLNMPg",
        ExportName = "strtol",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strtol(CpuContext ctx) => ConvertSigned(ctx);

    [SysAbiExport(
        Nid = "VOBg+iNwB-4",
        ExportName = "strtoll",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strtoll(CpuContext ctx) => ConvertSigned(ctx);

    [SysAbiExport(
        Nid = "QxmSHBCuKTk",
        ExportName = "strtoul",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strtoul(CpuContext ctx) => ConvertUnsigned(ctx);

    [SysAbiExport(
        Nid = "5OqszGpy7Mg",
        ExportName = "strtoull",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strtoull(CpuContext ctx) => ConvertUnsigned(ctx);

    // long/long long are both 64-bit under the PS5 LP64 ABI, so strtol and strtoll
    // are identical, as are strtoul and strtoull.
    private static int ConvertSigned(CpuContext ctx)
    {
        var nptr = ctx[CpuRegister.Rdi];
        var endptr = ctx[CpuRegister.Rsi];
        var @base = unchecked((int)ctx[CpuRegister.Rdx]);

        var parsed = Parse(ctx, nptr, @base, signedRange: true, out var negative);

        ulong result;
        if (!parsed.HasError)
        {
            // parsed.Magnitude is bounded by the sign-specific cutoff, so the
            // two's-complement negation of the magnitude is the exact bit pattern.
            result = negative ? (~parsed.Magnitude + 1UL) : parsed.Magnitude;
        }
        else if (parsed.Overflow)
        {
            result = negative ? SignedNegativeMax : SignedPositiveMax;
            KernelRuntimeCompatExports.TrySetErrno(ctx, Erange);
        }
        else
        {
            result = 0;
            KernelRuntimeCompatExports.TrySetErrno(ctx, Einval);
        }

        WriteEndptr(ctx, endptr, nptr, parsed);
        ctx[CpuRegister.Rax] = result;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static int ConvertUnsigned(CpuContext ctx)
    {
        var nptr = ctx[CpuRegister.Rdi];
        var endptr = ctx[CpuRegister.Rsi];
        var @base = unchecked((int)ctx[CpuRegister.Rdx]);

        var parsed = Parse(ctx, nptr, @base, signedRange: false, out var negative);

        ulong result;
        if (!parsed.HasError)
        {
            // A leading '-' is accepted for the unsigned conversions; the standard
            // negates the (mod 2^64) result. On overflow the negation is skipped and
            // ULLONG_MAX is returned as-is, matching the BSD/glibc contract.
            result = negative ? (0UL - parsed.Magnitude) : parsed.Magnitude;
        }
        else if (parsed.Overflow)
        {
            result = UnsignedMax;
            KernelRuntimeCompatExports.TrySetErrno(ctx, Erange);
        }
        else
        {
            result = 0;
            KernelRuntimeCompatExports.TrySetErrno(ctx, Einval);
        }

        WriteEndptr(ctx, endptr, nptr, parsed);
        ctx[CpuRegister.Rax] = result;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private readonly struct ParseResult
    {
        public ulong Magnitude { get; init; }

        /// <summary>Offset from <c>nptr</c> to the first unconsumed character.</summary>
        public ulong EndOffset { get; init; }

        /// <summary>True once at least one value digit was consumed.</summary>
        public bool AnyDigits { get; init; }

        /// <summary>True when the accumulated magnitude exceeded the target range.</summary>
        public bool Overflow { get; init; }

        /// <summary>True when the base was invalid (no conversion is possible).</summary>
        public bool InvalidBase { get; init; }

        public bool HasError => Overflow || InvalidBase || !AnyDigits;
    }

    private static ParseResult Parse(CpuContext ctx, ulong nptr, int @base, bool signedRange, out bool negative)
    {
        negative = false;

        if (@base != 0 && (@base < 2 || @base > 36))
        {
            return new ParseResult { InvalidBase = true };
        }

        ulong pos = 0;

        // Leading whitespace.
        while (TryReadGuestByte(ctx, nptr, pos, out var ws) && IsSpace(ws))
        {
            pos++;
        }

        // Optional sign.
        if (TryReadGuestByte(ctx, nptr, pos, out var sign) && (sign == (byte)'+' || sign == (byte)'-'))
        {
            negative = sign == (byte)'-';
            pos++;
        }

        // Base detection and the "0x"/"0X" prefix. The prefix is only consumed when a
        // hex digit follows it; otherwise the leading '0' is a valid value digit and
        // scanning stops at the 'x' (classic strtol behaviour).
        var hasZero = TryReadGuestByte(ctx, nptr, pos, out var c0) && c0 == (byte)'0';
        if ((@base == 0 || @base == 16) && hasZero &&
            TryReadGuestByte(ctx, nptr, pos + 1, out var xchar) && (xchar == (byte)'x' || xchar == (byte)'X') &&
            TryReadGuestByte(ctx, nptr, pos + 2, out var xdigit) && DigitValue(xdigit) < 16)
        {
            @base = 16;
            pos += 2;
        }
        else if (@base == 0)
        {
            @base = hasZero ? 8 : 10;
        }

        var ubase = (ulong)@base;
        var maxMagnitude = signedRange ? (negative ? SignedNegativeMax : SignedPositiveMax) : UnsignedMax;
        var cutoff = maxMagnitude / ubase;
        var cutlim = maxMagnitude % ubase;

        ulong acc = 0;
        var anyDigits = false;
        var overflow = false;

        while (TryReadGuestByte(ctx, nptr, pos, out var b))
        {
            var digit = DigitValue(b);
            if (digit < 0 || (ulong)digit >= ubase)
            {
                break;
            }

            pos++;
            if (overflow)
            {
                // Keep consuming digits so endptr lands past the whole number, but
                // stop accumulating once the range has already been exceeded.
                anyDigits = true;
                continue;
            }

            if (acc > cutoff || (acc == cutoff && (ulong)digit > cutlim))
            {
                overflow = true;
                anyDigits = true;
                continue;
            }

            acc = (acc * ubase) + (ulong)digit;
            anyDigits = true;
        }

        return new ParseResult
        {
            Magnitude = acc,
            EndOffset = anyDigits ? pos : 0,
            AnyDigits = anyDigits,
            Overflow = overflow,
        };
    }

    private static void WriteEndptr(CpuContext ctx, ulong endptr, ulong nptr, ParseResult parsed)
    {
        if (endptr == 0)
        {
            return;
        }

        // On a successful conversion endptr points past the last digit; with no
        // conversion the C standard stores the original nptr.
        var value = parsed.AnyDigits ? nptr + parsed.EndOffset : nptr;
        ctx.TryWriteUInt64(endptr, value);
    }

    private static bool TryReadGuestByte(CpuContext ctx, ulong nptr, ulong offset, out byte value)
    {
        if (nptr == 0)
        {
            value = 0;
            return false;
        }

        return ctx.TryReadByte(nptr + offset, out value);
    }

    private static bool IsSpace(byte b) =>
        b == (byte)' ' || (b >= 0x09 && b <= 0x0D); // ' ', '\t', '\n', '\v', '\f', '\r'

    private static int DigitValue(byte b)
    {
        if (b >= (byte)'0' && b <= (byte)'9')
        {
            return b - (byte)'0';
        }

        if (b >= (byte)'a' && b <= (byte)'z')
        {
            return b - (byte)'a' + 10;
        }

        if (b >= (byte)'A' && b <= (byte)'Z')
        {
            return b - (byte)'A' + 10;
        }

        return -1;
    }
}
