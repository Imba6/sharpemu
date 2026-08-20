// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Threading;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

/// <summary>
/// Covers the generic host-park interrupt used to deliver a guest exception to a
/// thread parked on a host Monitor inside a blocking kernel call (IL2CPP's
/// stop-the-world collector suspending the primary/external executor). The wake
/// must only make the parked thread re-evaluate its pending guest state; it must
/// never satisfy the guest wait condition.
/// </summary>
[Collection("GuestThreadExecutionScheduler")]
public sealed class HostParkInterruptTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x1000;

    private static CpuContext NewContext() =>
        new(new FakeCpuMemory(MemoryBase, MemorySize), Generation.Gen5);

    // Mirrors the host-park wait loops in pthread_cond_wait / WaitSemaphoreOnHostThread /
    // the event-flag pump: park interruptibly, and on each wake service a pending
    // guest exception before re-checking the guest predicate.
    private static void RunHostParkLoop(
        ulong handle,
        object gate,
        CpuContext context,
        Func<bool> satisfied,
        ManualResetEventSlim registered)
    {
        using var park = GuestThreadExecution.EnterInterruptibleHostPark(handle, gate);
        registered.Set();
        lock (gate)
        {
            while (!satisfied())
            {
                if (park.ConsumeInterrupt())
                {
                    GuestThreadExecution.ServiceHostParkInterrupt(gate, context, handle);
                    continue;
                }

                Monitor.Wait(gate);
            }
        }
    }

    [Fact]
    public void Interrupt_DeliversQueuedExceptionOnParkedThread_ThenReParks()
    {
        var scheduler = new RecordingScheduler { Delivered = true };
        using var swap = new SchedulerSwap(scheduler);

        const ulong handle = 0xABCDEF;
        var gate = new object();
        var satisfied = new ManualResetEventSlim(false);
        var registered = new ManualResetEventSlim(false);
        var context = NewContext();

        var worker = new Thread(() => RunHostParkLoop(handle, gate, context, () => satisfied.IsSet, registered))
        {
            IsBackground = true,
            Name = "host-park-worker",
        };
        worker.Start();
        Assert.True(registered.Wait(TimeSpan.FromSeconds(5)));

        // Raise a suspension against the parked thread.
        GuestThreadExecution.InterruptHostPark(handle);
        Assert.True(SpinUntil(() => scheduler.DeliverCalls == 1, TimeSpan.FromSeconds(5)));
        Assert.Equal(handle, scheduler.LastThreadHandle);

        // The interrupt did not satisfy the guest wait: the thread re-parks and stays alive.
        Assert.False(worker.Join(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(1, scheduler.DeliverCalls);

        // A real signal (predicate satisfied) releases it, with no extra delivery.
        satisfied.Set();
        lock (gate)
        {
            Monitor.PulseAll(gate);
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, scheduler.DeliverCalls);
    }

    [Fact]
    public void UnrelatedHostWake_DoesNotDeliverOrUnblock()
    {
        var scheduler = new RecordingScheduler { Delivered = true };
        using var swap = new SchedulerSwap(scheduler);

        const ulong handle = 0x1234;
        var gate = new object();
        var satisfied = new ManualResetEventSlim(false);
        var registered = new ManualResetEventSlim(false);
        var context = NewContext();

        var worker = new Thread(() => RunHostParkLoop(handle, gate, context, () => satisfied.IsSet, registered))
        {
            IsBackground = true,
            Name = "host-park-unrelated",
        };
        worker.Start();
        Assert.True(registered.Wait(TimeSpan.FromSeconds(5)));

        // A spurious/unrelated pulse (no interrupt queued) must not deliver anything,
        // and the guest wait must stay blocked because its predicate is unchanged.
        for (var i = 0; i < 3; i++)
        {
            lock (gate)
            {
                Monitor.PulseAll(gate);
            }
            Thread.Sleep(20);
        }

        Assert.Equal(0, scheduler.DeliverCalls);
        Assert.False(worker.Join(TimeSpan.FromMilliseconds(200)));

        satisfied.Set();
        lock (gate)
        {
            Monitor.PulseAll(gate);
        }
        Assert.True(worker.Join(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, scheduler.DeliverCalls);
    }

    [Fact]
    public void ServiceHostParkInterrupt_ReleasesGateDuringDelivery_AndReacquires()
    {
        var gate = new object();
        var scheduler = new RecordingScheduler
        {
            Delivered = true,
            OnDeliver = _ =>
            {
                // The gate must be free while the guest handler runs, because the
                // handler may re-enter kernel synchronization on the same object.
                Assert.False(Monitor.IsEntered(gate));
            },
        };
        using var swap = new SchedulerSwap(scheduler);

        var context = NewContext();
        lock (gate)
        {
            GuestThreadExecution.ServiceHostParkInterrupt(gate, context, 0x55);
            // The gate is re-acquired before returning to the caller.
            Assert.True(Monitor.IsEntered(gate));
        }

        Assert.Equal(1, scheduler.DeliverCalls);
    }

    [Fact]
    public void NestedHostPark_TargetsInnermost_AndRestoresOuterOnDispose()
    {
        const ulong handle = 0x9999;
        var outerGate = new object();
        var innerGate = new object();

        using var outer = GuestThreadExecution.EnterInterruptibleHostPark(handle, outerGate);
        using (var inner = GuestThreadExecution.EnterInterruptibleHostPark(handle, innerGate))
        {
            // The interrupt targets the innermost (current) park only.
            GuestThreadExecution.InterruptHostPark(handle);
            Assert.True(inner.ConsumeInterrupt());
            Assert.False(outer.ConsumeInterrupt());
        }

        // After the inner park is disposed the outer registration is restored.
        GuestThreadExecution.InterruptHostPark(handle);
        Assert.True(outer.ConsumeInterrupt());
    }

    [Fact]
    public void InterruptHostPark_WithNoRegistration_IsNoOp()
    {
        // Must not throw and must not touch the scheduler.
        var scheduler = new RecordingScheduler { Delivered = true };
        using var swap = new SchedulerSwap(scheduler);

        GuestThreadExecution.InterruptHostPark(0xDEAD_BEEF);
        GuestThreadExecution.InterruptHostPark(0);

        Assert.Equal(0, scheduler.DeliverCalls);
    }

    [Fact]
    public void TryDeliverParkedGuestException_ReportsWhetherAHandlerRan()
    {
        var context = NewContext();

        var nothingQueued = new RecordingScheduler { Delivered = false };
        using (new SchedulerSwap(nothingQueued))
        {
            Assert.False(GuestThreadExecution.TryDeliverParkedGuestException(context, 0x1));
            Assert.Equal(1, nothingQueued.DeliverCalls);
        }

        var oneQueued = new RecordingScheduler { Delivered = true };
        using (new SchedulerSwap(oneQueued))
        {
            Assert.True(GuestThreadExecution.TryDeliverParkedGuestException(context, 0x1));
            Assert.Equal(1, oneQueued.DeliverCalls);
        }
    }

    private static bool SpinUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(5);
        }

        return condition();
    }

    private sealed class SchedulerSwap : IDisposable
    {
        private readonly IGuestThreadScheduler? _previous;

        public SchedulerSwap(IGuestThreadScheduler scheduler)
        {
            _previous = GuestThreadExecution.Scheduler;
            GuestThreadExecution.Scheduler = scheduler;
        }

        public void Dispose() => GuestThreadExecution.Scheduler = _previous;
    }

    private sealed class RecordingScheduler : IGuestThreadScheduler
    {
        private int _deliverCalls;

        public bool Delivered { get; init; }

        public Action<ulong>? OnDeliver { get; init; }

        public int DeliverCalls => Volatile.Read(ref _deliverCalls);

        public ulong LastThreadHandle { get; private set; }

        public bool TryDeliverQueuedGuestException(
            CpuContext targetContext,
            ulong threadHandle,
            out bool delivered,
            out string? error)
        {
            LastThreadHandle = threadHandle;
            OnDeliver?.Invoke(threadHandle);
            Interlocked.Increment(ref _deliverCalls);
            delivered = Delivered;
            error = null;
            return true;
        }

        public bool SupportsGuestContextTransfer => false;

        public void RegisterGuestThreadContext(ulong threadHandle, CpuContext context)
        {
        }

        public bool TryStartThread(CpuContext creatorContext, GuestThreadStartRequest request, out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryJoinThread(CpuContext callerContext, ulong threadHandle, out ulong returnValue, out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public void Pump(CpuContext callerContext, string reason)
        {
        }

        public int WakeBlockedThreads(string wakeKey, int maxCount = int.MaxValue) => 0;

        public bool TrySetGuestThreadPriority(ulong guestThreadHandle, int guestPriority) => false;

        public bool TrySetGuestThreadAffinity(ulong guestThreadHandle, ulong affinityMask) => false;

        public IReadOnlyList<GuestThreadSnapshot> SnapshotThreads() => [];

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryCallGuestFunction(
            CpuContext callerContext,
            ulong entryPoint,
            ulong arg0,
            ulong arg1,
            ulong arg2,
            ulong stackAddress,
            ulong stackSize,
            string reason,
            out ulong returnValue,
            out string? error)
        {
            returnValue = 0;
            error = "not supported";
            return false;
        }

        public bool TryCallGuestContinuation(
            CpuContext callerContext,
            GuestCpuContinuation continuation,
            string reason,
            out string? error)
        {
            error = "not supported";
            return false;
        }

        public bool TryRaiseGuestException(
            CpuContext callerContext,
            ulong threadHandle,
            ulong handler,
            int exceptionType,
            out string? error)
        {
            error = "not supported";
            return false;
        }
    }
}
