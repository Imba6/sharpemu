// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

namespace SharpEmu.Libs.LibcStrerror;

/// <summary>
/// libc <c>strerror</c> / <c>strerror_r</c>: map a FreeBSD errno to its message.
/// The message table mirrors the FreeBSD <c>sys_errlist</c>; an errno outside the
/// known range renders as "Unknown error: N", as the C library does.
/// </summary>
public static class LibcStrerrorExports
{
    private const int Einval = 22;
    private const int Erange = 34;

    // FreeBSD sys_errlist (sys/errno.h order). Index == errno; index 0 is the
    // "no error" slot the C library keeps for strerror(0).
    private static readonly string[] Messages =
    {
        /*  0 */ "Undefined error: 0",
        /*  1 */ "Operation not permitted",
        /*  2 */ "No such file or directory",
        /*  3 */ "No such process",
        /*  4 */ "Interrupted system call",
        /*  5 */ "Input/output error",
        /*  6 */ "Device not configured",
        /*  7 */ "Argument list too long",
        /*  8 */ "Exec format error",
        /*  9 */ "Bad file descriptor",
        /* 10 */ "No child processes",
        /* 11 */ "Resource deadlock avoided",
        /* 12 */ "Cannot allocate memory",
        /* 13 */ "Permission denied",
        /* 14 */ "Bad address",
        /* 15 */ "Block device required",
        /* 16 */ "Device busy",
        /* 17 */ "File exists",
        /* 18 */ "Cross-device link",
        /* 19 */ "Operation not supported by device",
        /* 20 */ "Not a directory",
        /* 21 */ "Is a directory",
        /* 22 */ "Invalid argument",
        /* 23 */ "Too many open files in system",
        /* 24 */ "Too many open files",
        /* 25 */ "Inappropriate ioctl for device",
        /* 26 */ "Text file busy",
        /* 27 */ "File too large",
        /* 28 */ "No space left on device",
        /* 29 */ "Illegal seek",
        /* 30 */ "Read-only file system",
        /* 31 */ "Too many links",
        /* 32 */ "Broken pipe",
        /* 33 */ "Numerical argument out of domain",
        /* 34 */ "Result too large",
        /* 35 */ "Resource temporarily unavailable",
        /* 36 */ "Operation now in progress",
        /* 37 */ "Operation already in progress",
        /* 38 */ "Socket operation on non-socket",
        /* 39 */ "Destination address required",
        /* 40 */ "Message too long",
        /* 41 */ "Protocol wrong type for socket",
        /* 42 */ "Protocol not available",
        /* 43 */ "Protocol not supported",
        /* 44 */ "Socket type not supported",
        /* 45 */ "Operation not supported",
        /* 46 */ "Protocol family not supported",
        /* 47 */ "Address family not supported by protocol family",
        /* 48 */ "Address already in use",
        /* 49 */ "Can't assign requested address",
        /* 50 */ "Network is down",
        /* 51 */ "Network is unreachable",
        /* 52 */ "Network dropped connection on reset",
        /* 53 */ "Software caused connection abort",
        /* 54 */ "Connection reset by peer",
        /* 55 */ "No buffer space available",
        /* 56 */ "Socket is already connected",
        /* 57 */ "Socket is not connected",
        /* 58 */ "Can't send after socket shutdown",
        /* 59 */ "Too many references: can't splice",
        /* 60 */ "Operation timed out",
        /* 61 */ "Connection refused",
        /* 62 */ "Too many levels of symbolic links",
        /* 63 */ "File name too long",
        /* 64 */ "Host is down",
        /* 65 */ "No route to host",
        /* 66 */ "Directory not empty",
        /* 67 */ "Too many processes",
        /* 68 */ "Too many users",
        /* 69 */ "Disc quota exceeded",
        /* 70 */ "Stale NFS file handle",
        /* 71 */ "Too many levels of remote in path",
        /* 72 */ "RPC struct is bad",
        /* 73 */ "RPC version wrong",
        /* 74 */ "RPC prog. not avail",
        /* 75 */ "Program version wrong",
        /* 76 */ "Bad procedure for program",
        /* 77 */ "No locks available",
        /* 78 */ "Function not implemented",
        /* 79 */ "Inappropriate file type or format",
        /* 80 */ "Authentication error",
        /* 81 */ "Need authenticator",
        /* 82 */ "Identifier removed",
        /* 83 */ "No message of desired type",
        /* 84 */ "Value too large to be stored in data type",
        /* 85 */ "Operation canceled",
        /* 86 */ "Illegal byte sequence",
        /* 87 */ "Attribute not found",
        /* 88 */ "Programming error",
        /* 89 */ "Bad message",
        /* 90 */ "Multihop attempted",
        /* 91 */ "Link has been severed",
        /* 92 */ "Protocol error",
        /* 93 */ "Capabilities insufficient",
        /* 94 */ "Not permitted in capability mode",
        /* 95 */ "State not recoverable",
        /* 96 */ "Previous owner died",
    };

