using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using SharpEmu.HLE;
using SharpEmu.HLE.Host;

namespace VirtualPS5.HLE.Audio;

public static class AudioOutExports
{
    private const int AudioOutErrorInvalidPort = unchecked((int)0x80260003);
    private const int AudioOutErrorInvalidPointer = unchecked((int)0x80260004);
    private const int S16StereoFormat = 0;
    private const int StereoPcm16FrameSize = 4;

    private static readonly ConcurrentDictionary<int, PortState> Ports = new();
    private static int _nextPortHandle;

    private sealed class PortState : IDisposable
    {
        private readonly object _paceGate = new();
        private long _nextSilentOutput;

        public PortState(uint bufferLength, uint frequency, IHostAudioStream? backend)
        {
            BufferLength = bufferLength;
            Frequency = frequency;
            Backend = backend;
        }

        public uint BufferLength { get; }
        public uint Frequency { get; }
        public IHostAudioStream? Backend { get; }

        public int BufferByteLength => checked((int)BufferLength * StereoPcm16FrameSize);

        public void PaceSilence()
        {
            long delay;
            lock (_paceGate)
            {
                var now = Stopwatch.GetTimestamp();
                if (_nextSilentOutput < now)
                {
                    _nextSilentOutput = now;
                }

                delay = _nextSilentOutput - now;
                _nextSilentOutput += checked(
                    (long)Math.Ceiling(
                        Stopwatch.Frequency * (double)BufferLength / Frequency));
            }

            if (delay > 0)
            {
                Thread.Sleep(TimeSpan.FromSeconds((double)delay / Stopwatch.Frequency));
            }
        }

        public void Dispose() => Backend?.Dispose();
    }

    [SysAbiExport(
        ExportName = "sceAudioOutInit",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int Init(CpuContext ctx) =>
        ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);

    [SysAbiExport(
        ExportName = "sceAudioOutOpen",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int Open(CpuContext ctx)
    {
        var userId = unchecked((int)ctx[CpuRegister.Rdi]);
        var type = unchecked((int)ctx[CpuRegister.Rsi]);
        var index = unchecked((int)ctx[CpuRegister.Rdx]);
        var bufferLength = unchecked((uint)ctx[CpuRegister.Rcx]);
        var frequency = unchecked((uint)ctx[CpuRegister.R8]);
        var format = unchecked((int)ctx[CpuRegister.R9]);

        _ = userId;
        _ = type;

        if (index != 0 || bufferLength == 0 || bufferLength > 65536 ||
            frequency == 0 || format != S16StereoFormat)
        {
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        IHostAudioStream? backend = null;
        string backendName;
        try
        {
            var audio = HostPlatform.Current.Audio;
            backend = audio.OpenStereoPcm16Stream(frequency);
            backendName = audio.BackendName;
        }
        catch (Exception exception)
        {
            backendName = "silent";
            Console.Error.WriteLine(
                $"[VPS5][AUDIO] host backend unavailable: {exception.Message}");
        }

        var handle = Interlocked.Increment(ref _nextPortHandle);
        Ports[handle] = new PortState(bufferLength, frequency, backend);

        Console.Error.WriteLine(
            $"[VPS5][AUDIO] sceAudioOutOpen -> handle={handle} " +
            $"{frequency}Hz s16-stereo frames={bufferLength} backend={backendName}");

        return ctx.SetReturn(handle);
    }

    [SysAbiExport(
        ExportName = "sceAudioOutOutput",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int Output(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        var sourceAddress = ctx[CpuRegister.Rsi];

        if (!Ports.TryGetValue(handle, out var port))
        {
            return ctx.SetReturn(AudioOutErrorInvalidPort);
        }

        if (sourceAddress == 0)
        {
            port.PaceSilence();
            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }

        var buffer = ArrayPool<byte>.Shared.Rent(port.BufferByteLength);
        try
        {
            var pcm = buffer.AsSpan(0, port.BufferByteLength);
            if (!ctx.Memory.TryRead(sourceAddress, pcm))
            {
                return ctx.SetReturn(AudioOutErrorInvalidPointer);
            }

            if (port.Backend is null || !port.Backend.Submit(pcm))
            {
                port.PaceSilence();
            }

            return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    [SysAbiExport(
        ExportName = "sceAudioOutClose",
        Target = Generation.Gen5,
        LibraryName = "libSceAudioOut")]
    public static int Close(CpuContext ctx)
    {
        var handle = unchecked((int)ctx[CpuRegister.Rdi]);
        if (!Ports.TryRemove(handle, out var port))
        {
            return ctx.SetReturn(AudioOutErrorInvalidPort);
        }

        port.Dispose();
        Console.Error.WriteLine($"[VPS5][AUDIO] sceAudioOutClose handle={handle}");
        return ctx.SetReturn(OrbisGen2Result.ORBIS_GEN2_OK);
    }
}
