// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using SharpEmu.HLE;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// Reusable FreeBSD <c>sysctl(3)</c> / <c>__sysctl(2)</c> core plus the
/// management-information-base (MIB) dispatch table it resolves against.
/// </summary>
/// <remarks>
/// The Orbis/Prospero kernel is a FreeBSD 9/11 derivative, so its sysctl ABI is
/// the stock one: a name is an array of integer OID components, and the call
/// either reads a value into <c>oldp</c>, reports the value's size when
/// <c>oldp</c> is null, or (for writable nodes) stores <c>newp</c>. This class
/// implements that contract once; individual OIDs are rows in
/// <see cref="MibTable"/>, so extending coverage never touches the ABI plumbing.
///
/// Only OIDs whose value we can source honestly are listed. An unknown OID —
/// including Sony-specific ones whose value would have to be invented — returns
/// <c>ENOENT</c>, exactly as a real kernel does for a node it does not export.
/// </remarks>
public static class KernelSysctlCompatExports
{
    // FreeBSD errno subset used by the sysctl contract.
    private const int Enoent = 2;
    private const int Eperm = 1;
    private const int Efault = 14;
    private const int Einval = 22;
    private const int Enomem = 12;

    // sys/sysctl.h: CTL_MAXNAME is the largest number of OID components.
    private const int CtlMaxName = 24;

    // Top-level identifiers (sys/sysctl.h).
    private const int CtlKern = 1;
    private const int CtlHw = 6;

    // CTL_HW identifiers (sys/sysctl.h).
    private const int HwPagesize = 7;

    // Sony-specific CTL_KERN node: kernel_get_fw_version reads {CTL_KERN, 46} for
    // the presented PS5 system-software version. The value is not a FreeBSD
    // constant and is not invented here; it comes from KernelFirmwareProfile,
    // which returns nothing (-> ENOENT) until a version is configured or derived.
    private const int KernFwVersionPs5 = 46;

    // PS4/PS5 userspace page size. Not invented for this call: the memory
    // compat layer already models the guest page as 0x4000, and the loader
    // aligns guest segments to it. HW_PAGESIZE must agree with that model.
    private const int OrbisPageSizeBytes = 0x4000;

    /// <summary>
    /// One resolvable OID. <see cref="Read"/> yields the raw little-endian bytes
    /// the kernel would place in <c>oldp</c>; a null result means the node is
    /// known but currently unreadable and is reported as <c>ENOENT</c>.
    /// </summary>
    private sealed record MibNode(int[] Name, string DebugName, Func<byte[]?> Read);

    // Additional OIDs are added here; the resolver and ABI below are unaffected.
    private static readonly MibNode[] MibTable =
    [
        new(
            [CtlHw, HwPagesize],
            "hw.pagesize",
            static () => Int32Value(OrbisPageSizeBytes)),
        new(
            [CtlKern, KernFwVersionPs5],
            "kern.fw_version_ps5",
            static () => KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out var version)
                ? Int32Value(unchecked((int)version))
                : null),
    ];

    private static byte[] Int32Value(int value)
    {
        var buffer = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        return buffer;
    }

    [SysAbiExport(
        Nid = "SSYvJ0oHilM",
        ExportName = "__sysctl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libKernel")]
    public static int Sysctl(CpuContext ctx) => SysctlCore(ctx);

    // Userspace sysctl(3) has the identical (name, namelen, oldp, oldlenp, newp,
    // newlen) ABI and, for the OIDs we serve, the identical behaviour; the libc
    // wrapper's only extra job on FreeBSD is a handful of user-space-computed
    // OIDs we do not implement. Sharing the core is therefore correct.
    [SysAbiExport(
        Nid = "DFmMT80xcNI",
        ExportName = "sysctl",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libSceLibcInternal")]
    public static int PosixSysctl(CpuContext ctx) => SysctlCore(ctx);

    /// <summary>
    /// FreeBSD <c>__sysctl(const int *name, u_int namelen, void *oldp,
    /// size_t *oldlenp, const void *newp, size_t newlen)</c>. Returns 0 on
    /// success, or -1 with the guest errno slot set on failure, which is the
    /// convention both the import path and the raw-syscall gateway expect.
    /// </summary>
    private static int SysctlCore(CpuContext ctx)
    {
        var nameAddress = ctx[CpuRegister.Rdi];
        var nameLength = unchecked((uint)ctx[CpuRegister.Rsi]);
        var oldAddress = ctx[CpuRegister.Rdx];
        var oldLenAddress = ctx[CpuRegister.Rcx];
        var newAddress = ctx[CpuRegister.R8];
        var newLength = ctx[CpuRegister.R9];

        // A write is requested only when newp is non-null; newlen is the size of
        // that buffer and is otherwise ignored, exactly as the kernel treats it.
        // (Real callers leave garbage in the high bits of newlen — a libkernel
        // wrapper zeroing it with a 32-bit store into a 64-bit slot — so keying
        // off newlen would wrongly reject an ordinary read.) Every OID we serve
        // is read-only, so an actual write attempt is EPERM, not a silent no-op.
        if (newAddress != 0)
        {
            return Fail(ctx, Eperm);
        }

        _ = newLength;

        if (nameLength == 0 || nameLength > CtlMaxName)
        {
            return Fail(ctx, Einval);
        }

        var name = new int[nameLength];
        for (var index = 0; index < name.Length; index++)
        {
            if (!ctx.TryReadInt32(nameAddress + (ulong)(index * sizeof(int)), out name[index]))
            {
                return Fail(ctx, Efault);
            }
        }

        if (!TryResolve(name, out var data))
        {
            return Fail(ctx, Enoent);
        }

        var required = (ulong)data.Length;

        // oldp without a length is unusable, matching the kernel's EFAULT.
        if (oldAddress != 0 && oldLenAddress == 0)
        {
            return Fail(ctx, Efault);
        }

        if (oldLenAddress != 0)
        {
            if (!ctx.TryReadUInt64(oldLenAddress, out var available))
            {
                return Fail(ctx, Efault);
            }

            if (oldAddress != 0)
            {
                if (available < required)
                {
                    // The caller's buffer is too small: report the size it
                    // needs and fail, exactly as the kernel does.
                    _ = ctx.TryWriteUInt64(oldLenAddress, required);
                    return Fail(ctx, Enomem);
                }

                if (!ctx.Memory.TryWrite(oldAddress, data))
                {
                    return Fail(ctx, Efault);
                }
            }

            // Both the read and the size-query paths report the actual size.
            if (!ctx.TryWriteUInt64(oldLenAddress, required))
            {
                return Fail(ctx, Efault);
            }
        }

        ctx[CpuRegister.Rax] = 0;
        return 0;
    }

    private static bool TryResolve(ReadOnlySpan<int> name, out byte[] data)
    {
        data = Array.Empty<byte>();
        foreach (var node in MibTable)
        {
            if (!NameEquals(node.Name, name))
            {
                continue;
            }

            var value = node.Read();
            if (value is null)
            {
                return false;
            }

            data = value;
            return true;
        }

        return false;
    }

    private static bool NameEquals(int[] candidate, ReadOnlySpan<int> name)
    {
        if (candidate.Length != name.Length)
        {
            return false;
        }

        for (var index = 0; index < candidate.Length; index++)
        {
            if (candidate[index] != name[index])
            {
                return false;
            }
        }

        return true;
    }

    private static int Fail(CpuContext ctx, int errno)
    {
        KernelRuntimeCompatExports.TrySetErrno(ctx, errno);
        ctx[CpuRegister.Rax] = unchecked((ulong)-1L);
        return -1;
    }
}
