// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;
using SharpEmu.Libs.Audio;
using Xunit;

namespace SharpEmu.Libs.Tests.Audio;

[CollectionDefinition("AudioOutState", DisableParallelization = true)]
public sealed class AudioOutStateCollection
{
    public const string Name = "AudioOutState";
}

[Collection(AudioOutStateCollection.Name)]
public sealed class AudioOutExportsTests : IDisposable
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x4000;
    private const ulong ParameterAddress = MemoryBase + 0x100;
    private const ulong FirstSourceAddress = MemoryBase + 0x1000;
    private const ulong SecondSourceAddress = MemoryBase + 0x2000;

    private readonly FakeCpuMemory _memory = new(MemoryBase, MemorySize);
    private readonly CpuContext _ctx;
    private readonly List<RecordingAudioStream> _streams = [];

    public AudioOutExportsTests()
    {
        AudioOutExports.ResetForTests();
        AudioOutExports.SetStreamFactoryForTests(_ =>
        {
            var stream = new RecordingAudioStream();
            _streams.Add(stream);
            return stream;
        });
        _ctx = new CpuContext(_memory, Generation.Gen5);
    }

    [Fact]
    public void Outputs_StagesAndSubmitsSingleStereoPort()
    {
        var handle = OpenPort(bufferLength: 2);
        Span<byte> source = stackalloc byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(source, -32768);
        BinaryPrimitives.WriteInt16LittleEndian(source[2..], 32767);
        BinaryPrimitives.WriteInt16LittleEndian(source[4..], -1234);
        BinaryPrimitives.WriteInt16LittleEndian(source[6..], 5678);
        Assert.True(_memory.TryWrite(FirstSourceAddress, source));
        WriteDescriptor(0, handle, FirstSourceAddress);

        var result = Submit(outputCount: 1);

        Assert.Equal(2, result);
        Assert.Equal(2UL, _ctx[CpuRegister.Rax]);
        Assert.Equal(source.ToArray(), Assert.Single(_streams[0].Submissions));
    }

    [Fact]
    public void Outputs_SubmitsEveryPortInTheBatch()
    {
        var firstHandle = OpenPort(bufferLength: 2);
        var secondHandle = OpenPort(bufferLength: 2);
        byte[] firstSource = [1, 0, 2, 0, 3, 0, 4, 0];
        byte[] secondSource = [5, 0, 6, 0, 7, 0, 8, 0];
        Assert.True(_memory.TryWrite(FirstSourceAddress, firstSource));
        Assert.True(_memory.TryWrite(SecondSourceAddress, secondSource));

        // Reverse handle order to exercise canonical lock ordering without
        // changing which guest buffer belongs to each port.
        WriteDescriptor(0, secondHandle, FirstSourceAddress);
        WriteDescriptor(1, firstHandle, SecondSourceAddress);

        var result = Submit(outputCount: 2);

        Assert.Equal(2, result);
        Assert.Equal(secondSource, Assert.Single(_streams[0].Submissions));
        Assert.Equal(firstSource, Assert.Single(_streams[1].Submissions));
    }

    [Fact]
    public void Outputs_AcceptsNullBufferAsSynchronizationOnly()
    {
        var handle = OpenPort(bufferLength: 2);
        WriteDescriptor(0, handle, sourceAddress: 0);

        var result = Submit(outputCount: 1);

        Assert.Equal(2, result);
        Assert.Empty(_streams[0].Submissions);
    }

    [Fact]
    public void Outputs_FaultInLaterBufferDoesNotPartiallySubmit()
    {
        var firstHandle = OpenPort(bufferLength: 2);
        var secondHandle = OpenPort(bufferLength: 2);
        Assert.True(_memory.TryWrite(FirstSourceAddress, new byte[8]));
        WriteDescriptor(0, firstHandle, FirstSourceAddress);
        WriteDescriptor(1, secondHandle, MemoryBase + MemorySize);

        var result = Submit(outputCount: 2);

        Assert.Equal(AudioOutExports.AudioOutErrorInvalidPointer, result);
        Assert.All(_streams, stream => Assert.Empty(stream.Submissions));
    }

    [Fact]
    public void Outputs_RejectsDuplicateHandlesBeforeSubmission()
    {
        var handle = OpenPort(bufferLength: 2);
        Assert.True(_memory.TryWrite(FirstSourceAddress, new byte[8]));
        Assert.True(_memory.TryWrite(SecondSourceAddress, new byte[8]));
        WriteDescriptor(0, handle, FirstSourceAddress);
        WriteDescriptor(1, handle, SecondSourceAddress);

        var result = Submit(outputCount: 2);

        Assert.Equal(AudioOutExports.AudioOutErrorInvalidPort, result);
        Assert.Empty(_streams[0].Submissions);
    }

    [Fact]
    public void Outputs_RejectsPortsWithDifferentBufferLengths()
    {
        var firstHandle = OpenPort(bufferLength: 2);
        var secondHandle = OpenPort(bufferLength: 4);
        WriteDescriptor(0, firstHandle, FirstSourceAddress);
        WriteDescriptor(1, secondHandle, SecondSourceAddress);

        var result = Submit(outputCount: 2);

        Assert.Equal(AudioOutExports.AudioOutErrorInvalidSize, result);
        Assert.All(_streams, stream => Assert.Empty(stream.Submissions));
    }

    [Fact]
    public void Outputs_RejectsUnknownHandle()
    {
        WriteDescriptor(0, 99, FirstSourceAddress);

        Assert.Equal(
            AudioOutExports.AudioOutErrorInvalidPort,
            Submit(outputCount: 1));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(26u)]
    public void Outputs_RejectsInvalidOutputCount(uint outputCount)
    {
        Assert.Equal(
            AudioOutExports.AudioOutErrorPortFull,
            Submit(outputCount));
    }

    [Fact]
    public void Outputs_RejectsNullOrUnreadableParameterArray()
    {
        Assert.Equal(
            AudioOutExports.AudioOutErrorInvalidPointer,
            Submit(outputCount: 1, parameterAddress: 0));
        Assert.Equal(
            AudioOutExports.AudioOutErrorInvalidPointer,
            Submit(outputCount: 1, parameterAddress: MemoryBase + MemorySize));
    }

    [Fact]
    public void OutputsExportRegistersForBothGenerations()
    {
        foreach (var generation in new[] { Generation.Gen4, Generation.Gen5 })
        {
            var manager = new ModuleManager();
            manager.RegisterExports(
                SharpEmu.Generated.SysAbiExportRegistry.CreateExports(generation));

            Assert.True(manager.TryGetExport("w3PdaSTSwGE", out var export));
            Assert.Equal("sceAudioOutOutputs", export.Name);
            Assert.Equal("libSceAudioOut", export.LibraryName);
        }
    }

    // Regression for the Dreaming Sarah (PPSA02929) silent-audio bug. Sarah opens
    // its AudioOut port with the real PS5 SCE_AUDIO_OUT_PARAM_FORMAT_FLOAT_STEREO
    // (4), 48 kHz, 256-frame grain. The previously-active legacy handler only
    // accepted format 0, so the open was rejected with INVALID_ARGUMENT and no port
    // (hence no audio). The authoritative handler must accept the whole PS5 format
    // table.
    [Theory]
    [InlineData(0)] // S16_MONO
    [InlineData(1)] // S16_STEREO
    [InlineData(2)] // S16_8CH
    [InlineData(3)] // FLOAT_MONO
    [InlineData(4)] // FLOAT_STEREO (Dreaming Sarah)
    [InlineData(5)] // FLOAT_8CH
    [InlineData(6)] // S16_8CH_STD
    [InlineData(7)] // FLOAT_8CH_STD
    public void Open_AcceptsEveryPs5AudioFormat(int format)
    {
        var handle = OpenPortWithFormat(bufferLength: 256, format: format);
        Assert.True(handle > 0, $"PS5 audio format {format} must be accepted");
    }

    [Theory]
    [InlineData(8)]
    [InlineData(99)]
    [InlineData(0xFF)]
    public void Open_RejectsUnknownFormat(int format)
    {
        var handle = OpenPortWithFormat(bufferLength: 256, format: format);
        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, handle);
    }

    [Fact]
    public void Open_AcceptsDreamingSarahFloatStereoRequest()
    {
        // Exact runtime arguments observed from Dreaming Sarah's AudioOutThread.
        _ctx[CpuRegister.Rdi] = 255;   // userId
        _ctx[CpuRegister.Rsi] = 0;     // type = MAIN
        _ctx[CpuRegister.Rdx] = 0;     // index
        _ctx[CpuRegister.Rcx] = 256;   // len (frames)
        _ctx[CpuRegister.R8] = 48000;  // freq
        _ctx[CpuRegister.R9] = 4;      // FLOAT_STEREO
        var handle = AudioOutExports.AudioOutOpen(_ctx);
        Assert.True(handle > 0);
    }

    public void Dispose() => AudioOutExports.ResetForTests();

    private int OpenPortWithFormat(uint bufferLength, int format)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = bufferLength;
        _ctx[CpuRegister.R8] = 48000;
        _ctx[CpuRegister.R9] = unchecked((ulong)(uint)format);
        return AudioOutExports.AudioOutOpen(_ctx);
    }

    private int OpenPort(uint bufferLength)
    {
        _ctx[CpuRegister.Rdi] = 1;
        _ctx[CpuRegister.Rsi] = 0;
        _ctx[CpuRegister.Rdx] = 0;
        _ctx[CpuRegister.Rcx] = bufferLength;
        _ctx[CpuRegister.R8] = 48000;
        _ctx[CpuRegister.R9] = 1;
        return AudioOutExports.AudioOutOpen(_ctx);
    }

    private int Submit(uint outputCount, ulong parameterAddress = ParameterAddress)
    {
        _ctx[CpuRegister.Rdi] = parameterAddress;
        _ctx[CpuRegister.Rsi] = outputCount;
        return AudioOutExports.AudioOutOutputs(_ctx);
    }

    private void WriteDescriptor(int index, int handle, ulong sourceAddress)
    {
        Span<byte> descriptor = stackalloc byte[16];
        descriptor.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(descriptor, handle);
        BinaryPrimitives.WriteUInt64LittleEndian(descriptor[8..], sourceAddress);
        Assert.True(_memory.TryWrite(
            ParameterAddress + unchecked((ulong)(index * descriptor.Length)),
            descriptor));
    }

    private sealed class RecordingAudioStream : IHostAudioStream
    {
        public List<byte[]> Submissions { get; } = [];

        public bool Submit(ReadOnlySpan<byte> stereoPcm16)
        {
            Submissions.Add(stereoPcm16.ToArray());
            return true;
        }

        public void Dispose()
        {
        }
    }
}
