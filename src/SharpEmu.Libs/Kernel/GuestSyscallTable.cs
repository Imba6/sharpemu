// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;

namespace SharpEmu.Libs.Kernel;

/// <summary>
/// How a syscall's backing HLE export reports failure, so the raw-syscall
/// dispatcher can produce the FreeBSD <c>(carry, errno)</c> pair without
/// guessing per call site.
/// </summary>
public enum GuestSyscallResultConvention
{
    /// <summary>
    /// The export cannot fail: whatever it leaves in RAX is the syscall value
    /// and carry is always clear. Only valid for syscalls FreeBSD itself
    /// documents as never failing (<c>getpid</c>, <c>getuid</c>, ...).
    /// </summary>
    Infallible,

    /// <summary>
    /// The export follows the libc convention the POSIX-named libKernel exports
    /// use: a negative return means failure, and the errno has already been
    /// stored in the guest's per-thread errno slot (the one <c>__error</c>
    /// returns). The dispatcher converts that back into the raw
    /// <c>(carry, errno)</c> pair a syscall reports.
    /// </summary>
    PosixErrno,
}

/// <param name="Number">FreeBSD/Orbis syscall number, as passed in RAX.</param>
/// <param name="Name">Canonical syscall name, for diagnostics.</param>
/// <param name="Nid">NID of the HLE export that implements the behaviour.</param>
/// <param name="ArgumentCount">Syscall arguments actually consumed.</param>
public readonly record struct GuestSyscallDescriptor(
    int Number,
    string Name,
    string Nid,
    int ArgumentCount,
    GuestSyscallResultConvention ResultConvention);

/// <summary>
/// Guest syscall number to HLE export mapping for the raw-syscall gateway.
/// </summary>
/// <remarks>
/// This is deliberately a lookup table and nothing else: the gateway, the
/// register ABI and the errno translation all live in the CPU backend and never
/// change as entries are added here. A syscall number that is absent is not an
/// error in this table — it is an <c>ENOSYS</c> the dispatcher reports to the
/// guest, so growing PS5 coverage is a matter of adding rows once the matching
/// HLE export exists.
///
/// Numbers follow the FreeBSD 9/11 <c>syscalls.master</c> the Orbis/Prospero
/// kernel inherits.
/// </remarks>
public static class GuestSyscallTable
{
    private static readonly GuestSyscallDescriptor[] Descriptors =
    [
        new(4, "write", "FN4gaPmuFV8", 3, GuestSyscallResultConvention.PosixErrno),
        new(20, "getpid", "HoLVWNanBBc", 0, GuestSyscallResultConvention.Infallible),
    ];

    private static readonly Dictionary<int, GuestSyscallDescriptor> ByNumber = BuildByNumber();

    private static readonly Dictionary<string, GuestSyscallDescriptor> ByNid = BuildByNid();

    /// <summary>FreeBSD <c>ENOSYS</c>, reported for every unmapped number.</summary>
    public const int ENOSYS = 78;

    /// <summary>NID of libKernel <c>__error</c>, which returns the errno slot.</summary>
    public const string ErrnoAddressNid = "9BcDykPmo1I";

    public static bool TryGetByNumber(int number, out GuestSyscallDescriptor descriptor) =>
        ByNumber.TryGetValue(number, out descriptor);

    /// <summary>
    /// True when an export is a libkernel syscall wrapper, i.e. one whose
    /// address a caller may legitimately offset by 0x0A to reach the shared
    /// <c>syscall</c> entry.
    /// </summary>
    public static bool TryGetByExportNid(string nid, out GuestSyscallDescriptor descriptor)
    {
        if (string.IsNullOrWhiteSpace(nid))
        {
            descriptor = default;
            return false;
        }

        return ByNid.TryGetValue(nid, out descriptor);
    }

    private static Dictionary<int, GuestSyscallDescriptor> BuildByNumber()
    {
        var result = new Dictionary<int, GuestSyscallDescriptor>(Descriptors.Length);
        foreach (var descriptor in Descriptors)
        {
            result[descriptor.Number] = descriptor;
        }

        return result;
    }

    private static Dictionary<string, GuestSyscallDescriptor> BuildByNid()
    {
        var result = new Dictionary<string, GuestSyscallDescriptor>(Descriptors.Length, StringComparer.Ordinal);
        foreach (var descriptor in Descriptors)
        {
            result[descriptor.Nid] = descriptor;
        }

        return result;
    }
}
