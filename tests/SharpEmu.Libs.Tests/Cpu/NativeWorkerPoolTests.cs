// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Native Guest Execution V2 stage 2: the grow-on-demand worker pool that replaces
// the fixed SemaphoreSlim(2) in-flight limiter. Exercises growth, reuse, the
// high-water bound, failed acquisition/creation (no run-slot leak), and shutdown.
public sealed class NativeWorkerPoolTests
{
    private sealed class FakeWorker
    {
        public int Id;
        public volatile bool Disposed;
    }

    private static (NativeWorkerPool<FakeWorker> pool, List<FakeWorker> disposed) NewPool(
        int max, System.Func<FakeWorker?>? factory = null)
    {
        var created = 0;
        var disposed = new List<FakeWorker>();
        factory ??= () => new FakeWorker { Id = Interlocked.Increment(ref created) };
        var pool = new NativeWorkerPool<FakeWorker>(
            max,
            factory,
            w => { w.Disposed = true; lock (disposed) { disposed.Add(w); } });
        return (pool, disposed);
    }

    [Fact]
    public void Rent_CreatesOnDemand_AndReuseAvoidsCreation()
    {
        var (pool, _) = NewPool(max: 4);

        var w1 = pool.Rent(0);
        Assert.NotNull(w1);
        Assert.Equal(1, pool.TotalWorkers);
        Assert.Equal(1, pool.CreatedCount);

        pool.Return(w1!);
        Assert.Equal(1, pool.IdleWorkers);

        // Reuse: a second rent must hand back the pooled worker, not create another.
        var w2 = pool.Rent(0);
        Assert.Same(w1, w2);
        Assert.Equal(1, pool.CreatedCount);
        Assert.Equal(1, pool.TotalWorkers);
        pool.Return(w2!);
    }

    [Fact]
    public void Rent_GrowsUpToMax_ThenBlocksOverCap()
    {
        var (pool, _) = NewPool(max: 3);

        var held = new List<FakeWorker>();
        for (var i = 0; i < 3; i++)
        {
            var w = pool.Rent(0);
            Assert.NotNull(w);
            held.Add(w!);
        }

        Assert.Equal(3, pool.TotalWorkers);
        Assert.Equal(3, pool.CreatedCount);
        Assert.Equal(3, pool.PeakConcurrentRuns);

        // Over the high-water mark: no free run slot, a zero-timeout rent fails and
        // records a timeout WITHOUT creating a 4th worker.
        var over = pool.Rent(0);
        Assert.Null(over);
        Assert.Equal(1, pool.RentTimeouts);
        Assert.Equal(3, pool.TotalWorkers);

        // Releasing one frees a slot for a subsequent rent (worker reused).
        pool.Return(held[0]);
        var again = pool.Rent(50);
        Assert.NotNull(again);
        Assert.Equal(3, pool.TotalWorkers);
        Assert.Equal(3, pool.CreatedCount);

        pool.Return(again!);
        pool.Return(held[1]);
        pool.Return(held[2]);
    }

    [Fact]
    public void Rent_TotalWorkersNeverExceedsMax_UnderConcurrentChurn()
    {
        const int max = 8;
        var (pool, _) = NewPool(max);
        var iterations = 0;

        Parallel.For(0, 32, _ =>
        {
            for (var i = 0; i < 400; i++)
            {
                var w = pool.Rent(1000);
                if (w is null)
                {
                    continue;
                }

                Interlocked.Increment(ref iterations);
                Thread.SpinWait(50);
                pool.Return(w);
            }
        });

        Assert.True(iterations > 0);
        Assert.True(pool.TotalWorkers <= max, $"total {pool.TotalWorkers} > max {max}");
        Assert.True(pool.PeakConcurrentRuns <= max, $"peak {pool.PeakConcurrentRuns} > max {max}");
        Assert.True(pool.CreatedCount <= max, $"created {pool.CreatedCount} > max {max}");
    }

    [Fact]
    public void Rent_FailedCreation_ReturnsNull_AndDoesNotLeakRunSlots()
    {
        var fail = true;
        var created = 0;
        var (pool, _) = NewPool(max: 2, factory: () =>
            Volatile.Read(ref fail) ? null : new FakeWorker { Id = Interlocked.Increment(ref created) });

        // Creation fails: every rent returns null and records a create failure, and
        // (crucially) the run slot is released each time -- otherwise the 3rd would
        // block forever below.
        for (var i = 0; i < 5; i++)
        {
            Assert.Null(pool.Rent(0));
        }

        Assert.Equal(5, pool.CreateFailures);
        Assert.Equal(0, pool.TotalWorkers);

        // Recover: both run slots must still be available (none leaked).
        Volatile.Write(ref fail, false);
        var a = pool.Rent(0);
        var b = pool.Rent(0);
        Assert.NotNull(a);
        Assert.NotNull(b);
        pool.Return(a!);
        pool.Return(b!);
    }

