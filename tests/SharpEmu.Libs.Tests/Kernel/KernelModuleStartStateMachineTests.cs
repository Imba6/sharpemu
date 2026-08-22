// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using System.Threading.Tasks;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// Native Guest Execution V2 stage 10: pins the sceKernelLoadStartModule (Path B, on-demand)
// module-start state machine that the post-first-frame hang investigation audited. The registry
// itself is deadlock-free -- StartState is a non-blocking claim, never a lock held across guest
// module_start. These tests codify the exact-once claim, the recursive/concurrent no-deadlock
// behaviour, the failed-start retry contract, and the synthetic-module (no guest code) path so a
// regression in any of them is caught without a live Cocoon run. The actual hang is a guest-driven
// liveness/producer-ordering wait *inside* module_start (see docs/native-guest-execution-v2-stage10*),
// which is above this registry layer and not exercisable as a pure unit test.
[CollectionDefinition(nameof(KernelModuleStartStateMachineTests), DisableParallelization = true)]
public sealed class KernelModuleStartStateMachineCollection
{
}

[Collection(nameof(KernelModuleStartStateMachineTests))]
public sealed class KernelModuleStartStateMachineTests
{
    private const ulong Base = 0x0000_0008_0900_0000UL;
    private const ulong Size = 0x1000UL;
    // A real DT_INIT entry point must be >= 0x10000 or TryBeginModuleStart short-circuits it.
    private const ulong RealInit = 0x0000_0008_0900_0010UL;

    private static int RegisterStartable(string path, ulong init = RealInit) =>
        KernelModuleRegistry.RegisterModule(
            modulePath: path,
            baseAddress: Base,
            size: Size,
            entryPoint: Base,
            initEntryPoint: init,
            ehFrameHeaderAddress: 0,
            ehFrameAddress: 0,
            ehFrameSize: 0,
            isMain: false);

    [Fact]
    public void ConcurrentLoadStartModule_SameModule_ExactlyOneStartRuns_AllCallersGetHandle()
    {
        KernelModuleRegistry.Reset();
        var handle = RegisterStartable("/app0/PSNCore.prx");

        // Many callers race TryBeginModuleStart for the same handle (the LoadStartModule claim).
        // Exactly one must win; the losers must be told to skip (they return OK+handle without
        // waiting for the winner's DT_INIT to complete).
        const int callers = 32;
        var wins = 0;
        Parallel.For(0, callers, _ =>
        {
            if (KernelModuleRegistry.TryBeginModuleStart(handle, out var module))
            {
                Assert.Equal("PSNCore.prx", module.Name);
                System.Threading.Interlocked.Increment(ref wins);
            }
        });

        Assert.Equal(1, wins);
        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var entry));
        Assert.Equal(KernelModuleRegistry.ModuleStartState.Starting, entry.StartState);
    }

    [Fact]
    public void RecursiveModuleDependency_NoSelfDeadlock_InitRunsAtMostOnce()
    {
        KernelModuleRegistry.Reset();
        var a = RegisterStartable("/app0/A.prx");
        var b = RegisterStartable("/app0/B.prx");

        // A's module_start begins (claims A=Starting). Inside A's DT_INIT the guest calls
        // LoadStartModule(B); B claims and begins. B's DT_INIT calls LoadStartModule(A) --
        // a back-edge. Because A is already Starting, the claim is refused: no re-entry into A's
        // initializer, no A-waits-B-waits-A cycle. TryBeginModuleStart never blocks.
        Assert.True(KernelModuleRegistry.TryBeginModuleStart(a, out _));      // enter A
        Assert.True(KernelModuleRegistry.TryBeginModuleStart(b, out _));      // A's init loads B
        Assert.False(KernelModuleRegistry.TryBeginModuleStart(a, out _));     // B's init re-loads A -> refused
        Assert.False(KernelModuleRegistry.TryBeginModuleStart(b, out _));     // B self-reentry -> refused

        KernelModuleRegistry.CompleteModuleStart(b, succeeded: true);
        KernelModuleRegistry.CompleteModuleStart(a, succeeded: true);

        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(a, out var ea));
        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(b, out var eb));
        Assert.Equal(KernelModuleRegistry.ModuleStartState.Started, ea.StartState);
        Assert.Equal(KernelModuleRegistry.ModuleStartState.Started, eb.StartState);

        // An already-Started module is never restarted.
        Assert.False(KernelModuleRegistry.TryBeginModuleStart(a, out _));
    }

    [Fact]
    public void FailedModuleStart_ResetsToNotStarted_SoAReloadCanRetry()
    {
        KernelModuleRegistry.Reset();
        var handle = RegisterStartable("/app0/SaveData.prx");

        Assert.True(KernelModuleRegistry.TryBeginModuleStart(handle, out _));
        KernelModuleRegistry.CompleteModuleStart(handle, succeeded: false);

        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var entry));
        Assert.Equal(KernelModuleRegistry.ModuleStartState.NotStarted, entry.StartState);

        // A subsequent LoadStartModule may retry the initializer after a failed start.
        Assert.True(KernelModuleRegistry.TryBeginModuleStart(handle, out _));
    }

    [Fact]
    public void InFlightStart_StaysStarting_UntilComplete_BlockingOtherStartsNonWaiting()
    {
        // Models the hang shape at the registry layer: a module whose DT_INIT never returns keeps
        // the module pinned Starting (CompleteModuleStart is never reached). Every other caller is
        // refused the claim and returns immediately -- it does NOT wait, so the registry cannot be
        // the thing that deadlocks. (The wait, when it happens, is inside the guest DT_INIT.)
        KernelModuleRegistry.Reset();
        var handle = RegisterStartable("/app0/PSNCommon.prx");

        Assert.True(KernelModuleRegistry.TryBeginModuleStart(handle, out _));
        // ... DT_INIT is "still running" (never completed) ...
        for (var i = 0; i < 8; i++)
        {
            Assert.False(KernelModuleRegistry.TryBeginModuleStart(handle, out _));
        }

        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var entry));
        Assert.Equal(KernelModuleRegistry.ModuleStartState.Starting, entry.StartState);
    }

    [Fact]
    public void SyntheticModule_IsStarted_NeverRunsGuestCode()
    {
        // The not-found path in KernelLoadStartModule registers a synthetic module. It must be born
        // Started with a null initializer so TryBeginModuleStart refuses it -- no guest code (and no
        // 0x0 entry) is ever executed for an unknown module path.
        KernelModuleRegistry.Reset();
        var handle = KernelModuleRegistry.RegisterSyntheticModule("mystery.sprx", isSystemModule: false);

        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var entry));
        Assert.Equal(KernelModuleRegistry.ModuleStartState.Started, entry.StartState);
        Assert.Equal(0UL, entry.InitEntryPoint);
        Assert.False(KernelModuleRegistry.TryBeginModuleStart(handle, out _));
    }

    [Fact]
    public void ModuleWithoutRealInitializer_ShortCircuitsToStarted_WithoutRunningGuestCode()
    {
        // A registered module whose InitEntryPoint is below 0x10000 (no usable DT_INIT) must not be
        // handed to the guest executor: TryBeginModuleStart flips it straight to Started and refuses
        // the claim.
        KernelModuleRegistry.Reset();
        var handle = RegisterStartable("/app0/noinit.prx", init: 0x100);

        Assert.False(KernelModuleRegistry.TryBeginModuleStart(handle, out _));
        Assert.True(KernelModuleRegistry.TryGetModuleByHandle(handle, out var entry));
        Assert.Equal(KernelModuleRegistry.ModuleStartState.Started, entry.StartState);
    }
}
