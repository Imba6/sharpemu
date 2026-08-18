using System.Threading;
using SharpEmu.HLE;

namespace VirtualPS5.HLE.UserService;

public static class UserServiceExports
{
    private const int PrimaryUserId = 0x10000000;
    private const int UserServiceErrorInvalidArgument =
        unchecked((int)0x80960005);

    private static int _initialized;

    [SysAbiExport(
        ExportName = "sceUserServiceInitialize",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int Initialize(CpuContext ctx)
    {
        var parametersAddress = ctx[CpuRegister.Rdi];

        Interlocked.Exchange(ref _initialized, 1);

        Console.Error.WriteLine(
            $"[VPS5][USER] sceUserServiceInitialize " +
            $"params=0x{parametersAddress:X16}");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        ExportName = "sceUserServiceGetInitialUser",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int GetInitialUser(CpuContext ctx)
    {
        var userIdAddress = ctx[CpuRegister.Rdi];

        if (userIdAddress == 0)
        {
            return ctx.SetReturn(
                UserServiceErrorInvalidArgument);
        }

        if (!ctx.TryWriteInt32(
                userIdAddress,
                PrimaryUserId))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.Error.WriteLine(
            $"[VPS5][USER] sceUserServiceGetInitialUser " +
            $"-> 0x{PrimaryUserId:X8}");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }

    [SysAbiExport(
        ExportName = "sceUserServiceTerminate",
        Target = Generation.Gen5,
        LibraryName = "libSceUserService")]
    public static int Terminate(CpuContext ctx)
    {
        Interlocked.Exchange(ref _initialized, 0);

        Console.Error.WriteLine(
            "[VPS5][USER] sceUserServiceTerminate");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }
}