// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Guards the SceVideoOutFlipStatus serialization done by sceVideoOutGetFlipStatus.
// The struct callers read is: count(0x00), processTime(0x08), tsc(0x10),
// flipArg(0x18), submitTsc(0x20), reserve(0x28), gcQueueNum:flipPendingNum(0x30),
// currentBuffer(0x38). A SharpProspero-style present loop spins
// `while (flipStatus.flipArg < submittedFrame)` and livelocks if flipArg (0x18)
// is not a dedicated, advancing field — the original bug wrote 0 there and put
// currentBuffer at 0x20 instead of 0x38.
public sealed class VideoOutFlipStatusTests
{
    private const ulong MemoryBase = 0x2_0000_0000;
    private const ulong StatusAddress = MemoryBase + 0x100;
    private const int SceUserSystem = 255;
    private const int BusTypeMain = 0;
    private const int InvalidHandle = unchecked((int)0x8029000B);
    private const int InvalidAddress = unchecked((int)0x80290002);

    private static int OpenPort(CpuContext ctx)
    {
        ctx[CpuRegister.Rdi] = SceUserSystem;
        ctx[CpuRegister.Rsi] = BusTypeMain;
        ctx[CpuRegister.Rdx] = 0;
        ctx[CpuRegister.Rcx] = 0;
        var handle = VideoOutExports.VideoOutOpen(ctx);
        Assert.True(handle > 0, $"sceVideoOutOpen returned {handle}");
        return handle;
    }

    private static void ClosePort(CpuContext ctx, int handle)
    {
        ctx[CpuRegister.Rdi] = unchecked((ulong)handle);
        VideoOutExports.VideoOutClose(ctx);
    }

    [Fact]
    public void GetFlipStatus_FreshPort_UsesCorrectFieldOffsets()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = OpenPort(context);
        try
        {
            // Poison the buffer so unwritten fields would be visible.
            var poison = new byte[0x40];
            Array.Fill(poison, (byte)0xAB);
            Assert.True(memory.TryWrite(StatusAddress, poison));

            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = StatusAddress;
            Assert.Equal(
                (int)OrbisGen2Result.ORBIS_GEN2_OK,
                VideoOutExports.VideoOutGetFlipStatus(context));

            Assert.True(context.TryReadUInt64(StatusAddress + 0x00, out var count));
            Assert.True(context.TryReadUInt64(StatusAddress + 0x18, out var flipArg));
            Assert.True(context.TryReadUInt64(StatusAddress + 0x20, out var submitTsc));
            Assert.True(context.TryReadUInt64(StatusAddress + 0x38, out var currentBuffer));

            Assert.Equal(0UL, count);
            // flipArg has its own slot, distinct from count, and starts at 0.
            Assert.Equal(0UL, flipArg);
            // 0x20 (submitTsc) is no longer where currentBuffer is written.
            Assert.Equal(0UL, submitTsc);
            // Fresh port CurrentBuffer is -1, reported at 0x38 as a uint32.
            Assert.Equal(0xFFFFFFFFUL, currentBuffer & 0xFFFFFFFFUL);
        }
        finally
        {
            ClosePort(context, handle);
        }
    }

    [Fact]
    public void GetFlipStatus_InvalidHandle_ReturnsError()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = 0x7FFFFFFF;
        context[CpuRegister.Rsi] = StatusAddress;

        Assert.Equal(InvalidHandle, VideoOutExports.VideoOutGetFlipStatus(context));
    }

    [Fact]
    public void GetFlipStatus_NullAddress_ReturnsInvalidAddress()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var handle = OpenPort(context);
        try
        {
            context[CpuRegister.Rdi] = unchecked((ulong)handle);
            context[CpuRegister.Rsi] = 0;
            Assert.Equal(InvalidAddress, VideoOutExports.VideoOutGetFlipStatus(context));
        }
        finally
        {
            ClosePort(context, handle);
        }
    }
}
