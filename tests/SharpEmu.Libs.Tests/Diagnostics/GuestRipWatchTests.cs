// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Linq;
using SharpEmu.HLE.Diagnostics;
using Xunit;

namespace SharpEmu.Libs.Tests.Diagnostics;

// Unit tests for the generic guest-RIP-watch spec parsing (Stage 14d). The
// register/memory sampling itself runs against a live CpuContext at an import
// boundary and is exercised on Windows captures; this pins the pure parsing.
public sealed class GuestRipWatchTests
{
    [Theory]
    [InlineData("0x800D18129", 0x800D18129UL)]
    [InlineData("0x800d18129", 0x800D18129UL)]
    [InlineData("34411053353", 34411053353UL)] // decimal form of 0x800D18129
    [InlineData("", 0UL)]
    [InlineData("garbage", 0UL)]
    public void ParseRip(string input, ulong expected)
    {
        Assert.Equal(expected, GuestRipWatch.ParseRip(input));
    }

    [Fact]
    public void ParseRipSet_MultipleSites()
    {
        // producer signal 0x86, producer wait 0x84, consumer wait 0x86, consumer ack 0x84
        var set = GuestRipWatch.ParseRipSet("0x800D17D53,0x800D17CFB,0x800D18129,0x800D1379F");
        Assert.Equal(4, set.Count);
        Assert.Contains(0x800D17D53UL, set);
        Assert.Contains(0x800D1379FUL, set);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("0x800D18129", 1)]
    [InlineData("0, garbage, 0x800D18129", 1)]   // zero and garbage dropped
    public void ParseRipSet_DropsEmptyAndBad(string input, int expectedCount)
    {
        Assert.Equal(expectedCount, GuestRipWatch.ParseRipSet(input).Count);
    }

    [Fact]
    public void ParseOperands_TheCocoonConditionSpec()
    {
        // The r13 object fields the 0x86 acquire loop consults (from the disasm):
        // count @0x118 (u32), handle @0x120, and the exit-state fields @0x170/@0x190.
        var ops = GuestRipWatch.ParseOperands("r13+0x118:4,r13+0x120:8,r13+0x170:8,r13+0x190:8,r13+0x34:4");
        Assert.Equal(5, ops.Count);
        Assert.Equal(new RipWatchOperand("r13", 0x118, 4), ops[0]);
        Assert.Equal(new RipWatchOperand("r13", 0x120, 8), ops[1]);
        Assert.Equal(new RipWatchOperand("r13", 0x34, 4), ops[4]);
    }

    [Fact]
    public void ParseOperands_DefaultsAndNegativeDisp()
    {
        var ops = GuestRipWatch.ParseOperands("rbp-0x34:4, r13 , rsp+0x8");
        Assert.Equal(3, ops.Count);
        Assert.Equal(new RipWatchOperand("rbp", -0x34, 4), ops[0]);
        Assert.Equal(new RipWatchOperand("r13", 0, 8), ops[1]);   // default size 8, disp 0
        Assert.Equal(new RipWatchOperand("rsp", 0x8, 8), ops[2]);
    }

    [Fact]
    public void ParseOperands_ClampsBadSizeToDefaultAndSkipsBadRegs()
    {
        var ops = GuestRipWatch.ParseOperands("r13+0x10:3, notareg+0x8:4, xmm0:8, r14:2");
        // size 3 is invalid -> defaults to 8; notareg/xmm0 are not GPRs -> skipped.
        Assert.Equal(2, ops.Count);
        Assert.Equal(new RipWatchOperand("r13", 0x10, 8), ops[0]);
        Assert.Equal(new RipWatchOperand("r14", 0, 2), ops[1]);
    }

    [Fact]
    public void ParseOperands_EmptyOrNull()
    {
        Assert.Empty(GuestRipWatch.ParseOperands(null));
        Assert.Empty(GuestRipWatch.ParseOperands("   "));
    }

    [Theory]
    [InlineData("r13", true)]
    [InlineData("r15", true)]
    [InlineData("rax", true)]
    [InlineData("rip", false)]
    [InlineData("eax", false)]
    public void IsGpr(string reg, bool expected)
    {
        Assert.Equal(expected, GuestRipWatch.IsGpr(reg));
    }
}
