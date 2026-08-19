// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Core.Cpu;

internal static class RuntimeStubNids
{
    public const string BootstrapBridge = "__internal_bootstrap_bridge";

    public const string KernelDynlibDlsym = "__internal_kernel_dynlib_dlsym";

    /// <summary>NID of the public <c>sceKernelDlsym</c> libKernel export.</summary>
    public const string SceKernelDlsym = "LwG8g3niqwA";

    /// <summary>
    /// Reserved import entry backing the guest raw-syscall gateway. Not a guest
    /// symbol: it exists so the gateway reuses the import dispatch path.
    /// </summary>
    public const string RawSyscallGateway = "__internal_raw_syscall_gateway";
}
