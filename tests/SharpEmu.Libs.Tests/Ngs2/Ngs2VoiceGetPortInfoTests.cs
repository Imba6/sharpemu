// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.Libs.Ngs2;
using Xunit;

namespace SharpEmu.Libs.Tests.Ngs2;

// sceNgs2VoiceGetPortInfo(SceNgs2Handle voice, uint32_t port,
//                         SceNgs2VoicePortInfo* outInfo, size_t outInfoSize).
// SceNgs2VoicePortInfo is 24 bytes: s32 matrixId; float volume; u32 numDelaySamples;
// u32 destInputId; SceNgs2Handle destHandle. Our software mixer does not model
// per-voice port routing, so a valid voice reports a default port (matrix 0, unity
// volume, no delay, master destination). AnimalWell (PPSA22520) busy-polls this on
// every voice; leaving it unresolved returned garbage and spun the audio thread.
public sealed class Ngs2VoiceGetPortInfoTests
{
    private const int InvalidVoiceHandle = unchecked((int)0x804A0300);
    private const int InvalidOutAddress = unchecked((int)0x804A0053);
    private const ulong Base = 0x1_0000_0000UL;
    private const ulong VoiceHandle = Base + 0x40;
    private const ulong OutAddress = Base + 0x100;

    private readonly CpuContext _ctx = new(new FakeCpuMemory(Base, 0x1000), Generation.Gen5);

    public Ngs2VoiceGetPortInfoTests() => Ngs2Exports.ResetStateForTests();

    [Fact]
    public void UnknownVoiceHandle_ReturnsInvalidVoiceHandle()
    {
        _ctx[CpuRegister.Rdi] = VoiceHandle; // never seeded
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = OutAddress;
        _ctx[CpuRegister.Rcx] = 0x18;

        Assert.Equal(InvalidVoiceHandle, Ngs2Exports.Ngs2VoiceGetPortInfo(_ctx));
    }

    [Fact]
    public void NullOutAddress_ReturnsInvalidOutAddress()
    {
        Ngs2Exports.SeedVoiceForTests(VoiceHandle);
        _ctx[CpuRegister.Rdi] = VoiceHandle;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = 0x18;

        Assert.Equal(InvalidOutAddress, Ngs2Exports.Ngs2VoiceGetPortInfo(_ctx));
    }

    [Fact]
    public void ValidVoice_WritesDefaultPortInfoAndSucceeds()
    {
        Ngs2Exports.SeedVoiceForTests(VoiceHandle);
        // Poison the output so we can prove every field is written deterministically.
        WriteScratch(OutAddress, 0x18, 0xAB);

        _ctx[CpuRegister.Rdi] = VoiceHandle;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = OutAddress;
        _ctx[CpuRegister.Rcx] = 0x18;

        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetPortInfo(_ctx));

        var info = ReadScratch(OutAddress, 0x18);
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(0, 4)));   // matrixId
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(info.AsSpan(4, 4))); // volume
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(8, 4)));  // numDelaySamples
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(info.AsSpan(12, 4))); // destInputId
        Assert.Equal(0ul, BinaryPrimitives.ReadUInt64LittleEndian(info.AsSpan(16, 8))); // destHandle
    }

    [Fact]
    public void SmallOutInfoSize_DoesNotOverrunTheCallerBuffer()
    {
        Ngs2Exports.SeedVoiceForTests(VoiceHandle);
        WriteScratch(OutAddress, 0x18, 0xAB);

        _ctx[CpuRegister.Rdi] = VoiceHandle;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = OutAddress;
        _ctx[CpuRegister.Rcx] = 8; // caller only provided 8 bytes

        Assert.Equal(0, Ngs2Exports.Ngs2VoiceGetPortInfo(_ctx));

        var info = ReadScratch(OutAddress, 0x18);
        // First 8 bytes filled (matrixId + unity volume), remainder untouched (0xAB).
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(info.AsSpan(0, 4)));
        Assert.Equal(1.0f, BinaryPrimitives.ReadSingleLittleEndian(info.AsSpan(4, 4)));
        for (var i = 8; i < 0x18; i++)
        {
            Assert.Equal(0xAB, info[i]);
        }
    }

    private void WriteScratch(ulong address, int length, byte value)
    {
        Span<byte> buffer = stackalloc byte[length];
        buffer.Fill(value);
        Assert.True(_ctx.Memory.TryWrite(address, buffer));
    }

    private byte[] ReadScratch(ulong address, int length)
    {
        var buffer = new byte[length];
        Assert.True(_ctx.Memory.TryRead(address, buffer));
        return buffer;
    }
}
