// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Diagnostics;

namespace SharpEmu.HLE;

public readonly record struct GuestThreadStartRequest(
    ulong ThreadHandle,
    ulong EntryPoint,
    ulong Argument,
    ulong AttributeAddress,
    string Name,
    int Priority,
    ulong AffinityMask);

public readonly record struct GuestThreadSnapshot(
    ulong ThreadHandle,
    string Name,
    string State,
    long ImportCount,
    string? LastImportNid,
    ulong LastReturnRip,
    string? BlockReason);

/// <summary>
/// Continuation state for a blocked guest thread, replacing the closure pair a blocking
/// wait used to allocate. TryWake runs under the scheduler's guest-thread gate and
/// returns true when the waiter has a final result and the thread should be re-readied;
/// false leaves it parked. Resume runs later on the woken thread outside that gate, and
/// its return value becomes the guest's RAX for the resumed call.
/// </summary>
public interface IGuestThreadBlockWaiter
{
    int Resume();

    bool TryWake();
}

public interface IGuestThreadScheduler
{
    bool SupportsGuestContextTransfer { get; }

    /// <summary>
    /// Associates a pthread identity created on the primary guest executor
    /// with its live CPU context. Primary execution does not pass through
    /// TryStartThread, but kernel exception delivery must still be able to
    /// target it.
    /// </summary>
    void RegisterGuestThreadContext(ulong threadHandle, CpuContext context);

    bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error);

    bool TryJoinThread(
        CpuContext callerContext,
        ulong threadHandle,
        out ulong returnValue,
        out string? error);

    void Pump(CpuContext callerContext, string reason);

    int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue);

    /// <summary>
    /// Applies a new guest scheduling priority to a live thread, mapping it
    /// onto the host thread if one is running. Returns false when the thread
    /// handle is unknown.
    /// </summary>
    bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority);

    /// <summary>
    /// Records a new affinity mask for a guest thread and re-applies it to
    /// the host thread where the platform supports it.
    /// </summary>
    bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask);

    IReadOnlyList<GuestThreadSnapshot> SnapshotThreads();

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out string? error);

    bool TryCallGuestFunction(
        CpuContext callerContext,
        ulong entryPoint,
        ulong arg0,
        ulong arg1,
        ulong arg2,
        ulong stackAddress,
        ulong stackSize,
        string reason,
        out ulong returnValue,
        out string? error);

    bool TryCallGuestContinuation(
        CpuContext callerContext,
        GuestCpuContinuation continuation,
        string reason,
        out string? error);

    /// <summary>
    /// Asynchronously invokes an installed kernel exception handler as the
    /// target guest thread. This is used by IL2CPP's stop-the-world collector:
    /// the handler acknowledges suspension and may remain blocked until the
    /// collecting thread resumes it.
    /// </summary>
    bool TryRaiseGuestException(
        CpuContext callerContext,
        ulong threadHandle,
        ulong handler,
        int exceptionType,
        out string? error);

    /// <summary>
    /// Delivers any guest exception already queued for <paramref name="threadHandle"/>
    /// by running its handler on the CURRENT host thread, using
    /// <paramref name="targetContext"/> as the interrupted CPU state. This is the
    /// symmetric counterpart to <see cref="TryRaiseGuestException"/> for a thread that
    /// is parked on a host Monitor inside a blocking kernel call: the parked thread's
    /// own host thread services the queued signal. <paramref name="delivered"/> reports
    /// whether a handler actually ran (false when nothing was queued for the thread).
    /// The call itself returns false only on a hard delivery failure.
    /// </summary>
    bool TryDeliverQueuedGuestException(
        CpuContext targetContext,
        ulong threadHandle,
        out bool delivered,
        out string? error);
}

public readonly record struct GuestImportCallFrame(
    bool IsValid,
    ulong ReturnRip,
    ulong ResumeRsp,
    ulong ReturnSlotAddress);

public readonly record struct GuestCpuContinuation(
    ulong Rip,
    ulong Rsp,
    ulong ReturnSlotAddress,
    ulong Rflags,
    ulong FsBase,
    ulong GsBase,
    ulong Rax,
    ulong Rcx,
    ulong Rdx,
    ulong Rbx,
    ulong Rbp,
    ulong Rsi,
    ulong Rdi,
    ulong R8,
    ulong R9,
    ulong R10,
    ulong R11,
    ulong R12,
    ulong R13,
    ulong R14,
    ulong R15,
    ushort FpuControlWord,
    uint Mxcsr,
    bool RestoreFullFpuState);

