// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Threading;
using SharpEmu.Core.Cpu;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Guest raw-syscall support: a reusable gateway that behaves like the shared
/// <c>syscall</c> instruction inside libkernel, and the libkernel-shaped veneer
/// that exposes it at the +0x0A offset callers expect.
/// </summary>
/// <remarks>
/// PS5 CRTs — the ps5-payload-sdk one among them — do not call a syscall
/// wrapper to issue a syscall. They resolve any libkernel wrapper, skip its
/// 10-byte prologue, and keep the resulting address as a general-purpose
/// <c>syscall</c> entry:
///
/// <code>
///   49 89 CA              mov r10, rcx          ; +0x00 wrapper entry
///   48 C7 C0 nn 00 00 00  mov rax, &lt;number&gt;
///   0F 05                 syscall               ; +0x0A shared raw entry
/// </code>
///
/// Guest code executes natively here, so a literal <c>syscall</c> would enter
/// the *host* kernel with a guest number — never correct, frequently fatal. The
/// gateway is therefore an ordinary import trampoline: it marshals into managed
/// code exactly like an HLE import, and the number in RAX selects a
/// <see cref="GuestSyscallTable"/> row instead of a NID selecting an export.
/// Nothing about it is tied to which wrapper the guest happened to resolve.
/// </remarks>
public sealed unsafe partial class DirectExecutionBackend
{
	// mov r10,rcx / mov rax,imm32 / jmp [rip+...] / pointer slots.
	private const int SyscallVeneerSize = 48;

	private const uint SyscallVeneerPageSize = 4096;

	private readonly object _rawSyscallGate = new();

	private ulong _rawSyscallGatewayAddress;

	private nint _syscallVeneerPage;

	private uint _syscallVeneerPageOffset;

	private long _unsupportedSyscallReports;

	/// <summary>
	/// The shared raw-syscall entry. Callable from guest code with the FreeBSD
	/// register ABI (number in RAX; arguments in RDI, RSI, RDX, R10, R8, R9).
	/// </summary>
	private bool TryGetRawSyscallGateway(out ulong address)
	{
		address = Volatile.Read(ref _rawSyscallGatewayAddress);
		if (address != 0)
		{
			return true;
		}

		lock (_rawSyscallGate)
		{
			address = _rawSyscallGatewayAddress;
			if (address != 0)
			{
				return true;
			}

			// A reserved import entry, so the gateway reuses the whole existing
			// marshalling path: host stack switch, volatile-register capture,
			// guest-thread bookkeeping and exception safe-points.  Only the
			// epilogue differs, to carry CF back out.
			if (!TryAppendImportEntry(RuntimeStubNids.RawSyscallGateway, export: null, rawSyscallCarry: true, out var trampoline))
			{
				return false;
			}

			address = (ulong)trampoline;
			Volatile.Write(ref _rawSyscallGatewayAddress, address);
			Console.Error.WriteLine($"[LOADER][INFO] Raw syscall gateway: 0x{address:X16}");
			return true;
		}
	}

	/// <summary>
	/// Builds a libkernel-shaped veneer for one syscall wrapper export.
	/// </summary>
	/// <remarks>
	/// Layout, matching the offsets a PS5 CRT relies on:
	/// <code>
	///   +0x00  jmp [rip+0x12]  -> the export's own HLE trampoline (public ABI)
	///   +0x0A  jmp [rip+0x10]  -> the raw syscall gateway         (syscall ABI)
	///   +0x18  qword  HLE trampoline
	///   +0x20  qword  raw syscall gateway
	/// </code>
	/// The two ABIs stay separate: calling the export normally still goes
	/// through its HLE dispatch untouched, and only the +0x0A entry is a
	/// syscall. Neither entry encodes the export's own syscall number, because
	/// the raw entry is generic — the caller supplies the number in RAX, which
	/// is exactly what makes one wrapper usable for every syscall.
	/// </remarks>
	private bool TryCreateSyscallVeneer(nint hleTrampoline, out ulong address)
	{
		address = 0;
		if (hleTrampoline == 0 || !TryGetRawSyscallGateway(out var gateway))
		{
			return false;
		}

		var slot = AllocateSyscallVeneerSlot();
		if (slot == 0)
		{
			return false;
		}

		var code = (byte*)slot;
		for (var index = 0; index < SyscallVeneerSize; index++)
		{
			// int3 fill: any entry offset we did not intend must trap loudly
			// rather than run into the pointer table.
			code[index] = 0xCC;
		}

		// +0x00: jmp qword ptr [rip+0x12]  -> +0x18
		code[0] = 0xFF;
		code[1] = 0x25;
		*(int*)(code + 2) = 0x12;

		// +0x0A: jmp qword ptr [rip+0x10]  -> +0x20
		code[0x0A] = 0xFF;
		code[0x0B] = 0x25;
		*(int*)(code + 0x0C) = 0x10;

		*(ulong*)(code + 0x18) = (ulong)hleTrampoline;
		*(ulong*)(code + 0x20) = gateway;

		FlushInstructionCache(GetCurrentProcess(), code, SyscallVeneerSize);
		address = (ulong)slot;
		return true;
	}

