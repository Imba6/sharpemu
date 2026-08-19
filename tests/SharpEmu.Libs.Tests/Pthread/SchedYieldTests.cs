// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Pthread;

// sched_yield offers the processor to another runnable thread and returns 0.
public sealed class SchedYieldTests
{
    [Fact]
    public void SchedYield_ReturnsZero()
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x100), Generation.Gen5);
        ctx[CpuRegister.Rax] = 0xDEAD;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelPthreadCompatExports.SchedYield(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
    }
}
