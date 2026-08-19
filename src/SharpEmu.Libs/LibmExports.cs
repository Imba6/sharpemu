// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.Libm;

/// <summary>
/// libc math functions. Scalar double routines take their argument in XMM0's low
/// 64 bits and return in XMM0's low 64 bits, per the System V AMD64 ABI; the import
/// dispatcher loads the caller's XMM registers into the context and stores XMM0/XMM1
/// back as the return.
/// </summary>
public static class LibmExports
{
    private const int Xmm0 = 0;

    [SysAbiExport(
        Nid = "gacfOmO8hNs",
        ExportName = "ceil",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Ceil(CpuContext ctx)
    {
        var x = ReadDoubleArg(ctx);
        // Math.Ceiling matches C ceil for every case: NaN -> NaN, +/-inf preserved,
        // and inputs in (-1, 0] return -0.0 like the C library.
        WriteDoubleResult(ctx, Math.Ceiling(x));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "rtV7-jWC6Yg",
        ExportName = "log",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Log(CpuContext ctx)
    {
        var x = ReadDoubleArg(ctx);
        // Math.Log matches C log: log(1)=0, log(0)=-inf (pole), log(x<0)=NaN
        // (domain error), log(+inf)=+inf, log(NaN)=NaN.
        WriteDoubleResult(ctx, Math.Log(x));
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static double ReadDoubleArg(CpuContext ctx)
    {
        ctx.GetXmmRegister(Xmm0, out var low, out _);
        return BitConverter.Int64BitsToDouble(unchecked((long)low));
    }

    private static void WriteDoubleResult(CpuContext ctx, double value)
    {
        var bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(value));
        ctx.SetXmmRegister(Xmm0, bits, 0);
    }
}
