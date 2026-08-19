// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.LibcString;

/// <summary>
/// libc string helpers that tokenize or scan guest strings in place.
/// </summary>
public static class LibcStringExports
{
    // Guards against runaway scans over an unterminated or hostile guest string.
    private const int MaxScan = 1 << 20;
    private const int MaxDelim = 256;

    [SysAbiExport(
        Nid = "enqPGLfmVNU",
        ExportName = "strtok_r",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int StrtokR(CpuContext ctx)
    {
        var strAddress = ctx[CpuRegister.Rdi];
        var delimAddress = ctx[CpuRegister.Rsi];
        var saveAddress = ctx[CpuRegister.Rdx];

        // saveptr is mandatory; delim must be readable. A missing one yields no token.
        if (saveAddress == 0 || !TryReadDelimiterSet(ctx, delimAddress, out var isDelim))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // First call passes the string; later calls pass NULL and resume from saveptr.
        var cursor = strAddress;
        if (cursor == 0 && !ctx.TryReadUInt64(saveAddress, out cursor))
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if (cursor == 0)
        {
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        // Skip leading delimiters.
        var scanned = 0;
        while (scanned < MaxScan && ctx.TryReadByte(cursor, out var b) && b != 0 && isDelim[b])
        {
            cursor++;
            scanned++;
        }

        // At end of string (or unreadable): no more tokens. Point saveptr at the end.
        if (!ctx.TryReadByte(cursor, out var head) || head == 0)
        {
            ctx.TryWriteUInt64(saveAddress, cursor);
            ctx[CpuRegister.Rax] = 0;
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var tokenStart = cursor;

        // Scan to the next delimiter or the terminator.
        while (scanned < MaxScan && ctx.TryReadByte(cursor, out var b) && b != 0 && !isDelim[b])
        {
            cursor++;
            scanned++;
        }

        if (ctx.TryReadByte(cursor, out var terminatorOrDelim) && terminatorOrDelim != 0)
        {
            // Terminate the token in place and resume after the delimiter next time.
            var wroteTerminator = ctx.Memory.TryWrite(cursor, stackalloc byte[] { 0 });
            ctx.TryWriteUInt64(saveAddress, cursor + 1);
            if (!wroteTerminator)
            {
                // Could not null-terminate: fail closed rather than hand back an
                // unterminated token that runs into the next one.
                ctx[CpuRegister.Rax] = 0;
                return (int)OrbisGen2Result.ORBIS_GEN2_OK;
            }
        }
        else
        {
            // Hit the string terminator; the next call will return NULL.
            ctx.TryWriteUInt64(saveAddress, cursor);
        }

        ctx[CpuRegister.Rax] = tokenStart;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    private static bool TryReadDelimiterSet(CpuContext ctx, ulong delimAddress, out bool[] isDelim)
    {
        isDelim = new bool[256];
        if (delimAddress == 0)
        {
            return false;
        }

        for (var i = 0; i < MaxDelim; i++)
        {
            if (!ctx.TryReadByte(delimAddress + (ulong)i, out var b))
            {
                return false;
            }

            if (b == 0)
            {
                return true;
            }

            isDelim[b] = true;
        }

        // No terminator within the bound: treat the delimiter string as malformed.
        return false;
    }
}
