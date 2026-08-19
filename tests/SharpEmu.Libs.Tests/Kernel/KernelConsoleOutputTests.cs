// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using SharpEmu.Libs.LibcStdio;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// putc/putchar/puts are libc console output. When the FILE* is not one of the
// HLE's own fopen handles (i.e. the bundled libc's stdout/stderr), the bytes go
// to the host console. DoomGeneric reaches these through its startup banner and
// printf path. These tests pin the return values and the stdout text.
public sealed class KernelConsoleOutputTests
{
    private const ulong MemoryBase = 0xF_0000_0000;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static string CaptureStdout(Action body)
    {
        var previous = Console.Out;
        var writer = new StringWriter();
        Console.SetOut(writer);
        try
        {
            body();
        }
        finally
        {
            Console.SetOut(previous);
        }

        return writer.ToString();
    }

    [Fact]
    public void Putchar_WritesByteToStdoutAndReturnsIt()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = (byte)'Q';

        var output = CaptureStdout(() =>
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Putchar(ctx)));

        Assert.Contains("Q", output);
        Assert.Equal((ulong)'Q', ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Putc_UnknownStream_WritesToStdoutAndReturnsChar()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = (byte)'z';
        ctx[CpuRegister.Rsi] = 0xDEAD_BEEF; // not an HLE fopen handle -> stdout

        var output = CaptureStdout(() =>
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Putc(ctx)));

        Assert.Contains("z", output);
        Assert.Equal((ulong)'z', ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Puts_WritesStringWithNewlineAndReturnsNonNegative()
    {
        var ctx = NewContext(out var memory);
        memory.WriteCString(MemoryBase, "DOOM");
        ctx[CpuRegister.Rdi] = MemoryBase;

        var output = CaptureStdout(() =>
            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStdioExports.Puts(ctx)));

        Assert.Contains("DOOM\n", output);
        // Non-negative on success; we report characters written (string + newline).
        Assert.Equal(5UL, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Puts_NullPointer_ReturnsFault()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 0;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            LibcStdioExports.Puts(ctx));
        Assert.Equal(unchecked((ulong)(-1L)), ctx[CpuRegister.Rax]);
    }
}