public static class GuestThreadExecution
{
    private sealed class DelegateGuestThreadBlockWaiter : IGuestThreadBlockWaiter
    {
        private readonly Func<int> _resume;
        private readonly Func<bool> _tryWake;

        public DelegateGuestThreadBlockWaiter(Func<int> resume, Func<bool> tryWake)
        {
            _resume = resume;
            _tryWake = tryWake;
        }

        public int Resume() => _resume();

        public bool TryWake() => _tryWake();
    }

    // ---------------------------------------------------------------------
    // Host-park interrupt.
    //
    // A guest thread that is not a cooperative executor (the primary/external
    // executor, or any caller with no resumable CPU continuation) blocks inside
    // a kernel wait by parking its own host thread on a Monitor. A guest
    // exception raised against such a thread by another thread (IL2CPP's
    // stop-the-world collector does exactly this) must be delivered on that
    // thread's own host thread, which only surfaces when the park wakes. This
    // registry lets the raising thread wake a specific host park so its wait
    // loop re-evaluates pending guest state. The wake never satisfies the guest
    // wait condition; the loop must re-check its own predicate and re-park when
    // nothing else has changed.
    // ---------------------------------------------------------------------

    internal sealed class HostParkRegistration
    {
        public required ulong ThreadHandle { get; init; }

        public required object Gate { get; init; }

        // Guarded by Gate.
        public bool InterruptPending;

        // Guarded by _hostParkRegistryGate; supports nested parks on one thread.
        public HostParkRegistration? Previous;
    }

    private static readonly Dictionary<ulong, HostParkRegistration> _hostParkRegistry = new();
    private static readonly object _hostParkRegistryGate = new();

    /// <summary>
    /// Registers the calling host thread as parked on <paramref name="gate"/> (the
    /// same Monitor its wait loop blocks on) under <paramref name="threadHandle"/>,
    /// so a queued guest exception can interrupt the park. Dispose the returned
    /// scope when the wait loop exits. Nested parks on one thread are stacked and
    /// restored on dispose. A zero handle registers nothing (still returns a
    /// disposable no-op scope).
    /// </summary>
    public static HostParkScope EnterInterruptibleHostPark(ulong threadHandle, object gate)
    {
        ArgumentNullException.ThrowIfNull(gate);
        var registration = new HostParkRegistration
        {
            ThreadHandle = threadHandle,
            Gate = gate,
        };
        if (threadHandle != 0)
        {
            lock (_hostParkRegistryGate)
            {
                _hostParkRegistry.TryGetValue(threadHandle, out var previous);
                registration.Previous = previous;
                _hostParkRegistry[threadHandle] = registration;
            }
        }

        return new HostParkScope(registration);
    }

    /// <summary>
    /// Wakes the host thread parked under <paramref name="threadHandle"/> so its
    /// wait loop re-evaluates pending guest state (for example a just-queued kernel
    /// exception). This only pulses the park Monitor; it never records progress on
    /// the guest synchronization object, so the loop stays blocked unless its own
    /// predicate is independently satisfied.
    /// </summary>
    public static void InterruptHostPark(ulong threadHandle)
    {
        if (threadHandle == 0)
        {
            return;
        }

        HostParkRegistration? registration;
        lock (_hostParkRegistryGate)
        {
            _hostParkRegistry.TryGetValue(threadHandle, out registration);
        }

        if (registration is null)
        {
            return;
        }

        lock (registration.Gate)
        {
            registration.InterruptPending = true;
            Monitor.PulseAll(registration.Gate);
        }
    }

    /// <summary>
    /// Runs any guest exception queued for <paramref name="threadHandle"/> on the
    /// current host thread, using <paramref name="context"/> as the interrupted
    /// thread's CPU state. Call this after a host park wakes and its guest predicate
    /// is still unsatisfied, with the park gate released. Returns true if a handler
    /// ran.
    /// </summary>
    public static bool TryDeliverParkedGuestException(CpuContext context, ulong threadHandle)
    {
        var scheduler = Scheduler;
        if (scheduler is null || threadHandle == 0)
        {
            return false;
        }

        return scheduler.TryDeliverQueuedGuestException(context, threadHandle, out var delivered, out _) &&
            delivered;
    }

