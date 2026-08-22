// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;
using SharpEmu.Core.Cpu.Native;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// Native Guest Execution V2 stage 8: a boot module initializer (DispatchModuleInitializer
// -> ExecuteEntry) runs arbitrary guest DT_INIT code inline on the CLR main thread
// (guest-above-managed -> UnmanagedCallersOnly __fastfail under a concurrent GC). Under V2
// it is routed onto a rented native worker (GC-safe). The process entry is NOT routed by
// this path (Stage 5 owns it). These tests pin the routing gate and the wiring; the
// behavioral properties (runs-to-completion/return value, imports, run-once, DT_INIT
// ordering, FMOD boot-init, no ordering regression) are validated by the >=10-run Cocoon
// and cross-title (Sarah/Hotline/Solitaire/Unpacking) runs recorded in the stage-8 doc.
public sealed class ModuleInitializerRoutingTests
{
    [Theory]
    // V2 ON: a boot module initializer routes to a native worker...
    [InlineData(true, true, true)]
    // ...but the process entry (isModuleInitializer=false) is NOT routed here.
    [InlineData(false, true, false)]
    // V2 OFF: module initializers stay inline (unchanged default behavior).
    [InlineData(true, false, false)]
    [InlineData(false, false, false)]
    public void ShouldRunModuleInitializerOnNativeWorker_MatchesStage8Policy(
        bool isModuleInitializer, bool v2Enabled, bool expected)
    {
        Assert.Equal(
            expected,
            DirectExecutionBackend.ShouldRunModuleInitializerOnNativeWorker(isModuleInitializer, v2Enabled));
    }

    [Fact]
    public void ModuleInitializer_RoutingDecision_IsIndependentOfProcessEntryGate()
    {
        // The module-init routing must not fire for the process entry -- the process entry
        // stays on its own (Stage-5) path, never routed to a worker by ExecuteEntry's
        // module-init branch. Only (isModuleInitializer && v2) is true.
        Assert.True(DirectExecutionBackend.ShouldRunModuleInitializerOnNativeWorker(true, true));
        Assert.False(DirectExecutionBackend.ShouldRunModuleInitializerOnNativeWorker(false, true));
    }

    [Fact]
    public void Backend_ExposesModuleInitializerFrameKindSetter()
    {
        // The debug frame is null without an attached debugger, so CpuDispatcher must be
        // able to tell the backend the entry is a module initializer directly.
        var setter = typeof(DirectExecutionBackend).GetMethod(
            "SetActiveEntryIsModuleInitializer",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.NotNull(setter);
        var parameters = setter!.GetParameters();
        Assert.Single(parameters);
        Assert.Equal(typeof(bool), parameters[0].ParameterType);
    }
}