	private nint AllocateSyscallVeneerSlot()
	{
		lock (_rawSyscallGate)
		{
			if (_syscallVeneerPage == 0 ||
				_syscallVeneerPageOffset + SyscallVeneerSize > SyscallVeneerPageSize)
			{
				var page = VirtualAlloc(null, SyscallVeneerPageSize, 12288u, 64u);
				if (page == null)
				{
					return 0;
				}

				lock (_importHandlerTrampolineGate)
				{
					_importHandlerTrampolines.Add((nint)page);
				}

				_syscallVeneerPage = (nint)page;
				_syscallVeneerPageOffset = 0;
			}

			var slot = _syscallVeneerPage + (nint)_syscallVeneerPageOffset;
			_syscallVeneerPageOffset += SyscallVeneerSize;
			return slot;
		}
	}

	private void ResetRawSyscallState()
	{
		lock (_rawSyscallGate)
		{
			_rawSyscallGatewayAddress = 0;
			_syscallVeneerPage = 0;
			_syscallVeneerPageOffset = 0;
		}
	}

	/// <summary>
	/// Managed side of the gateway: FreeBSD syscall ABI in, FreeBSD syscall ABI
	/// out.
	/// </summary>
	/// <remarks>
	/// In:  RAX = number, arguments in RDI, RSI, RDX, R10, R8, R9.
	/// Out: CF clear and RAX = value on success; CF set and RAX = errno on
	///      failure. RCX and R11 are the caller's to lose, as with a real
	///      <c>syscall</c>.
	///
	/// The HLE exports behind the table use the SysV C ABI, whose fourth
	/// argument is RCX rather than R10, so the one register the two ABIs
	/// disagree on is copied across before dispatch.
	/// </remarks>
	private OrbisGen2Result DispatchRawSyscall(nint argPackPtr)
	{
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null)
		{
			return OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT;
		}

		var number = unchecked((int)cpuContext[CpuRegister.Rax]);
		if (!GuestSyscallTable.TryGetByNumber(number, out var descriptor) ||
			!_moduleManager.TryGetExport(descriptor.Nid, out var export) ||
			(export.Target & cpuContext.TargetGeneration) == 0)
		{
			ReportUnsupportedSyscall(cpuContext, number);
			CompleteRawSyscall(argPackPtr, cpuContext, GuestSyscallTable.ENOSYS, failed: true);
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}

		// R10 is the syscall ABI's fourth argument; the export reads RCX.
		cpuContext[CpuRegister.Rcx] = cpuContext[CpuRegister.R10];

		cpuContext.ClearRaxWriteFlag();
		var exportResult = export.Function(cpuContext);
		if (!cpuContext.WasRaxWritten)
		{
			cpuContext[CpuRegister.Rax] = unchecked((ulong)exportResult);
		}