    // Guest-resident copy of each message returned by strerror, one persistent
    // buffer per distinct message text (known errnos share the constant-string
    // semantics; unknown errnos each format their own "Unknown error: N").
    private static readonly ConcurrentDictionary<string, ulong> _messageBuffers = new();

    internal static bool IsKnownErrno(int errnum) => errnum >= 0 && errnum < Messages.Length;

    internal static string MessageFor(int errnum) => IsKnownErrno(errnum)
        ? Messages[errnum]
        : $"Unknown error: {errnum.ToString(CultureInfo.InvariantCulture)}";

    [SysAbiExport(
        Nid = "RIa6GnWp+iU",
        ExportName = "strerror",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strerror(CpuContext ctx)
    {
        var errnum = unchecked((int)ctx[CpuRegister.Rdi]);
        var message = MessageFor(errnum);

        // strerror returns a pointer to a string with static storage duration, so
        // materialize each distinct message once in guest memory and reuse it. A
        // failed allocation is not cached so a later call can retry.
        if (!_messageBuffers.TryGetValue(message, out var address))
        {
            address = MaterializeGuestString(ctx, message);
            if (address != 0)
            {
                address = _messageBuffers.GetOrAdd(message, address);
            }
        }

        ctx[CpuRegister.Rax] = address;
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }

    [SysAbiExport(
        Nid = "RBcs3uut1TA",
        ExportName = "strerror_r",
        Target = Generation.Gen4 | Generation.Gen5,
        LibraryName = "libc")]
    public static int Strerror_r(CpuContext ctx)
    {
        var errnum = unchecked((int)ctx[CpuRegister.Rdi]);
        var buffer = ctx[CpuRegister.Rsi];
        var bufLen = ctx[CpuRegister.Rdx];

        var message = MessageFor(errnum);
        var bytes = Encoding.ASCII.GetBytes(message);

        // XSI strerror_r: EINVAL for an unknown errno, ERANGE when the buffer is too
        // small to hold the message and its terminator (ERANGE overrides EINVAL). The
        // buffer is filled with as much of the message as fits, always terminated.
        var result = IsKnownErrno(errnum) ? 0 : Einval;

        if (buffer == 0 || bufLen == 0)
        {
            // No room even for the terminator.
            SetReturn(ctx, Erange);
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        var copyLen = (int)Math.Min((ulong)bytes.Length, bufLen - 1);

        // Write the fitting prefix and the terminator in one span so a bad buffer
        // pointer fails cleanly instead of faulting the host.
        var outBytes = new byte[copyLen + 1];
        Array.Copy(bytes, outBytes, copyLen);
        if (!ctx.Memory.TryWrite(buffer, outBytes))
        {
            SetReturn(ctx, Einval); // bad buffer pointer -> report an error, do not fault
            return (int)OrbisGen2Result.ORBIS_GEN2_OK;
        }

        if ((ulong)bytes.Length + 1 > bufLen)
        {
            result = Erange; // truncated
        }

        return SetReturn(ctx, result);
    }

    private static ulong MaterializeGuestString(CpuContext ctx, string message)
    {
        var bytes = Encoding.ASCII.GetBytes(message);
        if (!KernelMemoryCompatExports.TryAllocateHleData(
                ctx,
                (ulong)bytes.Length + 1,
                alignment: 0x10,
                out var address))
        {
            return 0;
        }

        // Length+1 array is zero-filled, so the trailing terminator is already set.
        var stored = new byte[bytes.Length + 1];
        Array.Copy(bytes, stored, bytes.Length);
        return ctx.Memory.TryWrite(address, stored) ? address : 0;
    }

    private static int SetReturn(CpuContext ctx, int value)
    {
        ctx[CpuRegister.Rax] = unchecked((ulong)(uint)value);
        return (int)OrbisGen2Result.ORBIS_GEN2_OK;
    }
}
