// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelGetCurrentCpu reports the core running the calling thread. Callers use
// it to index per-core data, so the result must always be a valid PS5 core index.
public sealed class KernelGetCurrentCpuTests
{
    private const int Ps5LogicalCoreCount = 8;

    [Fact]
    public void GetCurrentCpu_ReturnsCoreInRange()
    {
        var context = new CpuContext(new FakeCpuMemory(0x1_0000_0000, 0x100), Generation.Gen5);

        // Repeat so a value out of range on any executing core would be caught.
        for (var i = 0; i < 64; i++)
        {
            context[CpuRegister.Rax] = 0xDEAD;
            var result = KernelRuntimeCompatExports.KernelGetCurrentCpu(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, result);
            var core = context[CpuRegister.Rax];
            Assert.InRange(core, 0UL, (ulong)(Ps5LogicalCoreCount - 1));
        }
    }
}