    /// <summary>
    /// Releases <paramref name="gate"/> (which the caller currently holds), delivers
    /// any queued guest exception for <paramref name="threadHandle"/> on this host
    /// thread, then re-acquires <paramref name="gate"/>. The gate is released around
    /// delivery because the guest handler may re-enter kernel synchronization that
    /// takes the same gate.
    /// </summary>
    public static void ServiceHostParkInterrupt(object gate, CpuContext context, ulong threadHandle)
    {
        ArgumentNullException.ThrowIfNull(gate);
        Monitor.Exit(gate);
        try
        {
            TryDeliverParkedGuestException(context, threadHandle);
        }
        finally
        {
            Monitor.Enter(gate);
        }
    }

    /// <summary>Scope returned by <see cref="EnterInterruptibleHostPark"/>.</summary>
    public readonly struct HostParkScope : IDisposable
    {
        private readonly HostParkRegistration? _registration;

        internal HostParkScope(HostParkRegistration registration) => _registration = registration;

        /// <summary>
        /// Consumes and clears a pending interrupt flag. Must be called while holding
        /// the park gate. Returns true when an interrupt was pending, in which case
        /// the loop should service it and re-check its predicate before re-parking.
        /// </summary>
        public bool ConsumeInterrupt()
        {
            var registration = _registration;
            if (registration is null || !registration.InterruptPending)
            {
                return false;
            }

            registration.InterruptPending = false;
            return true;
        }

        public void Dispose()
        {
            var registration = _registration;
            if (registration is null || registration.ThreadHandle == 0)
            {
                return;
            }

            lock (_hostParkRegistryGate)
            {
                if (_hostParkRegistry.TryGetValue(registration.ThreadHandle, out var current) &&
                    ReferenceEquals(current, registration))
                {
                    if (registration.Previous is null)
                    {
                        _hostParkRegistry.Remove(registration.ThreadHandle);
                    }
                    else
                    {
                        _hostParkRegistry[registration.ThreadHandle] = registration.Previous;
                    }
                }
            }
        }
    }

    [ThreadStatic]
    private static ulong _currentGuestThreadHandle;

    [ThreadStatic]
    private static ulong _currentFiberAddress;

    [ThreadStatic]
    private static string? _pendingBlockReason;

    [ThreadStatic]
    private static bool _pendingBlockContinuationValid;

    [ThreadStatic]
    private static GuestCpuContinuation _pendingBlockContinuation;

    [ThreadStatic]
    private static string? _pendingBlockWakeKey;

    [ThreadStatic]
    private static IGuestThreadBlockWaiter? _pendingBlockWaiter;

    [ThreadStatic]
    private static long _pendingBlockDeadlineTimestamp;

    [ThreadStatic]
    private static bool _pendingEntryExit;

    [ThreadStatic]
    private static ulong _pendingEntryExitValue;

    [ThreadStatic]
    private static string? _pendingEntryExitReason;

    [ThreadStatic]
    private static bool _pendingContextTransfer;

    [ThreadStatic]
    private static GuestCpuContinuation _pendingContextTransferTarget;

    [ThreadStatic]
    private static bool _hasCurrentImportCallFrame;

    [ThreadStatic]
    private static ulong _currentImportReturnRip;

    [ThreadStatic]
    private static ulong _currentImportResumeRsp;

    [ThreadStatic]
    private static ulong _currentImportReturnSlotAddress;

    public static IGuestThreadScheduler? Scheduler { get; set; }

    /// <summary>
    /// Fired when a guest thread is torn down without a clean pthread_exit
    /// (e.g. TBB execute-AV → worker_abort). Libs use this to abandon mutexes.
    /// </summary>
    public static event Func<ulong, string, int>? GuestThreadAbandoned;

    public static int NotifyGuestThreadAbandoned(ulong threadHandle, string reason)
    {
        if (threadHandle == 0 || GuestThreadAbandoned is null)
        {
            return 0;
        }

        try
        {
            return GuestThreadAbandoned.Invoke(threadHandle, reason);
        }
        catch
        {
            return 0;
        }
    }

    public static bool IsGuestThread => _currentGuestThreadHandle != 0;

    public static ulong CurrentGuestThreadHandle => _currentGuestThreadHandle;

    public static ulong CurrentFiberAddress => _currentFiberAddress;

    public static ulong EnterGuestThread(ulong threadHandle)
    {
        var previous = _currentGuestThreadHandle;
        _currentGuestThreadHandle = threadHandle;
        _pendingBlockReason = null;
        _pendingBlockContinuationValid = false;
        _pendingBlockContinuation = default;
        _pendingBlockWakeKey = null;
        _pendingBlockWaiter = null;
        _pendingBlockDeadlineTimestamp = 0;
        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        _hasCurrentImportCallFrame = false;
        _currentImportReturnRip = 0;
        _currentImportResumeRsp = 0;
        _currentImportReturnSlotAddress = 0;
        return previous;
    }

