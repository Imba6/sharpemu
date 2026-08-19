// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// libc malloc() returns host-heap memory that is not part of the guest memory map,
// so HLE code that writes/reads a caller buffer (fread/fwrite/fgets ...) must use the
// compat memory helpers, which fall back to a direct host access. This regresses the
// DoomGeneric WAD-directory read that silently failed when fread used the raw
// guest-only path against a Z_Malloc destination.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelHeapCompatMemoryTests
{
    private const ulong MemoryBase = 0xF_0000_0000;

    [Fact]
    public void CompatWriteRead_RoundTripsThroughMallocHostBuffer()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        // Allocate a buffer larger than a couple of pages via the libc malloc export.
        const int size = 50608; // the DoomGeneric lump-directory size (3163 * 16)
        ctx[CpuRegister.Rdi] = size;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Malloc(ctx));
        var address = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);

        // The malloc'd address is outside the fake guest map, so a raw guest write
        // fails -- exactly the condition that broke fread.
        var payload = new byte[size];
        for (var i = 0; i < size; i++)
        {
            payload[i] = (byte)((i * 31 + 7) & 0xFF);
        }

        Assert.False(memory.TryWrite(address, payload)); // guest-only path cannot reach it

        // The compat helpers must succeed via the host-memory fallback.
        Assert.True(KernelMemoryCompatExports.TryWriteCompat(ctx, address, payload));

        var readback = new byte[size];
        Assert.True(KernelMemoryCompatExports.TryReadCompat(ctx, address, readback));
        Assert.Equal(payload, readback);

        ctx[CpuRegister.Rdi] = address;
        KernelMemoryCompatExports.Free(ctx);
    }
}
