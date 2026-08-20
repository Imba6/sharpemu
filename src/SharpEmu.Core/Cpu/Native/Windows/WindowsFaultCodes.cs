// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;

namespace SharpEmu.Core.Cpu.Native.Windows;

/// <summary>
/// Windows NTSTATUS exception codes and EXCEPTION_RECORD access-type values the
/// fault handlers filter on. Values are the same numbers the handlers previously
/// compared as bare literals; only the spelling changed.
/// </summary>
internal static class WindowsFaultCodes
{
    public const uint AccessViolation = 0xC0000005u;         // 3221225477
    public const uint Breakpoint = 0x80000003u;              // 2147483651
    public const uint IllegalInstruction = 0xC000001Du;      // 3221225501
    public const uint FastFail = 0xC0000409u;                // 3221226505
    public const uint StackOverflow = 0xC00000FDu;
    public const uint ClrManagedException = 0xE0434352u;
    public const uint MsvcCppException = 0xE06D7363u;

    // The single source of truth for the vectored-exception-handler pre-filter:
    // codes that may be raised while the faulting thread is in cooperative GC
    // mode. Entering the managed handler for any of these trips the CLR's
    // reverse-P/Invoke check and fatally fails the process with "Invalid
    // Program: attempted to call a UnmanagedCallersOnly method from managed
    // code" (a C# throw is RaiseException(0xE0434352) on the throwing thread;
    // FailFast / stack-overflow arrive mid-runtime-failure; MSVC C++ exceptions
    // come from host drivers). Both emit sites — WindowsFaultHandling.CreateHandlerThunk
    // and DirectExecutionBackend.CreateExceptionHandlerTrampoline — MUST filter
    // exactly this set so the two fault paths never diverge; the order is the
    // emission order and FastFail's position drives its native breadcrumb.
    public static ReadOnlySpan<uint> NonManagedVehPreFilterCodes =>
        [ClrManagedException, MsvcCppException, FastFail, StackOverflow];

    // EXCEPTION_RECORD.ExceptionInformation[0] for access violations.
    public const ulong AccessRead = 0;
    public const ulong AccessWrite = 1;
    public const ulong AccessExecute = 8;
}
