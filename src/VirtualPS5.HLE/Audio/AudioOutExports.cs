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

    // Env-gated diagnostic (SHARPEMU_PROFILE_AUDIO=1, default off). Proves the
    // guest submission pipeline is live and whether the submitted PCM is silent
    // vs carrying real mixed output (peak amplitude). Throttled to one summary
    // per second; never logs per call. Mirrors the stdio profiler's policy.
    private static readonly bool _profileAudio =
        string.Equals(
            Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_AUDIO"),
            "1",
            StringComparison.Ordinal);
    private static long _profOutputCalls;
    private static long _profSilentCalls;
    private static int _profPeakAmplitude;
    private static long _profFramesSubmitted;
    private static Timer? _profTimer;
    private static readonly object _profGate = new();

    private static void ProfileSubmission(ReadOnlySpan<byte> stereoPcm16)
    {
        Interlocked.Increment(ref _profOutputCalls);
        Interlocked.Add(ref _profFramesSubmitted, stereoPcm16.Length / StereoPcm16FrameSize);

        var peak = 0;
        for (var offset = 0; offset + 1 < stereoPcm16.Length; offset += 2)
        {
            var sample = (short)(stereoPcm16[offset] | (stereoPcm16[offset + 1] << 8));
            var magnitude = sample == short.MinValue ? short.MaxValue : Math.Abs(sample);
            if (magnitude > peak)
            {
                peak = magnitude;
            }
        }

        if (peak == 0)
        {
            Interlocked.Increment(ref _profSilentCalls);
        }

        int seen;
        do
        {
            seen = Volatile.Read(ref _profPeakAmplitude);
            if (peak <= seen)
            {
                break;
            }
        }
        while (Interlocked.CompareExchange(ref _profPeakAmplitude, peak, seen) != seen);

        EnsureProfileTimer();
    }

    private static void EnsureProfileTimer()
    {
        if (_profTimer is not null)
        {
            return;
        }

        lock (_profGate)
        {
            _profTimer ??= new Timer(_ => DumpProfile(), null, 1000, 1000);
        }
    }

    private static void DumpProfile()
    {
        var calls = Interlocked.Read(ref _profOutputCalls);
        var silent = Interlocked.Read(ref _profSilentCalls);
        var frames = Interlocked.Read(ref _profFramesSubmitted);
        var peak = Interlocked.Exchange(ref _profPeakAmplitude, 0);
        Console.Error.WriteLine(
            $"[VPS5][AUDIO-PROFILE] output_calls={calls} silent_calls={silent} " +
            $"frames={frames} peak_since_last={peak}");
    }

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

            if (_profileAudio)
            {
                ProfileSubmission(pcm);
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
