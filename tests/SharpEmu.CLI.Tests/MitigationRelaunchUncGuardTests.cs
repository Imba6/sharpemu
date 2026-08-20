// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.CLI;
using Xunit;

namespace SharpEmu.CLI.Tests;

// Regression for the GRIS (PPSA09804) silent-exit: launched via `dotnet run`
// from a \\wsl.localhost working copy, the Windows CET/CFG mitigation relaunch
// (Program.TryRunMitigatedChild) tore the parent down inside CreateProcessW —
// Windows does not support a UNC working directory for a new process — so the
// emulator printed a single "[DEBUG] SharpEmu starting" line and exited 0 with
// no window and no guest. The relaunch is now skipped when the process image
// or working directory is a UNC path, running the guest in-process instead
// (the same model Linux/macOS use). This locks in the UNC predicate that gates
// that decision; a local drive path must still take the relaunch.
public sealed class MitigationRelaunchUncGuardTests
{
    [Theory]
    [InlineData(@"\\wsl.localhost\Ubuntu\home\illia\projects\virtualps5\SharpEmu.exe")]
    [InlineData(@"\\server\share\SharpEmu.exe")]
    [InlineData("//server/share/SharpEmu")]
    [InlineData(@"\/mixed/separators")]
    public void UncPaths_SkipTheMitigationRelaunch(string path)
    {
        Assert.True(Program.IsUncPath(path));
    }

    [Theory]
    [InlineData(@"C:\Program Files\SharpEmu\SharpEmu.exe")]
    [InlineData(@"D:\games\SharpEmu.exe")]
    [InlineData("/home/illia/projects/virtualps5/SharpEmu")]
    [InlineData(@"\single-leading-separator")]
    [InlineData("relative\\path")]
    [InlineData("")]
    [InlineData(null)]
    public void LocalPaths_KeepTheMitigationRelaunch(string? path)
    {
        Assert.False(Program.IsUncPath(path));
    }
}
