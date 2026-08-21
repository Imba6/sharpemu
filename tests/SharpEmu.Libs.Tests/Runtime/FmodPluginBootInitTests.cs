// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Generic;
using SharpEmu.Core.Runtime;
using Xunit;

namespace SharpEmu.Libs.Tests.Runtime;

// Regression for the Cocoon (PPSA08766) startup crash at guest RIP 0x8096BF27E: an FMOD
// audio plugin (libresonanceaudio.prx) that lives under Media/Plugins is deferred
// (StartAtBoot=false) on the assumption the guest LoadStartModule's it. FMOD instead loads
// it by dlsym'ing its FMODGetPluginDescriptionList entry point and calling the returned
// descriptor directly, so DT_INIT never runs and its hash-map global is read with a NULL
// buckets pointer, faulting. SharpEmuRuntime promotes any deferred module exporting the
// FMOD plugin ABI to start-at-boot; ExportsFmodPluginAbi is that decision.
public sealed class FmodPluginBootInitTests
{
    private const string FmodPluginAbiNid = "44sreXeHo5Q";
    private const string FmodPluginAbiName = "FMODGetPluginDescriptionList";

    [Fact]
    public void ExportsFmodPluginAbi_IsTrue_WhenExportsKeyedByNid()
    {
        // How the PS5 export table actually keys libresonanceaudio.prx's entry point.
        var exports = new Dictionary<string, ulong>
        {
            [FmodPluginAbiNid] = 0x809669F10,
            ["someOtherNid"] = 0x809670000,
        };

        Assert.True(SharpEmuRuntime.ExportsFmodPluginAbi(exports));
    }

    [Fact]
    public void ExportsFmodPluginAbi_IsTrue_WhenExportsKeyedByPlainName()
    {
        var exports = new Dictionary<string, ulong>
        {
            [FmodPluginAbiName] = 0x809669F10,
        };

        Assert.True(SharpEmuRuntime.ExportsFmodPluginAbi(exports));
    }

    [Fact]
    public void ExportsFmodPluginAbi_IsFalse_ForAnOrdinaryPlugin()
    {
        // A non-audio plugin the guest really does LoadStartModule (e.g. a save-data or PSN
        // helper) must stay deferred: promoting it would run its constructors at boot.
        var exports = new Dictionary<string, ulong>
        {
            ["PrxInitialize"] = 0x805000000,
            ["PrxSaveDataMount"] = 0x805001000,
        };

        Assert.False(SharpEmuRuntime.ExportsFmodPluginAbi(exports));
    }

    [Fact]
    public void ExportsFmodPluginAbi_IsFalse_ForEmptyExports()
    {
        Assert.False(SharpEmuRuntime.ExportsFmodPluginAbi(new Dictionary<string, ulong>()));
    }
}
