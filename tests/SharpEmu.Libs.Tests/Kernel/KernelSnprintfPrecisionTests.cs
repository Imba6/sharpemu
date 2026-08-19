// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// snprintf integer precision: for d/i/u/x/o the precision is the minimum number of
// digits (zero-padded), independent of field width. DoomGeneric builds font lump
// names with "STCFN%.3d", so 33 must render "STCFN033", not "STCFN33".
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelSnprintfPrecisionTests
{
    private const ulong MemoryBase = 0xE_0000_0000;
    private const ulong DestAddress = MemoryBase + 0x40;
    private const ulong FormatAddress = MemoryBase + 0x200;

    private static string Snprintf(string format, params ulong[] args)
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        var fmt = Encoding.ASCII.GetBytes(format);
        Assert.True(memory.TryWrite(FormatAddress, fmt));
        Assert.True(memory.TryWrite(FormatAddress + (ulong)fmt.Length, new byte[] { 0 }));

        ctx[CpuRegister.Rdi] = DestAddress;
        ctx[CpuRegister.Rsi] = 256;
        ctx[CpuRegister.Rdx] = FormatAddress;
        var regs = new[] { CpuRegister.Rcx, CpuRegister.R8, CpuRegister.R9 };
        for (var i = 0; i < args.Length && i < regs.Length; i++)
        {
            ctx[regs[i]] = args[i];
        }

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Snprintf(ctx));

        var bytes = new List<byte>();
        for (var i = 0; i < 256; i++)
        {
            Assert.True(ctx.TryReadByte(DestAddress + (ulong)i, out var b));
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [Theory]
    [InlineData("STCFN%.3d", 33UL, "STCFN033")]      // the DoomGeneric font case
    [InlineData("%.3d", 5UL, "005")]
    [InlineData("%.3d", 1234UL, "1234")]             // more digits than precision: unchanged
    [InlineData("%.5d", 42UL, "00042")]
    [InlineData("%d", 33UL, "33")]                   // no precision: plain
    [InlineData("%.3x", 255UL, "0ff")]               // precision applies to hex too
    [InlineData("%.4o", 8UL, "0010")]                // and octal
    public void Snprintf_IntegerPrecision(string format, ulong arg, string expected)
    {
        Assert.Equal(expected, Snprintf(format, arg));
    }

    [Fact]
    public void Snprintf_PrecisionZeroWithZeroValue_IsEmpty()
    {
        // C: %.0d of 0 produces no characters.
        Assert.Equal("[]", Snprintf("[%.0d]", 0UL));
    }

    [Fact]
    public void Snprintf_WidthAndPrecision_PadsToWidthWithSpaces()
    {
        // Precision zero-pads digits; width pads the field with spaces (0 flag ignored
        // when precision given).
        Assert.Equal("  007", Snprintf("%5.3d", 7UL));
    }

    [Fact]
    public void Snprintf_NegativeWithPrecision_KeepsSign()
    {
        Assert.Equal("-007", Snprintf("%.3d", unchecked((ulong)(long)-7)));
    }
}
