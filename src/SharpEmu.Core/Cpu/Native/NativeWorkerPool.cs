// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Threading;

namespace SharpEmu.Core.Cpu.Native;

/// <summary>
/// Generic grow-on-demand pool of persistent native workers (Native Guest
/// Execution V2, stage 2). Replaces the fixed <c>SemaphoreSlim(2)</c> in-flight
/// limiter with a pool bounded by a configurable high-water mark:
/// <list type="bullet">
///   <item>reuse an idle worker before creating a new one,</item>
///   <item>grow on demand up to <see cref="MaxWorkers"/>,</item>
///   <item>bound concurrent checkouts (= peak live OS worker threads) by the same
///   high-water mark via a run-slot semaphore, so callers block (no busy-poll)
///   rather than storm-creating threads,</item>
///   <item>never leak a run slot on a failed/aborted creation, and</item>
///   <item>tear down deterministically.</item>
/// </list>
/// The worker type is opaque: the caller supplies a factory (which may return
/// <see langword="null"/> on creation failure) and a disposer. Thread-safe.
/// </summary>
internal sealed class NativeWorkerPool<T> : IDisposable
    where T : class
{
    private readonly int _max;
    private readonly Func<T?> _factory;
    private readonly Action<T> _disposer;
    private readonly SemaphoreSlim _runSlots;
    private readonly object _gate = new();
    private readonly Stack<T> _idle = new();
    private readonly List<T> _all = new();
    private bool _disposed;

    private int _peakWorkers;
    private int _liveRuns;
    private int _peakConcurrentRuns;
    private long _createdCount;
    private long _createFailures;
    private long _rentTimeouts;

    public NativeWorkerPool(int maxWorkers, Func<T?> factory, Action<T> disposer)
    {
        if (maxWorkers < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxWorkers));
        }

        _max = maxWorkers;
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _disposer = disposer ?? throw new ArgumentNullException(nameof(disposer));
        _runSlots = new SemaphoreSlim(maxWorkers, maxWorkers);
    }

    public int MaxWorkers => _max;
    public int PeakWorkers => Volatile.Read(ref _peakWorkers);
    public int PeakConcurrentRuns => Volatile.Read(ref _peakConcurrentRuns);
    public long CreatedCount => Interlocked.Read(ref _createdCount);
    public long CreateFailures => Interlocked.Read(ref _createFailures);
    public long RentTimeouts => Interlocked.Read(ref _rentTimeouts);

    public int TotalWorkers
    {
        get { lock (_gate) { return _all.Count; } }
    }

    public int IdleWorkers
    {
        get { lock (_gate) { return _idle.Count; } }
    }

    public bool IsDisposed
    {
        get { lock (_gate) { return _disposed; } }
    }

    /// <summary>
    /// Pre-creates up to <paramref name="count"/> workers into the idle set so the
    /// first runs do not pay creation latency. Never exceeds <see cref="MaxWorkers"/>.
    /// Best-effort: stops early if creation fails or the pool is disposed.
    /// </summary>
    public int Prewarm(int count)
    {
        var warmed = 0;
        for (var i = 0; i < count; i++)
        {
            lock (_gate)
            {
                if (_disposed || _all.Count >= _max)
                {
                    break;
                }
            }

            var w = _factory();
            if (w is null)
            {
                Interlocked.Increment(ref _createFailures);
                break;
            }

            lock (_gate)
            {
                // Re-check under the lock: another thread may have disposed or filled
                // the pool to the high-water mark while this worker was being created.
                if (_disposed || _all.Count >= _max)
                {
                    _disposer(w);
                    if (_disposed)
                    {
                        return warmed;
                    }

                    break;
                }

                _all.Add(w);
                _idle.Push(w);
                Interlocked.Increment(ref _createdCount);
                UpdatePeakWorkersLocked();
            }

            warmed++;
        }

        return warmed;
    }

    /// <summary>
    /// Rents a worker, blocking up to <paramref name="timeoutMs"/> for a free run
    /// slot when all <see cref="MaxWorkers"/> are in flight. Reuses an idle worker,
    /// otherwise creates one. Returns <see langword="null"/> if the wait timed out,
    /// creation failed, or the pool is disposed — in all of which cases NO run slot
    /// remains held. On success the caller MUST call <see cref="Return"/> exactly once.
    /// </summary>
    public T? Rent(int timeoutMs)
    {
        if (IsDisposed)
        {
            return null;
        }

        if (!_runSlots.Wait(timeoutMs))
        {
            Interlocked.Increment(ref _rentTimeouts);
            return null;
        }

        // A run slot is held from here; every early-out path must release it.
        T? worker = null;
        lock (_gate)
        {
            if (_disposed)
            {
                _runSlots.Release();
                return null;
            }

            if (_idle.Count > 0)
            {
                worker = _idle.Pop();
            }
        }

        if (worker is null)
        {
            worker = _factory();
            if (worker is null)
            {
                Interlocked.Increment(ref _createFailures);
                _runSlots.Release();
                return null;
            }

            lock (_gate)
            {
                if (_disposed)
                {
                    _disposer(worker);
                    _runSlots.Release();
                    return null;
                }

                _all.Add(worker);
                Interlocked.Increment(ref _createdCount);
                UpdatePeakWorkersLocked();
            }
        }

        var live = Interlocked.Increment(ref _liveRuns);
        UpdatePeak(ref _peakConcurrentRuns, live);
        return worker;
    }

    /// <summary>
    /// Returns a rented worker and releases its run slot. A worker returned after
    /// disposal is destroyed rather than pooled.
    /// </summary>
    public void Return(T worker)
    {
        if (worker is null)
        {
            throw new ArgumentNullException(nameof(worker));
        }

        Interlocked.Decrement(ref _liveRuns);

        var dispose = false;
        lock (_gate)
        {
            if (_disposed)
            {
                dispose = true;
            }
            else
            {
                _idle.Push(worker);
            }
        }

        if (dispose)
        {
            _disposer(worker);
        }

        _runSlots.Release();
    }

    public void Dispose()
    {
        T[] snapshot;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            snapshot = _all.ToArray();
            _all.Clear();
            _idle.Clear();
        }

        foreach (var w in snapshot)
        {
            _disposer(w);
        }
    }

    private void UpdatePeakWorkersLocked()
    {
        var count = _all.Count;
        if (count > _peakWorkers)
        {
            _peakWorkers = count;
        }
    }

    private static void UpdatePeak(ref int peak, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref peak);
            if (value <= seen)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref peak, value, seen) != seen);
    }
}
