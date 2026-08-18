using SharpEmu.HLE;

namespace VirtualPS5.HLE.Kernel;

public static class KernelExports
{
    private const ulong NotificationMessageOffset = 45;
    private const ulong NotificationMessageCapacity = 3075;

    [SysAbiExport(
        Nid = "zl7hupSO0C0",
        ExportName = "sceKernelSendNotificationRequest",
        Target = Generation.Gen5,
        LibraryName = "libKernel")]
    public static int KernelSendNotificationRequest(CpuContext ctx)
    {
        var type = unchecked((int)ctx[CpuRegister.Rdi]);
        var requestAddress = ctx[CpuRegister.Rsi];
        var requestSize = ctx[CpuRegister.Rdx];
        var unknown = unchecked((int)ctx[CpuRegister.Rcx]);

        if (requestAddress == 0 ||
            requestSize <= NotificationMessageOffset)
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT);
        }

        var availableMessageBytes =
            requestSize - NotificationMessageOffset;

        var messageCapacity = (int)Math.Min(
            NotificationMessageCapacity,
            availableMessageBytes);

        if (!ctx.TryReadNullTerminatedUtf8(
                requestAddress + NotificationMessageOffset,
                messageCapacity,
                out var message))
        {
            return ctx.SetReturn(
                OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT);
        }

        Console.WriteLine(
            $"[VPS5][NOTIFY] type={type} unknown={unknown} message=\"{message}\"");

        return ctx.SetReturn(
            OrbisGen2Result.ORBIS_GEN2_OK);
    }
}