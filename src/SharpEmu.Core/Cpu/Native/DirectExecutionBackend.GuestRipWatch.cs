// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using SharpEmu.HLE;
using SharpEmu.HLE.Diagnostics;

namespace SharpEmu.Core.Cpu.Native;

// Generic guest-RIP watch (Stage 14d, diagnostics only).
//
//   SHARPEMU_DIAG_GUEST_RIP_WATCH=<guest RIP>        # e.g. 0x800D18129
//   SHARPEMU_DIAG_GUEST_RIP_WATCH_COUNT=<N>          # max emissions (default 256)
//   SHARPEMU_DIAG_GUEST_RIP_WATCH_MEMORY=r13+0x118:4,r13+0x170:8,...   # optional operands
//
// Fires when an import is dispatched whose RETURN address into the guest equals
// the watched RIP (the direct-execution backend exposes the full guest register
// file + memory at that boundary — no breakpoint machinery). Logs the register
// file and any requested memory operands as data. Fully inert when unset; reads
// only; never throws into guest execution. No title-specific values are baked in.
public sealed unsafe partial class DirectExecutionBackend
{
    // Accepts one RIP or a comma-separated set (e.g. producer signal + consumer
    // wait + ack) so a single capture correlates all sites of a handshake.
    private static readonly IReadOnlySet<ulong> _diagGuestRipWatchSet =
        GuestRipWatch.ParseRipSet(Environment.GetEnvironmentVariable("SHARPEMU_DIAG_GUEST_RIP_WATCH"));

    private static readonly bool _diagGuestRipWatchEnabled = _diagGuestRipWatchSet.Count != 0;

    private static readonly int _diagGuestRipWatchLimit = ParseGuestRipWatchLimit();

    private static readonly IReadOnlyList<RipWatchOperand> _diagGuestRipWatchOperands =
        GuestRipWatch.ParseOperands(Environment.GetEnvironmentVariable("SHARPEMU_DIAG_GUEST_RIP_WATCH_MEMORY"));

    private int _diagGuestRipWatchCount;

    private static int ParseGuestRipWatchLimit()
    {
        return int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_DIAG_GUEST_RIP_WATCH_COUNT"),
            NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            ? n
            : 256;
    }

    // One-shot startup confirmation so a capture is self-verifying: if this line
    // is absent while the env var was set, the running binary predates the watch.
    private void LogGuestRipWatchArmed()
    {
        if (!_diagGuestRipWatchEnabled)
        {
            return;
        }

        var rips = string.Join(",", System.Linq.Enumerable.Select(_diagGuestRipWatchSet, r => $"0x{r:X}"));
        var ops = string.Join(",", System.Linq.Enumerable.Select(_diagGuestRipWatchOperands,
            o => $"{o.Reg}{(o.Disp >= 0 ? "+" : "-")}0x{Math.Abs(o.Disp):X}:{o.Size}"));
        Console.Error.WriteLine(
            $"[LOADER][DIAG] guest_rip_watch armed rip=[{rips}] " +
            $"limit={_diagGuestRipWatchLimit} operands=[{ops}]");
    }

