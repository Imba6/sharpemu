using System.Buffers.Binary;
using System.Diagnostics;
using System.Threading;
using SharpEmu.HLE;

namespace VirtualPS5.HLE.Pad;

public static class PadExports
{
    private const int PadErrorInvalidHandle =
        unchecked((int)0x80920003);

    private const int PadErrorNotInitialized =
        unchecked((int)0x80920005);

    private const int PadErrorDeviceNotConnected =
        unchecked((int)0x80920007);

    private const int PrimaryUserId = 0x10000000;
    private const int PrimaryPadHandle = 1;

    private const int StandardPortType = 0;

    private const int PadDataSize = 0x78;

    private static int _initialized;

    private static readonly long StartTimestamp =
        Stopwatch.GetTimestamp();

    [SysAbiExport(
        ExportName = "scePadInit",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int Init(CpuContext ctx)
    {
        Volatile.Write(
            ref _initialized,
            1);

        Console.Error.WriteLine(
            "[VPS5][PAD] scePadInit");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        ExportName = "scePadOpen",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int Open(CpuContext ctx)
    {
        var userId =
            unchecked((int)ctx[CpuRegister.Rdi]);

        var type =
            unchecked((int)ctx[CpuRegister.Rsi]);

        var index =
            unchecked((int)ctx[CpuRegister.Rdx]);

        var parameterAddress =
            ctx[CpuRegister.Rcx];

        if (Volatile.Read(
                ref _initialized) == 0)
        {
            return ctx.SetReturn(
                PadErrorNotInitialized);
        }

        if (userId != PrimaryUserId ||
            type != StandardPortType ||
            index != 0 ||
            parameterAddress != 0)
        {
            return ctx.SetReturn(
                PadErrorDeviceNotConnected);
        }

        Console.Error.WriteLine(
            $"[VPS5][PAD] scePadOpen " +
            $"user=0x{userId:X8} type={type} index={index} " +
            $"-> handle={PrimaryPadHandle}");

        /*
         * scePadOpen returns the handle directly,
         * not an Orbis error code on success.
         */
        return ctx.SetReturn(
            PrimaryPadHandle);
    }

    [SysAbiExport(
        ExportName = "scePadReadState",
        Target = Generation.Gen5,
        LibraryName = "libScePad")]
    public static int ReadState(CpuContext ctx)
    {
        var handle =
            unchecked((int)ctx[CpuRegister.Rdi]);

        var dataAddress =
            ctx[CpuRegister.Rsi];

        if (handle != PrimaryPadHandle)
        {
            return ctx.SetReturn(
                PadErrorInvalidHandle);
        }

        if (dataAddress == 0)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        Span<byte> data =
            stackalloc byte[PadDataSize];

        data.Clear();

        /*
         * Neutral controller state.
         *
         * buttons   0x00 uint32
         * leftX     0x04
         * leftY     0x05
         * rightX    0x06
         * rightY    0x07
         * L2        0x08
         * R2        0x09
         */

        BinaryPrimitives.WriteUInt32LittleEndian(
            data[0x00..],
            0);

        data[0x04] = 128;
        data[0x05] = 128;
        data[0x06] = 128;
        data[0x07] = 128;

        data[0x08] = 0;
        data[0x09] = 0;

        /*
         * Neutral orientation.
         */
        BinaryPrimitives.WriteSingleLittleEndian(
            data[0x18..],
            1.0f);

        /*
         * Controller connected.
         */
        data[0x4C] = 1;

        var elapsedTicks =
            Stopwatch.GetTimestamp() - StartTimestamp;

        var timestampMicroseconds =
            (ulong)Math.Max(
                0,
                elapsedTicks * 1_000_000L /
                Stopwatch.Frequency);

        BinaryPrimitives.WriteUInt64LittleEndian(
            data[0x50..],
            timestampMicroseconds);

        /*
         * Connected/current-state flag used by
         * the standard pad data structure.
         */
        data[0x68] = 1;

        if (!ctx.Memory.TryWrite(
                dataAddress,
                data))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][PAD] scePadReadState " +
            $"handle={handle} buttons=0x00000000 " +
            $"sticks=[128,128,128,128] " +
            $"timestamp={timestampMicroseconds}");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }
}