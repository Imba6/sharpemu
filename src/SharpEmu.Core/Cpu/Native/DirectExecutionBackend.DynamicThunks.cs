// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Materializes guest-callable entry points for HLE exports the guest image
/// never statically imported.
/// </summary>
/// <remarks>
/// A statically imported symbol gets its guest-callable address from the ELF
/// import stub: <see cref="SetupImportStubs"/> overwrites the 16-byte slot with
/// a jump into an import handler trampoline that carries the stub's index into
/// <c>_importEntries</c>, and the managed gateway dispatches whatever
/// <see cref="ExportedFunction"/> that entry resolved to.
///
/// A symbol reached only through <c>dlsym</c> has no such slot, so there is
/// nothing for the runtime to hand back. Rather than inventing a second calling
/// path, this appends an import entry, builds the exact same trampoline for it,
/// and then materializes a 16-byte stub slot patched by
/// <see cref="PatchImportStub"/> — byte for byte what the loader writes over a
/// static import stub. The slot address is what the guest receives, so a
/// dynamically resolved export and a statically imported one are the same kind
/// of pointer: 16-byte aligned, callable at +0, and carrying the stub's
/// <c>jmp rax</c> continuation at +0x0A.
///
/// Thunks are keyed by NID, so repeated lookups of one export return one stable
/// address, and their lifetime is the trampoline table's: they are released by
/// <see cref="ClearImportHandlerTrampolines"/> when the import table is rebuilt
/// or the backend is disposed.
/// </remarks>
public sealed unsafe partial class DirectExecutionBackend
{
	// One patched import stub, matching the 16-byte slot layout PatchImportStub
	// writes into the guest image.
	private const int DynamicHleThunkStubSize = 16;

	private const uint DynamicHleThunkStubPageSize = 4096;

	private readonly object _dynamicHleThunkGate = new();

	private readonly Dictionary<string, ulong> _dynamicHleThunksByNid = new(StringComparer.Ordinal);

	private nint _dynamicHleThunkStubPage;

	private uint _dynamicHleThunkStubPageOffset;

	/// <summary>
	/// Resolves <paramref name="symbolName"/> (an export name or an encoded NID)
	/// to an HLE export and returns a guest-callable address for it.
	/// </summary>
	private bool TryResolveDynamicHleThunkAddress(string symbolName, out ulong address)
	{
		address = 0;
		var cpuContext = ActiveCpuContext;
		if (cpuContext == null || !TryResolveHleExport(symbolName, out var export))
		{
			return false;
		}

		return TryGetOrCreateDynamicHleThunk(export, cpuContext.TargetGeneration, out address);
	}

	/// <summary>
	/// Maps a dlsym request onto the HLE export table without losing the
	/// library/NID/export identity the caller asked for.
	/// </summary>
	private bool TryResolveHleExport(string symbolName, out ExportedFunction export)
	{
		export = null!;
		if (string.IsNullOrWhiteSpace(symbolName))
		{
			return false;
		}

		// dlsym callers pass a plain export name; payload-style callers and the
		// module symbol tables pass the encoded NID directly.
		return _moduleManager.TryGetExportByName(symbolName, out export) ||
			_moduleManager.TryGetExport(symbolName, out export) ||
			_moduleManager.TryGetExport(ComputePsNid(symbolName), out export);
	}

	private bool TryGetOrCreateDynamicHleThunk(
		ExportedFunction export,
		Generation targetGeneration,
		out ulong address)
	{
		address = 0;
		if ((export.Target & targetGeneration) == 0)
		{
			// The same generation gate the static dispatch path applies; a stub
			// that would immediately report "not implemented" is not an address.
			return false;
		}

		lock (_dynamicHleThunkGate)
		{
			if (_dynamicHleThunksByNid.TryGetValue(export.Nid, out address))
			{
				return address != 0;
			}

			var published = _importEntries;
			var index = published.Length;
			var grown = new ImportStubEntry[index + 1];
			Array.Copy(published, grown, index);

			// The trampoline bakes in its import index, so it must be created
			// against the slot it will occupy. Nothing can reach it before the
			// grown table is published: its address is not guest-visible yet.
			var trampoline = CreateImportHandlerTrampoline(index);
			var stub = trampoline == 0 ? 0 : AllocateDynamicHleThunkStub();
			if (stub == 0 || !PatchImportStub(stub, trampoline))
			{
				Console.Error.WriteLine(
					$"[LOADER][WARN] Dynamic HLE thunk allocation failed: {export.LibraryName}:{export.Name} ({export.Nid})");
				return false;
			}

			address = (ulong)stub;
			grown[index] = new ImportStubEntry(
				address,
				export.Nid,
				export,
				IsLeafImport(export.Nid),
				IsNoBlockLeafImport(export.Nid),
				ShouldSuppressStrlenTrace(export.Nid),
				IsImportLoopGuardBoundary(export.Nid),
				StableHash64(export.Nid));
			Volatile.Write(ref _importEntries, grown);
			_dynamicHleThunksByNid[export.Nid] = address;
			Console.Error.WriteLine(
				$"[LOADER][INFO] Dynamic HLE thunk: {export.LibraryName}:{export.Name} ({export.Nid}) -> 0x{address:X16}");
			return true;
		}
	}

	/// <summary>
	/// Carves the next 16-byte stub slot, backing it with a fresh executable page
	/// when the current one is exhausted. Pages join the import trampoline list,
	/// so thunk storage is released on the same rebuild/dispose path.
	/// </summary>
	private nint AllocateDynamicHleThunkStub()
	{
		if (_dynamicHleThunkStubPage == 0 ||
			_dynamicHleThunkStubPageOffset + DynamicHleThunkStubSize > DynamicHleThunkStubPageSize)
		{
			var page = VirtualAlloc(null, DynamicHleThunkStubPageSize, 12288u, 64u);
			if (page == null)
			{
				return 0;
			}

			lock (_importHandlerTrampolineGate)
			{
				_importHandlerTrampolines.Add((nint)page);
			}

			_dynamicHleThunkStubPage = (nint)page;
			_dynamicHleThunkStubPageOffset = 0;
		}

		var slot = _dynamicHleThunkStubPage + (nint)_dynamicHleThunkStubPageOffset;
		_dynamicHleThunkStubPageOffset += DynamicHleThunkStubSize;
		return slot;
	}

	private void ClearDynamicHleThunks()
	{
		lock (_dynamicHleThunkGate)
		{
			_dynamicHleThunksByNid.Clear();
			_dynamicHleThunkStubPage = 0;
			_dynamicHleThunkStubPageOffset = 0;
		}
	}
}