    private void MaybeEmitGuestRipWatch(CpuContext ctx, ulong returnRip, string nid)
    {
        if (!_diagGuestRipWatchEnabled || !_diagGuestRipWatchSet.Contains(returnRip))
        {
            return;
        }
        if (Interlocked.Increment(ref _diagGuestRipWatchCount) > _diagGuestRipWatchLimit)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder(256);
            sb.Append("[LOADER][DIAG] guest_rip_watch seq=").Append(_diagGuestRipWatchCount)
              .Append(" rip=0x").Append(returnRip.ToString("X", CultureInfo.InvariantCulture))
              .Append(" nid=").Append(nid)
              .Append(" thread=0x").Append(GuestThreadExecution.CurrentGuestThreadHandle.ToString("X", CultureInfo.InvariantCulture));
            AppendReg(sb, "rdi", ctx, CpuRegister.Rdi);
            AppendReg(sb, "rsi", ctx, CpuRegister.Rsi);
            AppendReg(sb, "rdx", ctx, CpuRegister.Rdx);
            AppendReg(sb, "rcx", ctx, CpuRegister.Rcx);
            AppendReg(sb, "r8", ctx, CpuRegister.R8);
            AppendReg(sb, "r9", ctx, CpuRegister.R9);
            AppendReg(sb, "rbx", ctx, CpuRegister.Rbx);
            AppendReg(sb, "rbp", ctx, CpuRegister.Rbp);
            AppendReg(sb, "rsp", ctx, CpuRegister.Rsp);
            AppendReg(sb, "r12", ctx, CpuRegister.R12);
            AppendReg(sb, "r13", ctx, CpuRegister.R13);
            AppendReg(sb, "r14", ctx, CpuRegister.R14);
            AppendReg(sb, "r15", ctx, CpuRegister.R15);

            foreach (var op in _diagGuestRipWatchOperands)
            {
                ulong baseVal = ReadNamedRegister(ctx, op.Reg);
                ulong baseEa = unchecked(baseVal + (ulong)op.Disp);
                sb.Append(op.Indirect ? " [[" : " [").Append(op.Reg);
                AppendDisp(sb, op.Disp);
                if (op.Indirect)
                {
                    // Chase one pointer: read ptr at baseEa, then read at ptr+Disp2.
                    sb.Append(']');
                    AppendDisp(sb, op.Disp2);
                    sb.Append(':').Append(op.Size).Append("]");
                    if (!TryReadGuestOperand(ctx, baseEa, 8, out var ptr))
                    {
                        sb.Append("ptr@0x").Append(baseEa.ToString("X", CultureInfo.InvariantCulture)).Append("=<unreadable>");
                        continue;
                    }
                    ulong ea = unchecked(ptr + (ulong)op.Disp2);
                    sb.Append("ptr=0x").Append(ptr.ToString("X", CultureInfo.InvariantCulture))
                      .Append(" ea=0x").Append(ea.ToString("X", CultureInfo.InvariantCulture));
                    sb.Append(TryReadGuestOperand(ctx, ea, op.Size, out var iv)
                        ? "=0x" + iv.ToString("X", CultureInfo.InvariantCulture) : "=<unreadable>");
                }
                else
                {
                    sb.Append(':').Append(op.Size).Append("]ea=0x").Append(baseEa.ToString("X", CultureInfo.InvariantCulture));
                    sb.Append(TryReadGuestOperand(ctx, baseEa, op.Size, out var value)
                        ? "=0x" + value.ToString("X", CultureInfo.InvariantCulture) : "=<unreadable>");
                }
            }

            Console.Error.WriteLine(sb.ToString());
        }
        catch (Exception ex)
        {
            // Diagnostics must never disturb guest execution.
            Console.Error.WriteLine($"[LOADER][DIAG] guest_rip_watch.error {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void AppendDisp(StringBuilder sb, long disp)
    {
        if (disp >= 0)
        {
            sb.Append("+0x").Append(disp.ToString("X", CultureInfo.InvariantCulture));
        }
        else
        {
            sb.Append("-0x").Append((-disp).ToString("X", CultureInfo.InvariantCulture));
        }
    }

    private static void AppendReg(StringBuilder sb, string name, CpuContext ctx, CpuRegister reg)
    {
        sb.Append(' ').Append(name).Append("=0x").Append(ctx[reg].ToString("X", CultureInfo.InvariantCulture));
    }

    private static ulong ReadNamedRegister(CpuContext ctx, string reg) => reg switch
    {
        "rax" => ctx[CpuRegister.Rax],
        "rbx" => ctx[CpuRegister.Rbx],
        "rcx" => ctx[CpuRegister.Rcx],
        "rdx" => ctx[CpuRegister.Rdx],
        "rsi" => ctx[CpuRegister.Rsi],
        "rdi" => ctx[CpuRegister.Rdi],
        "rbp" => ctx[CpuRegister.Rbp],
        "rsp" => ctx[CpuRegister.Rsp],
        "r8" => ctx[CpuRegister.R8],
        "r9" => ctx[CpuRegister.R9],
        "r10" => ctx[CpuRegister.R10],
        "r11" => ctx[CpuRegister.R11],
        "r12" => ctx[CpuRegister.R12],
        "r13" => ctx[CpuRegister.R13],
        "r14" => ctx[CpuRegister.R14],
        "r15" => ctx[CpuRegister.R15],
        _ => 0,
    };

    private static bool TryReadGuestOperand(CpuContext ctx, ulong ea, int size, out ulong value)
    {
        value = 0;
        switch (size)
        {
            case 8:
                return ctx.TryReadUInt64(ea, out value);
            case 4:
                if (ctx.TryReadUInt32(ea, out var v32)) { value = v32; return true; }
                return false;
            case 2:
                if (ctx.TryReadUInt16(ea, out var v16)) { value = v16; return true; }
                return false;
            default: // 1 byte
                if (ctx.TryReadUInt32(ea, out var vb)) { value = vb & 0xFF; return true; }
                return false;
        }
    }
}
