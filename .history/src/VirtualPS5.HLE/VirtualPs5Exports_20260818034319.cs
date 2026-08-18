using SharpEmu.HLE;

namespace VirtualPS5.HLE;

public static class VirtualPs5Exports
{
    public static IReadOnlyList<ExportedFunction> CreateExports(
        Generation generation)
    {
        return SharpEmu.Generated.SysAbiExportRegistry.CreateExports(
            generation);
    }
}