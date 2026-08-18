using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;

namespace VirtualPS5.HLE.SystemService;

public static class SystemServiceExports
{
    private const int SystemServiceErrorParameter =
        unchecked((int)0x80A10003);

    private const int SystemServiceStatusSize = 0x0C;

    [SysAbiExport(
        ExportName = "sceSystemServiceGetStatus",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int GetStatus(CpuContext ctx)
    {
        var statusAddress = ctx[CpuRegister.Rdi];

        if (statusAddress == 0)
        {
            return ctx.SetReturn(
                SystemServiceErrorParameter);
        }

        Span<byte> status =
            stackalloc byte[SystemServiceStatusSize];

        status.Clear();

        /*
         * Current baseline:
         * normal running application state.
         */
        BinaryPrimitives.WriteInt32LittleEndian(
            status,
            0);

        status[0x06] = 1;

        if (!ctx.Memory.TryWrite(
                statusAddress,
                status))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][SYSTEM] sceSystemServiceGetStatus " +
            $"out=0x{statusAddress:X16}");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        ExportName = "sceSystemServiceParamGetInt",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int ParamGetInt(CpuContext ctx)
    {
        var parameterId =
            unchecked((int)ctx[CpuRegister.Rdi]);

        var valueAddress =
            ctx[CpuRegister.Rsi];

        if (valueAddress == 0)
        {
            return ctx.SetReturn(
                SystemServiceErrorParameter);
        }

        var value = parameterId switch
        {
            1 or 2 or 3 or 1000 => 1,
            4 => 180,
            _ => 0,
        };

        if (!ctx.TryWriteInt32(
                valueAddress,
                value))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][SYSTEM] sceSystemServiceParamGetInt " +
            $"id={parameterId} -> {value}");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        ExportName = "sceSystemServiceParamGetString",
        Target = Generation.Gen5,
        LibraryName = "libSceSystemService")]
    public static int ParamGetString(CpuContext ctx)
    {
        var parameterId =
            unchecked((int)ctx[CpuRegister.Rdi]);

        var bufferAddress =
            ctx[CpuRegister.Rsi];

        var bufferSize =
            unchecked((int)ctx[CpuRegister.Rdx]);

        if (bufferAddress == 0 ||
            bufferSize <= 0)
        {
            return ctx.SetReturn(
                SystemServiceErrorParameter);
        }

        var value =
            Encoding.UTF8.GetBytes("VirtualPS5");

        var writeLength =
            Math.Min(value.Length, bufferSize - 1);

        Span<byte> output =
            stackalloc byte[writeLength + 1];

        value.AsSpan(
            0,
            writeLength).CopyTo(output);

        output[writeLength] = 0;

        if (!ctx.Memory.TryWrite(
                bufferAddress,
                output))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][SYSTEM] sceSystemServiceParamGetString " +
            $"id={parameterId} -> \"VirtualPS5\"");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }
}