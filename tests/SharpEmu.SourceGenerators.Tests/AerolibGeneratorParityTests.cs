// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.SourceGenerators.Tests;

/// <summary>
/// The build-time aerolib.bin generator (SharpEmu.HLE/build/AerolibBinaryGenerator.build.cs,
/// compiled standalone by RoslynCodeTaskFactory) carries its own copy of the NID
/// derivation so it need not load the analyzer assembly at build time. This test pins
/// that copy to <see cref="Ps5Nid"/> across the whole embedded catalog, so any drift
/// between the two implementations fails the build rather than silently corrupting the
/// runtime NID -> name mapping.
/// </summary>
public sealed class AerolibGeneratorParityTests
{
    [Fact]
    public void EmbeddedCatalogNidsMatchAnalyzerAlgorithm()
    {
        var catalog = Aerolib.Instance.GetAllNidNames();

        Assert.NotEmpty(catalog);

        foreach (var (nid, exportName) in catalog)
        {
            Assert.Equal(nid, Ps5Nid.Compute(exportName));
        }
    }
}
