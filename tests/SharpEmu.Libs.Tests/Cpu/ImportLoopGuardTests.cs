// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Import-loop guard decision policy. The guard forcibly unwinds the guest when it
// detects a hot repeating import loop that has persisted past the time threshold.
// The defect this suite pins: the guard used to fire on (pattern + wall-clock)
// ALONE, which falsely kills a legitimate hot spin (e.g. scePthreadYield) while
// OTHER guest threads are making real progress. ShouldFireImportLoopGuard now
// vetoes/restarts on global forward progress. No per-title or per-NID special
// casing is involved — the policy is expressed purely in dispatch deltas + time.
public sealed class ImportLoopGuardTests
{
    private const int MinHits = 6;
    private const long GuardTicks = 1000;      // arbitrary "5s" worth of ticks
    private const long ProgressThreshold = 64; // ImportLoopOtherThreadProgressThreshold

    // Regression for the core defect: enough hits + past the time threshold, but
    // OTHER threads dispatched ~1M imports during the window => the VM is clearly
    // progressing => the guard must NOT fire, and must restart its window.
    // Before the fix this scenario (pattern + elapsed>=guard) fired unconditionally.
    [Fact]
    public void DoesNotFire_WhenOtherThreadsMadeProgress()
    {
        var fired = DirectExecutionBackend.ShouldFireImportLoopGuard(
            patternHits: MinHits,
            minPatternHits: MinHits,
            elapsedTicks: GuardTicks * 10,       // well past the time threshold
            guardTicks: GuardTicks,
            otherThreadDispatchDelta: 1_000_000, // massive other-thread progress
            otherThreadProgressThreshold: ProgressThreshold,
            out var restartWindow);

        Assert.False(fired);
        Assert.True(restartWindow);
    }

    // The complement: a genuine whole-VM wedge (no other-thread progress) still
    // fires once it has both repeated enough and outlived the time threshold.
    [Fact]
    public void Fires_WhenNoGlobalProgressAndPastThreshold()
    {
        var fired = DirectExecutionBackend.ShouldFireImportLoopGuard(
            patternHits: MinHits,
            minPatternHits: MinHits,
            elapsedTicks: GuardTicks,            // exactly at the threshold
            guardTicks: GuardTicks,
            otherThreadDispatchDelta: 0,         // nothing else advanced
            otherThreadProgressThreshold: ProgressThreshold,
            out var restartWindow);

        Assert.True(fired);
        Assert.False(restartWindow);
    }

    // A tiny amount of other-thread churn (below the threshold) is noise, not
    // progress, and must not save a truly wedged VM.
    [Fact]
    public void Fires_WhenOtherThreadProgressBelowThreshold()
    {
        var fired = DirectExecutionBackend.ShouldFireImportLoopGuard(
            patternHits: MinHits,
            minPatternHits: MinHits,
            elapsedTicks: GuardTicks,
            guardTicks: GuardTicks,
            otherThreadDispatchDelta: ProgressThreshold, // == threshold, not ">"
            otherThreadProgressThreshold: ProgressThreshold,
            out var restartWindow);

        Assert.True(fired);
        Assert.False(restartWindow);
    }

    // Not enough repeats yet: never fires and never restarts, regardless of time.
    [Fact]
    public void DoesNotFire_BeforeMinimumPatternHits()
    {
        var fired = DirectExecutionBackend.ShouldFireImportLoopGuard(
            patternHits: MinHits - 1,
            minPatternHits: MinHits,
            elapsedTicks: GuardTicks * 100,
            guardTicks: GuardTicks,
            otherThreadDispatchDelta: 0,
            otherThreadProgressThreshold: ProgressThreshold,
            out var restartWindow);

        Assert.False(fired);
        Assert.False(restartWindow);
    }

    // Pattern + no progress, but the spin has not yet outlived the time window:
    // hold fire (the whole point of the wall-clock threshold).
    [Fact]
    public void DoesNotFire_BeforeTimeThreshold()
    {
        var fired = DirectExecutionBackend.ShouldFireImportLoopGuard(
            patternHits: MinHits,
            minPatternHits: MinHits,
            elapsedTicks: GuardTicks - 1,
            guardTicks: GuardTicks,
            otherThreadDispatchDelta: 0,
            otherThreadProgressThreshold: ProgressThreshold,
            out var restartWindow);

        Assert.False(fired);
        Assert.False(restartWindow);
    }
}