    [Fact]
    public void Prewarm_CreatesIdleWorkers_BoundedByMax()
    {
        var (pool, _) = NewPool(max: 4);

        Assert.Equal(3, pool.Prewarm(3));
        Assert.Equal(3, pool.IdleWorkers);
        Assert.Equal(3, pool.TotalWorkers);

        // Prewarm never exceeds max.
        Assert.Equal(1, pool.Prewarm(10));
        Assert.Equal(4, pool.TotalWorkers);
    }

    [Fact]
    public void Dispose_DestroysAllWorkers_AndBlocksFurtherRent()
    {
        var (pool, disposed) = NewPool(max: 4);
        pool.Prewarm(2);
        var live = pool.Rent(0);
        Assert.NotNull(live);
        pool.Return(live!);

        pool.Dispose();

        Assert.True(pool.IsDisposed);
        Assert.Equal(2, disposed.Count);
        Assert.All(disposed, w => Assert.True(w.Disposed));
        Assert.Null(pool.Rent(0));

        // Double dispose is a no-op.
        pool.Dispose();
        Assert.Equal(2, disposed.Count);
    }

    // Concurrency > 2 (old cap) with natural allocation-driven GC and no worker
    // leak on shutdown. NOTE: process-wide *forced* stop-the-world GC at high
    // concurrency is proven against emitted native workers by the standalone
    // prototypes/NativeGuestV2 harness -- it is deliberately NOT hammered here, since
    // a blocking gen2 Collect from a unit test perturbs the whole parallel suite.
    [Fact]
    public void Pool_HighConcurrency_NoCorruptionNoLeak()
    {
        const int max = 16;
        var allCreated = new List<FakeWorker>();
        var createdCounter = 0;
        var disposed = new List<FakeWorker>();
        var pool = new NativeWorkerPool<FakeWorker>(
            max,
            () =>
            {
                var w = new FakeWorker { Id = Interlocked.Increment(ref createdCounter) };
                lock (allCreated) { allCreated.Add(w); }
                return w;
            },
            w => { w.Disposed = true; lock (disposed) { disposed.Add(w); } });

        long ran = 0;
        Parallel.For(0, 24, new ParallelOptions { MaxDegreeOfParallelism = 24 }, _ =>
        {
            for (var i = 0; i < 200; i++)
            {
                var w = pool.Rent(2000);
                if (w is null)
                {
                    continue;
                }

                Assert.False(w.Disposed);
                var churn = new byte[128]; // allocate -> natural gen0 GC pressure
                churn[0] = (byte)i;
                Interlocked.Increment(ref ran);
                Thread.SpinWait(30);
                pool.Return(w);
            }
        });

        Assert.True(ran > 0);
        Assert.True(pool.TotalWorkers <= max);
        Assert.True(pool.PeakConcurrentRuns <= max);
        Assert.True(pool.PeakConcurrentRuns > 2, $"peak concurrency {pool.PeakConcurrentRuns} did not exceed the old cap of 2");
        Assert.True(pool.CreatedCount <= max);

        // No worker leak: every worker ever created is destroyed exactly once on shutdown.
        pool.Dispose();
        Assert.Equal(allCreated.Count, disposed.Count);
        Assert.Equal(allCreated.OrderBy(w => w.Id), disposed.OrderBy(w => w.Id));
        Assert.All(allCreated, w => Assert.True(w.Disposed));
    }

    [Fact]
    public void Dispose_WhileIdle_DisposesAllPooledWorkers()
    {
        var (pool, disposed) = NewPool(max: 6);
        pool.Prewarm(6);

        // Cycle a few so some have been rented and returned to idle.
        for (var i = 0; i < 3; i++)
        {
            var w = pool.Rent(0);
            Assert.NotNull(w);
            pool.Return(w!);
        }

        Assert.Equal(6, pool.TotalWorkers);
        Assert.Equal(6, pool.IdleWorkers);

        pool.Dispose();
        Assert.Equal(6, disposed.Count);
    }

    [Fact]
    public void Return_AfterDispose_DestroysWorker_AndReleasesSlot()
    {
        var (pool, disposed) = NewPool(max: 1);
        var w = pool.Rent(0);
        Assert.NotNull(w);

        pool.Dispose();
        // Worker checked out at dispose time is destroyed on return, not pooled.
        pool.Return(w!);
        Assert.Contains(w!, disposed);
        Assert.True(w!.Disposed);
    }
}
