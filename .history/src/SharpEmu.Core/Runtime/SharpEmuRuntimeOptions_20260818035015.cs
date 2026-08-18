// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Runtime;

using SharpEmu.Core.Cpu;
using SharpEmu.Core.Cpu.Debugging;

using SharpEmu.HLE;

/// <summary>
/// HLE exports supplied by the host runtime.
///
/// These are registered before SharpEmu built-ins, so the host layer
/// may provide or override compatibility implementations.
/// </summary>
public IReadOnlyList<ExportedFunction>? ExternalHleExports { get; init; }

public readonly struct SharpEmuRuntimeOptions
{
    public CpuExecutionEngine CpuEngine { get; init; }

    public bool StrictDynlibResolution { get; init; }

    public int ImportTraceLimit { get; init; }

    /// <summary>
    /// An optional debugger to attach to guest execution. Flows through to
    /// <see cref="CpuExecutionOptions.DebugHook"/>. Null (the default) runs with
    /// no debugger attached.
    /// </summary>
    public ICpuDebugHook? DebugHook { get; init; }
}