    public static void RestoreGuestThread(ulong previousThreadHandle)
    {
        _currentGuestThreadHandle = previousThreadHandle;
        _pendingBlockReason = null;
        _pendingBlockContinuationValid = false;
        _pendingBlockContinuation = default;
        _pendingBlockWakeKey = null;
        _pendingBlockWaiter = null;
        _pendingBlockDeadlineTimestamp = 0;
        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        _hasCurrentImportCallFrame = false;
        _currentImportReturnRip = 0;
        _currentImportResumeRsp = 0;
        _currentImportReturnSlotAddress = 0;
    }

    public static ulong EnterFiber(ulong fiberAddress)
    {
        var previous = _currentFiberAddress;
        _currentFiberAddress = fiberAddress;
        return previous;
    }

    public static void RestoreFiber(ulong previousFiberAddress)
    {
        _currentFiberAddress = previousFiberAddress;
    }

    public static bool RequestCurrentThreadBlock(string reason) => RequestCurrentThreadBlock(null, reason);

    public static bool RequestCurrentThreadBlock(
        CpuContext? context,
        string reason,
        string? wakeKey = null,
        IGuestThreadBlockWaiter? waiter = null,
        long blockDeadlineTimestamp = 0)
    {
        if (!IsGuestThread)
        {
            return false;
        }

        _pendingBlockReason = string.IsNullOrWhiteSpace(reason) ? "guest_thread_blocked" : reason;
        _pendingBlockWakeKey = string.IsNullOrWhiteSpace(wakeKey) ? _pendingBlockReason : wakeKey;
        _pendingBlockWaiter = waiter;
        _pendingBlockDeadlineTimestamp = blockDeadlineTimestamp;
        if (context is not null && TryCaptureCurrentBlockContinuation(context, out var continuation))
        {
            _pendingBlockContinuation = continuation;
            _pendingBlockContinuationValid = true;
        }
        else
        {
            _pendingBlockContinuation = default;
            _pendingBlockContinuationValid = false;
        }

        return true;
    }

    // Compatibility bridge for exports that still describe blocked work as a
    // resume/wake delegate pair. New hot paths should provide an
    // IGuestThreadBlockWaiter directly to avoid allocating closures.
    public static bool RequestCurrentThreadBlock(
        CpuContext? context,
        string reason,
        string? wakeKey,
        Func<int> resumeHandler,
        Func<bool> wakeHandler,
        long blockDeadlineTimestamp = 0) =>
        RequestCurrentThreadBlock(
            context,
            reason,
            wakeKey,
            new DelegateGuestThreadBlockWaiter(resumeHandler, wakeHandler),
            blockDeadlineTimestamp);

