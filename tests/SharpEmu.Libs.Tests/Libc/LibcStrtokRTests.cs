// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

using SharpEmu.HLE;
using SharpEmu.Libs.LibcString;

using Xunit;

namespace SharpEmu.Libs.Tests.Libc;

// strtok_r is the reentrant tokenizer: it splits a string in place on any of the
// delimiter characters, storing its resume position through saveptr.
public sealed class LibcStrtokRTests
{
    private const ulong MemoryBase = 0xB_0000_0000;
    private const ulong StrAddress = MemoryBase + 0x100;
    private const ulong DelimAddress = MemoryBase + 0x300;
    private const ulong SaveAddress = MemoryBase + 0x340;

    private static void PutCString(FakeCpuMemory memory, ulong address, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        Assert.True(memory.TryWrite(address, bytes));
        Assert.True(memory.TryWrite(address + (ulong)bytes.Length, new byte[] { 0 }));
    }

    private static string ReadCString(CpuContext ctx, ulong address)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < 4096; i++)
        {
            Assert.True(ctx.TryReadByte(address + (ulong)i, out var b));
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    private static List<string> Tokenize(string input, string delim)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        PutCString(memory, StrAddress, input);
        PutCString(memory, DelimAddress, delim);
        Assert.True(memory.TryWrite(SaveAddress, new byte[8])); // saveptr = 0

        var tokens = new List<string>();
        var first = StrAddress;
        for (var guard = 0; guard < 1000; guard++)
        {
            ctx[CpuRegister.Rdi] = first;
            ctx[CpuRegister.Rsi] = DelimAddress;
            ctx[CpuRegister.Rdx] = SaveAddress;
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStringExports.StrtokR(ctx));

            var token = ctx[CpuRegister.Rax];
            if (token == 0)
            {
                break;
            }

            tokens.Add(ReadCString(ctx, token));
            first = 0; // subsequent calls resume from saveptr
        }

        return tokens;
    }

    [Fact]
    public void SplitsOnSingleDelimiter() =>
        Assert.Equal(new[] { "a", "b", "c" }, Tokenize("a,b,c", ","));

    [Fact]
    public void SkipsLeadingTrailingAndRepeatedDelimiters() =>
        Assert.Equal(new[] { "hello", "world" }, Tokenize("  hello   world  ", " "));

    [Fact]
    public void CollapsesRepeatedDelimiters() =>
        Assert.Equal(new[] { "a", "b" }, Tokenize(",,a,,b,,", ","));

    [Fact]
    public void MultipleDelimiterCharacters() =>
        Assert.Equal(new[] { "a", "b", "c" }, Tokenize("a-b_c", "-_"));

    [Fact]
    public void EmptyString_NoTokens() =>
        Assert.Empty(Tokenize("", ","));

    [Fact]
    public void AllDelimiters_NoTokens() =>
        Assert.Empty(Tokenize(",,,,", ","));

    [Fact]
    public void NoDelimiterInString_SingleToken() =>
        Assert.Equal(new[] { "abcdef" }, Tokenize("abcdef", ","));

    [Fact]
    public void NullSaveptr_ReturnsNull()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);
        PutCString(memory, StrAddress, "a,b");
        PutCString(memory, DelimAddress, ",");
        ctx[CpuRegister.Rdi] = StrAddress;
        ctx[CpuRegister.Rsi] = DelimAddress;
        ctx[CpuRegister.Rdx] = 0; // null saveptr

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStringExports.StrtokR(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }
}
