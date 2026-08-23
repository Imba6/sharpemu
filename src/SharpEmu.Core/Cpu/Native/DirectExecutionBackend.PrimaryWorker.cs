// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Runtime.InteropServices;
using System.Threading;
using SharpEmu.HLE;

namespace SharpEmu.Core.Cpu.Native;

public sealed partial class DirectExecutionBackend
{
    // Native Guest Execution V2 stage 5C (gated, default off; requires V2):
    // run the true process entry (the primary/main guest) as a cooperative
    // GuestThreadState pinned to its OWN persistent native worker.
    //
    // Stage 5A proved a cooperative primary eliminates the UnmanagedCallersOnly
    // __fastfail (the host-park nested 0x1E GC-suspend delivery is gone) but
    // stalled Cocoon at ~0 fps: the primary's hot per-frame Baselib semaphore
    // block/resume paid a pooled-worker Rent/Return + cross-thread handoff every
    // acquire. The 5C audit proved that churn is legitimate high-frequency
    // producer/consumer traffic (CASE B), not a wake bug. The fix is a dedicated
    // persistent worker: the primary owns one NativeGuestExecutor for the whole
    // session, so block/resume is a same-worker continuation with no pool rent.
    internal static readonly bool NativeGuestV2PrimaryEnabled =
        NativeGuestV2Enabled &&
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_NATIVE_GUEST_V2_PRIMARY"),
            "1",
            StringComparison.Ordinal);

    // Bytes backing the synthetic primary thread handle. scePthreadSelf returns
    // this handle (via CurrentGuestThreadHandle) and the Boehm collector raises
    // 0x1E against it, so it must be a stable, collision-free value for the whole
    // session. An AllocHGlobal pointer is unique against every other live guest
    // thread handle (they are AllocHGlobal pointers too) and against guest-mapped
    // addresses (host address space), exactly as ordinary pthread handles are.
    private const int PrimaryHandleObjectSize = 64;

    private unsafe bool ExecuteEntryCooperativePrimary(
        CpuContext context,
        ulong entryPoint,
        out OrbisGen2Result result)
    {
        Console.Error.WriteLine(
            $"[LOADER][INFO] ExecuteEntry (V2 cooperative primary) starting at 0x{entryPoint:X16}");
        Console.Error.WriteLine(
            $"[LOADER][INFO] RSP=0x{context[CpuRegister.Rsp]:X16}, RDI=0x{context[CpuRegister.Rdi]:X16}");
        if (context[CpuRegister.Rsp] == 0)
        {
            LastError = "Guest stack pointer is zero";
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return false;
        }

        // The dedicated worker lives OUTSIDE the NativeWorkerPool run-slot budget,
        // so it never permanently consumes an ordinary-pthread slot.
        var pinned = NativeGuestExecutor.TryCreate(this);
        if (pinned is null)
        {
            // Do not silently fall back to the crashing inline primary: fail the
            // gated experiment loudly rather than regressing to the UCO crash.
            LastError = "V2 cooperative primary: failed to create dedicated native worker";
            Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT;
            return false;
        }

        var previousActiveBackend = _activeExecutionBackend;
        var previousActiveContext = _activeCpuContext;
        var previousSentinel = _activeEntryReturnSentinelRip;
        var previousReturnSlotAddress = _activeGuestReturnSlotAddress;
        var previousForcedExit = _activeForcedGuestExit;
        var previousYieldRequested = _activeGuestThreadYieldRequested;
        var previousYieldReason = _activeGuestThreadYieldReason;
        nint previousHostRspSlotValue = TlsGetValue(_hostRspSlotTlsIndex);

        var primaryHandle = unchecked((ulong)Marshal.AllocHGlobal(PrimaryHandleObjectSize).ToInt64());
        GuestThreadState? state = null;
        try
        {
            _activeExecutionBackend = this;
            _activeCpuContext = context;
            _activeEntryReturnSentinelRip = 0;
            _activeGuestReturnSlotAddress = 0;
            _activeForcedGuestExit = false;
            _activeGuestThreadYieldRequested = false;
            _activeGuestThreadYieldReason = null;
            BindTlsBase(context);

            state = new GuestThreadState
            {
                ThreadHandle = primaryHandle,
                EntryPoint = entryPoint,
                Argument = context[CpuRegister.Rdi],
                Name = "PrimaryGuest",
                Priority = 0,
                AffinityMask = 0,
                Context = context,
                State = GuestThreadRunState.Ready,
                IsCooperativePrimary = true,
                PinnedNativeExecutor = pinned,
            };

            // Register as a real _guestThreads entry (NOT external): the wake
            // sources (WakeBlockedThreads / WakeExpiredBlockedGuestThreads) and the
            // ready-dispatcher scan _guestThreads, and RegisterBlockedGuestThread
            // Continuation no-ops for a handle absent from it. An external-only
            // primary would park with no waker -> permanent hang.
            using (LockGate("RegisterCooperativePrimary"))
            {
                _guestThreads[primaryHandle] = state;
                _readyGuestThreads.Enqueue(state);
                Interlocked.Increment(ref _readyGuestThreadCount);
            }

            Console.Error.WriteLine(
                $"[LOADER][INFO] V2 cooperative primary registered handle=0x{primaryHandle:X16} " +
                $"entry=0x{entryPoint:X16} (pinned native worker)");

            StartStallWatchdog();
            StartReadyThreadDispatcher();

            // The CLR main thread parks here (preemptive) while the ready-dispatcher
            // drives the primary through RunGuestThread on its GuestExecutionRunner,
            // whose slices run on the pinned native worker. This is the block/resume
            // loop the Stage-5 design described, kept off the CLR main thread.
            int num6 = WaitForCooperativePrimaryExit(state);
            Console.Error.WriteLine($"[LOADER][INFO] Guest returned (cooperative primary): {num6}");

            if (!ActiveForcedGuestExit)
            {
                PumpUntilGuestThreadsIdle(context, "entry_return");
            }

            if (ActiveForcedGuestExit)
            {
                result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
                if (string.IsNullOrEmpty(LastError))
                {
                    LastError = "Detected repeating import loop and forced guest unwind to host.";
                }
                Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
                return false;
            }
            if (num6 == 0)
            {
                result = OrbisGen2Result.ORBIS_GEN2_OK;
                LastError = null;
                return true;
            }
            result = OrbisGen2Result.ORBIS_GEN2_ERROR_CPU_TRAP;
            if (string.IsNullOrEmpty(LastError))
            {
                LastError = $"Guest entry point returned non-zero: {num6}";
            }
            Console.Error.WriteLine("[LOADER][ERROR] " + LastError);
            return false;
        }
        finally
        {
            StopReadyThreadDispatcher();
            StopStallWatchdog();
            // Deterministic teardown: the primary is Exited/Faulted (or the session
            // is force-exiting) and will not be scheduled again, so its per-thread
            // GuestExecutionRunner is idle and its pinned worker is parked between
            // runs. Dispose both, then release the synthetic handle.
            if (state is not null)
            {
                state.PinnedNativeExecutor = null;
                state.ExecutionRunner?.Dispose();
                state.ExecutionRunner = null;
            }
            pinned.Dispose();
            Marshal.FreeHGlobal(unchecked((nint)(long)primaryHandle));
            ActiveEntryReturnSentinelRip = 0uL;
            TlsSetValue(_hostRspSlotTlsIndex, previousHostRspSlotValue);
            RestoreActiveExecutionThread(
                previousActiveBackend,
                previousActiveContext,
                previousSentinel,
                previousReturnSlotAddress,
                previousForcedExit,
                previousYieldRequested,
                previousYieldReason);
        }
    }

    // Parks the CLR main thread until the cooperative primary returns from guest
    // main (Exited -> its guest return value), faults, or the session force-exits.
    // A cooperative yield (Blocked) is NOT an exit: the dispatcher resumes it and
    // this loop keeps waiting, so a yielded primary is never mistaken for process
    // exit -- the generic extension of ExecuteEntry's return contract.
    private int WaitForCooperativePrimaryExit(GuestThreadState state)
    {
        while (true)
        {
            if (ActiveForcedGuestExit)
            {
                return -1;
            }

            GuestThreadRunState runState;
            ulong exitValue;
            string? faultReason;
            using (LockGate("WaitCooperativePrimaryExit"))
            {
                runState = state.State;
                exitValue = state.ExitValue;
                faultReason = state.BlockReason;
            }

            switch (runState)
            {
                case GuestThreadRunState.Exited:
                    return unchecked((int)(long)exitValue);
                case GuestThreadRunState.Faulted:
                    if (string.IsNullOrEmpty(LastError))
                    {
                        LastError = faultReason ?? "primary guest thread faulted";
                    }
                    return -1;
                default:
                    Thread.Sleep(1);
                    break;
            }
        }
    }
}
