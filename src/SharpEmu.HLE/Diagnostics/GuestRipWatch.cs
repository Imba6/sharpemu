// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;

namespace SharpEmu.HLE.Diagnostics;

// Pure helpers for the generic guest-RIP watch diagnostic (Stage 14d).
//
// The watch fires at an import-call RETURN RIP (the direct-execution backend has
// the full guest register file + memory at every import boundary, so no
// breakpoint machinery is needed). It optionally reads a small, caller-specified
// set of memory operands of the form "reg[+/-disp][:size]" so a stall condition
// can be inspected as data (e.g. "r13+0x118:4,r13+0x170:8"). Nothing here reads
// memory or touches the guest; it only parses the spec and formats values.
//
// No title-specific knowledge; the RIP and operands are entirely configuration.

/// <summary>
/// One parsed memory operand to sample. Direct: value_at([Reg]+Disp), Size bytes.
/// Indirect ("[reg+disp]+disp2:size"): first read an 8-byte pointer at Reg+Disp,
/// then read Size bytes at pointer+Disp2 — for chasing one pointer (e.g. a work
/// item held on the stack at [rbp-0x18]) to its fields.
/// </summary>
public readonly record struct RipWatchOperand(string Reg, long Disp, int Size, bool Indirect = false, long Disp2 = 0);

public static class GuestRipWatch
{
    /// <summary>
    /// Parse a "reg+disp:size,reg-disp:size,reg,reg:size" spec into operands.
    /// Whitespace-tolerant. Unknown/garbage entries are skipped. Size defaults to
    /// 8 and is clamped to {1,2,4,8}; disp defaults to 0. Registers are
    /// lower-cased and validated against the x86-64 GPR set.
    /// </summary>
    public static IReadOnlyList<RipWatchOperand> ParseOperands(string? spec)
    {
        var result = new List<RipWatchOperand>();
        if (string.IsNullOrWhiteSpace(spec))
        {
            return result;
        }

        foreach (var rawItem in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var item = rawItem;
            int size = 8;
            int colon = item.LastIndexOf(':');
            if (colon >= 0)
            {
                var sizePart = item[(colon + 1)..].Trim();
                item = item[..colon];
                if (int.TryParse(sizePart, out var s) && s is 1 or 2 or 4 or 8)
                {
                    size = s;
                }
            }

            // Indirect form: "[reg+disp]+disp2"
            if (item.StartsWith('['))
            {
                int close = item.IndexOf(']');
                if (close < 0)
                {
                    continue;
                }
                var inner = item[1..close].Trim();
                var outer = item[(close + 1)..].Trim();
                if (TryParseRegDisp(inner, out var breg, out var bdisp) && IsGpr(breg))
                {
                    long disp2 = 0;
                    if (outer.Length > 0)
                    {
                        _ = TryParseSignedDisp(outer, out disp2);
                    }
                    result.Add(new RipWatchOperand(breg, bdisp, size, Indirect: true, Disp2: disp2));
                }
                continue;
            }

            if (TryParseRegDisp(item.Trim(), out var reg, out var disp) && IsGpr(reg))
            {
                result.Add(new RipWatchOperand(reg, disp, size));
            }
        }

        return result;
    }

    private static bool TryParseRegDisp(string s, out string reg, out long disp)
    {
        disp = 0;
        reg = s.Trim();
        int sign = s.IndexOfAny(new[] { '+', '-' });
        if (sign > 0)
        {
            reg = s[..sign].Trim();
            _ = TryParseSignedDisp(s[sign..].Trim(), out disp);
        }
        reg = reg.ToLowerInvariant();
        return reg.Length > 0;
    }

    private static bool TryParseSignedDisp(string s, out long disp)
    {
        disp = 0;
        s = s.Trim();
        if (s.Length == 0)
        {
            return false;
        }
        bool negative = s[0] == '-';
        var digits = (s[0] is '+' or '-') ? s[1..].Trim() : s;
        if (TryParseIntLiteral(digits, out var d))
        {
            disp = negative ? -(long)d : (long)d;
            return true;
        }
        return false;
    }

    /// <summary>Parse a RIP literal ("0x..." or decimal). Returns 0 (disabled) on failure.</summary>
    public static ulong ParseRip(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        return TryParseIntLiteral(value.Trim(), out var v) ? v : 0UL;
    }

    /// <summary>
    /// Parse a comma-separated set of RIP literals (e.g. "0x800D18129,0x800D17D53").
    /// Zero/garbage entries are dropped. Empty result = watch disabled.
    /// </summary>
    public static IReadOnlySet<ulong> ParseRipSet(string? value)
    {
        var set = new HashSet<ulong>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return set;
        }

        foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseIntLiteral(item, out var v) && v != 0)
            {
                set.Add(v);
            }
        }

        return set;
    }

    private static bool TryParseIntLiteral(string s, out ulong value)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value);
        }

        return ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private static readonly HashSet<string> _gprs = new(StringComparer.Ordinal)
    {
        "rax","rbx","rcx","rdx","rsi","rdi","rbp","rsp",
        "r8","r9","r10","r11","r12","r13","r14","r15",
    };

    public static bool IsGpr(string reg) => _gprs.Contains(reg);
}
