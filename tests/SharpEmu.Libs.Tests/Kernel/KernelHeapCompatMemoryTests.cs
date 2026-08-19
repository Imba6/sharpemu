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

    // The compat helpers report which class of memory serviced the access so the
    // stdio profiler (and any future fast path) can tell a guest-mapped buffer
    // from a libc host-heap buffer. A mapped guest destination must resolve on
    // the guest fast path (guestHit == true).
    [Fact]
    public void CompatWriteRead_MappedGuestBuffer_ReportsGuestHit()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        var payload = new byte[256];
        for (var i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i ^ 0x5A);
        }

        Assert.True(KernelMemoryCompatExports.TryWriteCompat(ctx, MemoryBase, payload, out var writeGuestHit));
        Assert.True(writeGuestHit);

        var readback = new byte[payload.Length];
        Assert.True(KernelMemoryCompatExports.TryReadCompat(ctx, MemoryBase, readback, out var readGuestHit));
        Assert.True(readGuestHit);
        Assert.Equal(payload, readback);
    }

    // A libc malloc() destination lives outside the guest map, so the compat
    // helpers must fall back to the host-memory path and report guestHit == false.
    [Fact]
    public void CompatWriteRead_MallocHostBuffer_ReportsHostFallback()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        const int size = 4096;
        ctx[CpuRegister.Rdi] = size;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, KernelMemoryCompatExports.Malloc(ctx));
        var address = ctx[CpuRegister.Rax];
        Assert.NotEqual(0UL, address);

        var payload = new byte[size];
        for (var i = 0; i < size; i++)
        {
            payload[i] = (byte)((i * 7 + 3) & 0xFF);
        }

        Assert.True(KernelMemoryCompatExports.TryWriteCompat(ctx, address, payload, out var writeGuestHit));
        Assert.False(writeGuestHit); // host-heap fallback, not the guest map

        var readback = new byte[size];
        Assert.True(KernelMemoryCompatExports.TryReadCompat(ctx, address, readback, out var readGuestHit));
        Assert.False(readGuestHit);
        Assert.Equal(payload, readback);

        ctx[CpuRegister.Rdi] = address;
        KernelMemoryCompatExports.Free(ctx);
    }
}