    public static bool TryConsumeCurrentThreadBlock(out string reason)
    {
        return TryConsumeCurrentThreadBlock(out reason, out _, out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation)
    {
        return TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out _,
            out _,
            out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation,
        out string wakeKey,
        out IGuestThreadBlockWaiter? waiter)
    {
        return TryConsumeCurrentThreadBlock(
            out reason,
            out continuation,
            out hasContinuation,
            out wakeKey,
            out waiter,
            out _);
    }

    public static bool TryConsumeCurrentThreadBlock(
        out string reason,
        out GuestCpuContinuation continuation,
        out bool hasContinuation,
        out string wakeKey,
        out IGuestThreadBlockWaiter? waiter,
        out long blockDeadlineTimestamp)
    {
        reason = _pendingBlockReason ?? string.Empty;
        if (string.IsNullOrEmpty(reason))
        {
            continuation = default;
            hasContinuation = false;
            wakeKey = string.Empty;
            waiter = null;
            blockDeadlineTimestamp = 0;
            return false;
        }

        continuation = _pendingBlockContinuation;
        hasContinuation = _pendingBlockContinuationValid;
        wakeKey = _pendingBlockWakeKey ?? reason;
        waiter = _pendingBlockWaiter;
        blockDeadlineTimestamp = _pendingBlockDeadlineTimestamp;
        _pendingBlockReason = null;
        _pendingBlockContinuation = default;
        _pendingBlockContinuationValid = false;
        _pendingBlockWakeKey = null;
        _pendingBlockWaiter = null;
        _pendingBlockDeadlineTimestamp = 0;
        return true;
    }

    public static long ComputeDeadlineTimestamp(TimeSpan timeout)
    {
        if (timeout <= TimeSpan.Zero)
        {
            return Stopwatch.GetTimestamp();
        }

        var ticks = timeout.TotalSeconds >= long.MaxValue / (double)Stopwatch.Frequency
            ? long.MaxValue
            : (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency);
        var now = Stopwatch.GetTimestamp();
        if (long.MaxValue - now <= ticks)
        {
            return long.MaxValue;
        }

        return now + Math.Max(1, ticks);
    }

    private static bool TryCaptureCurrentBlockContinuation(CpuContext context, out GuestCpuContinuation continuation)
    {
        if (!TryGetCurrentImportCallFrame(out var frame) ||
            frame.ReturnRip < 65536 ||
            frame.ResumeRsp == 0 ||
            frame.ReturnSlotAddress == 0)
        {
            continuation = default;
            return false;
        }

        continuation = new GuestCpuContinuation(
            frame.ReturnRip,
            frame.ResumeRsp,
            frame.ReturnSlotAddress,
            context.Rflags,
            context.FsBase,
            context.GsBase,
            0,
            context[CpuRegister.Rcx],
            context[CpuRegister.Rdx],
            context[CpuRegister.Rbx],
            context[CpuRegister.Rbp],
            context[CpuRegister.Rsi],
            context[CpuRegister.Rdi],
            context[CpuRegister.R8],
            context[CpuRegister.R9],
            context[CpuRegister.R10],
            context[CpuRegister.R11],
            context[CpuRegister.R12],
            context[CpuRegister.R13],
            context[CpuRegister.R14],
            context[CpuRegister.R15],
            context.FpuControlWord,
            context.Mxcsr,
            RestoreFullFpuState: false);
        return true;
    }

    public static void RequestCurrentEntryExit(string reason, int status)
    {
        RequestCurrentEntryExit(reason, unchecked((ulong)(long)status));
    }

    public static void RequestCurrentEntryExit(string reason, ulong value)
    {
        _pendingEntryExit = true;
        _pendingEntryExitValue = value;
        _pendingEntryExitReason = string.IsNullOrWhiteSpace(reason) ? "guest_entry_exit" : reason;
    }

    public static bool TryConsumeCurrentEntryExit(out ulong value, out string reason)
    {
        value = _pendingEntryExitValue;
        reason = _pendingEntryExitReason ?? string.Empty;
        if (!_pendingEntryExit)
        {
            return false;
        }

        _pendingEntryExit = false;
        _pendingEntryExitValue = 0;
        _pendingEntryExitReason = null;
        return true;
    }

    public static void RequestCurrentContextTransfer(GuestCpuContinuation target)
    {
        _pendingContextTransferTarget = target;
        _pendingContextTransfer = true;
    }

    public static bool TryConsumeCurrentContextTransfer(out GuestCpuContinuation target)
    {
        target = _pendingContextTransferTarget;
        if (!_pendingContextTransfer)
        {
            return false;
        }

        _pendingContextTransfer = false;
        _pendingContextTransferTarget = default;
        return true;
    }

    public static GuestImportCallFrame EnterImportCallFrame(
        ulong returnRip,
        ulong resumeRsp,
        ulong returnSlotAddress)
    {
        var previous = new GuestImportCallFrame(
            _hasCurrentImportCallFrame,
            _currentImportReturnRip,
            _currentImportResumeRsp,
            _currentImportReturnSlotAddress);
        _hasCurrentImportCallFrame = true;
        _currentImportReturnRip = returnRip;
        _currentImportResumeRsp = resumeRsp;
        _currentImportReturnSlotAddress = returnSlotAddress;
        return previous;
    }

    public static void RestoreImportCallFrame(GuestImportCallFrame previous)
    {
        _hasCurrentImportCallFrame = previous.IsValid;
        _currentImportReturnRip = previous.ReturnRip;
        _currentImportResumeRsp = previous.ResumeRsp;
        _currentImportReturnSlotAddress = previous.ReturnSlotAddress;
    }

    public static bool TryGetCurrentImportCallFrame(out GuestImportCallFrame frame)
    {
        if (!_hasCurrentImportCallFrame)
        {
            frame = default;
            return false;
        }

        frame = new GuestImportCallFrame(
            true,
            _currentImportReturnRip,
            _currentImportResumeRsp,
            _currentImportReturnSlotAddress);
        return true;
    }
}