		var value = cpuContext[CpuRegister.Rax];
		if (!TryClassifySyscallResult(cpuContext, descriptor, exportResult, value, out var syscallValue, out var failed))
		{
			// The export failed in a way this row does not describe. Reporting
			// success would hand the guest a fabricated result, so refuse the
			// syscall instead.
			Console.Error.WriteLine(
				$"[LOADER][WARN] syscall {number} ({descriptor.Name}) returned an unclassifiable result " +
				$"0x{value:X16} (export result {exportResult}); reporting ENOSYS.");
			CompleteRawSyscall(argPackPtr, cpuContext, GuestSyscallTable.ENOSYS, failed: true);
			return OrbisGen2Result.ORBIS_GEN2_OK;
		}

		if (_logSyscalls)
		{
			Console.Error.WriteLine(
				$"[LOADER][TRACE] syscall {number} ({descriptor.Name}) -> " +
				$"{(failed ? $"errno {syscallValue}" : $"0x{syscallValue:X16}")}");
		}

		CompleteRawSyscall(argPackPtr, cpuContext, syscallValue, failed);
		return OrbisGen2Result.ORBIS_GEN2_OK;
	}

	/// <summary>
	/// Turns an HLE export's return into the syscall <c>(value, carry)</c> pair,
	/// or refuses when the row's declared convention does not explain it.
	/// </summary>
	private bool TryClassifySyscallResult(
		CpuContext cpuContext,
		in GuestSyscallDescriptor descriptor,
		int exportResult,
		ulong rawValue,
		out ulong syscallValue,
		out bool failed)
	{
		syscallValue = rawValue;
		failed = false;
		switch (descriptor.ResultConvention)
		{
			case GuestSyscallResultConvention.Infallible:
				return true;

			case GuestSyscallResultConvention.PosixErrno:
				if (exportResult >= 0)
				{
					return true;
				}

				// libc's cerror turns (carry, errno) into (-1, errno); this is
				// that conversion run backwards, so the errno has to come from
				// where the export just put it rather than from the return.
				if (!TryReadGuestErrno(cpuContext, out var errno) || errno <= 0)
				{
					return false;
				}

				syscallValue = unchecked((ulong)(uint)errno);
				failed = true;
				return true;

			default:
				return false;
		}
	}

	/// <summary>
	/// Reads the guest's per-thread errno through the same <c>__error</c> export
	/// guest libc uses, so there is one definition of where errno lives.
	/// </summary>
	private bool TryReadGuestErrno(CpuContext cpuContext, out int errno)
	{
		errno = 0;
		if (!_moduleManager.TryGetExport(GuestSyscallTable.ErrnoAddressNid, out var errorExport) ||
			(errorExport.Target & cpuContext.TargetGeneration) == 0)
		{
			return false;
		}

		cpuContext.ClearRaxWriteFlag();
		_ = errorExport.Function(cpuContext);
		var slot = cpuContext[CpuRegister.Rax];
		if (slot == 0 || !cpuContext.TryReadUInt64(slot, out var stored))
		{
			return false;
		}

		errno = unchecked((int)(uint)stored);
		return true;
	}

	private void CompleteRawSyscall(nint argPackPtr, CpuContext cpuContext, ulong value, bool failed)
	{
		cpuContext[CpuRegister.Rax] = value;
		*(ulong*)(argPackPtr + ImportSyscallCarryOffset) = failed ? 1uL : 0uL;
	}

	private void ReportUnsupportedSyscall(CpuContext cpuContext, int number)
	{
		// Unbounded per-call logging would drown a guest that probes in a loop;
		// the first few occurrences are what identify the next syscall to map.
		if (Interlocked.Increment(ref _unsupportedSyscallReports) > 64)
		{
			return;
		}

		Console.Error.WriteLine(
			$"[LOADER][WARN] Unsupported guest syscall {number}: ENOSYS " +
			$"(rdi=0x{cpuContext[CpuRegister.Rdi]:X16} rsi=0x{cpuContext[CpuRegister.Rsi]:X16} " +
			$"rdx=0x{cpuContext[CpuRegister.Rdx]:X16} r10=0x{cpuContext[CpuRegister.R10]:X16} " +
			$"r8=0x{cpuContext[CpuRegister.R8]:X16} r9=0x{cpuContext[CpuRegister.R9]:X16})");
	}
}
