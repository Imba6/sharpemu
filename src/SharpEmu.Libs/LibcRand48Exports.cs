// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcRand48;

/// <summary>
/// The libc <c>drand48</c> family of pseudo-random generators. They share a single
/// 48-bit state advanced by the linear congruential recurrence
/// <c>X = (a*X + c) mod 2^48</c> with the standard constants a = 0x5DEECE66D, c = 0xB.
/// srand48 seeds the state; lrand48 draws a non-negative 31-bit value. The state is
/// process-global and not thread-safe in the C library; a lock keeps the host safe.
/// </summary>
public static class LibcRand48Exports
{
    private const ulong Multiplier = 0x5DEECE66DUL;
    private const ulong Addend = 0xBUL;
    private const ulong Mask48 = 0xFFFF_FFFF_FFFFUL;

    // The low 16 bits srand48 forces into the state (SEED_LO in the C library).
    private const ulong SeedLow = 0x330EUL;

    private static readonly object _gate = new();

    // Default seed the C library starts from before srand48 is called:
    // {0x330E, 0xABCD, 0x1234} -> X = 0x1234ABCD330E.
    private static ulong _state = 0x1234_ABCD_330EUL;

    [SysAbiExport(
        Nid = "+KSnjvZ0NMc",
        ExportName = "srand48",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Srand48(CpuContext ctx)
    {
        // Only the low 32 bits of the seed are used: X = (seed << 16) | 0x330E.
        var seed = unchecked((uint)ctx[CpuRegister.Rdi]);
        lock (_gate)
        {
            _state = (((ulong)seed << 16) | SeedLow) & Mask48;
        }

        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "5IpoNfxu84U",
        ExportName = "lrand48",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Lrand48(CpuContext ctx)
    {
        ulong next;
        lock (_gate)
        {
            _state = ((Multiplier * _state) + Addend) & Mask48;
            next = _state;
        }

        // lrand48 returns the top 31 bits of the 48-bit state, a value in [0, 2^31).
        ctx[CpuRegister.Rax] = (next >> 17) & 0x7FFF_FFFFUL;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
