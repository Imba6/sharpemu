// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.IO;
using System.IO.Compression;

using SharpEmu.HLE;
using SharpEmu.Libs.VideoOut;

using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

// Verifies the CPU scan-out detile helper. The tiled fixture below was produced OFFLINE
// by SharpProspero's AgcTiler (AgcTileMode.RenderTarget == GFX10 SW_64KB_R_X, swizzle 27)
// from a sparse 128x128 BGRA8 image of unique per-pixel markers. Tiling is a bijection, so
// detiling it with the production GnmTiling path must return the exact same sparse image
// only if VirtualPS5 inverts precisely the swizzle SharpProspero produced -- a cross-oracle
// check that the assumed scan-out layout (mode 27) is correct. Provenance: generated with
// SharpProspero as a reference oracle; no SharpProspero code is referenced by this test.
public sealed class VideoOutScanoutDetileTests
{
    private const int Width = 128;
    private const int Height = 128;
    private const int Bpp = 4;

    // Diverse (x,y) grid the fixture marks; covers intra-tile swizzle bits and block edges.
    private static readonly int[] Coords =
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 15, 16, 17, 31, 32, 33, 48, 63, 64, 65, 80, 96, 111, 120, 126, 127 };

    // gzip(base64) of the SharpProspero-tiled sparse 128x128 render-target buffer (64 KiB).
    private const string TiledFixtureGzipB64 =
        "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";

    // gzip(base64) of a SharpProspero-tiled 300x260 render-target buffer (padded to
    // 384x384 = 3x3 blocks), so block indexing across block rows/columns is exercised.
    private const string MultiBlockTiledGzipB64 =
        "H4sIAAAAAAACA+3dUY6cMAwA0GTnOBwiuRnmZDkaRZW2GvjYbRZQp5P3NPMRKf/YxtgppcjbP33K2zk/nQEAAAAAgO+VFLmorwMAAADAaEra1wWLPlwAAACAU4o+LAAARlNT5CoOBgAAAACAU+YUeVZvB/aK93AAAAAAlyrqsAAAAAyiHmYOVzOHAQAA4Hy+beYsAAAAAAAADGE+9OLOenEBAK6Nt/RiAQAAAPDmqp1DAAAAcH2+becMAACwmb2LAwC4N95SiwUAAGAQWwIcy9Pcwcjb2dxBAAAAAADoEiXFYu48AAAAAAynHfZ/N324AAAAAKc0fVgAAAwmaorF3gEAAAAAADgl5hSLvXPAQfMeDgAAAOBSTR0WAAAAAAAAAAAAAAAAAIAvrGn/PeqafZ8KAHBpvGX/NwAAAAAAAAAAAAAAP7BW/egAALfGW/Z/AwAAMIy8z4FzlhMDAMOYDrHQJBYCAADgp4qcEoCxTZ6FAAAAAAAAAABcaKr6UQAYhGceAAAAN5tmuScAMAAxDwAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAFwvUuTt/+e8bOfl6QwAAAAAAHyvpchNfR0AAAAARlNiXxcs+nABAAAATin6sAAAAAAAAAAA6LWmyKu+EwAAAACAOxV1WAAAAAZRDzOHq5nDAAAAcD7fNnMWAAAAAAAAhjAfenFnvbgAANfGW3qxAAAAAAAAAADoVO2cAQAAAAC43awWCwAAwCAiUixPcwdj2X7mDgIAAAAAQJdoKRZz5wEAAABgOO2w/7vpwwUAAAA4penDAgAAAAAAAACgU6wpFnvnAAAAAABu1dRhAQAAAAAAAAAAAAAAAAD4whr771HXxfepAACXxlv2fwMAAAAAAAAAAAAAAADAy1nt/wYAAGAUkfc58JLlxADAMKZDLDSJhQAAAPipJqcEYGyTZyEAAAAAAAAAAAAAAAAAvJxp9f0fADAAMQ8AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAADc4CPy9v/r64/t/qPjPgAAAAAAAAAAAAC8qZL6+mqLPlwAAAAAAAAAAAAAAAAAAAAAAAAAAAB4SbVz5nA1cxgAAAAAAAAAAAAAfps7e3FnvbgAAAAAAAAAAAAAAAAAAAAAAAAA8F+I9BFLx9zBeGz3zR0EAAAAAAAAAAAAgNQ69383fbgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAALyRNX1E1/1H330AAAAAAAAAAAAAAAAAAAAAAAAA4F95RN/1zvsAAC9s6oyFJrEQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA8MZ+AdfrMaIAAAkA";

    private static readonly int[] MbXs = { 0, 1, 63, 64, 127, 128, 129, 191, 255, 256, 299 };
    private static readonly int[] MbYs = { 0, 1, 63, 64, 127, 128, 129, 191, 255, 256, 259 };
    private const int MbWidth = 300;
    private const int MbHeight = 260;

    private const ulong MemoryBase = 0xC_0000_0000;
    private const ulong BufferAddress = MemoryBase + 0x1000;

    private static uint Marker(int x, int y) => 0x80000000u | ((uint)y << 8) | (uint)x;

    private static byte[] ExpectedSparseLinear()
    {
        var linear = new byte[Width * Height * Bpp];
        foreach (var y in Coords)
        foreach (var x in Coords)
        {
            var m = Marker(x, y);
            var o = (y * Width + x) * Bpp;
            linear[o + 0] = (byte)(m & 0xFF);
            linear[o + 1] = (byte)((m >> 8) & 0xFF);
            linear[o + 2] = (byte)((m >> 16) & 0xFF);
            linear[o + 3] = (byte)((m >> 24) & 0xFF);
        }

        return linear;
    }

    private static byte[] DecodeTiledFixture()
    {
        var gz = System.Convert.FromBase64String(TiledFixtureGzipB64);
        using var input = new MemoryStream(gz);
        using var stream = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private static VideoOutExports.DisplayBufferInfo Buffer(uint tilingMode, uint pitch) =>
        new(BufferAddress, 0x8000000000000000UL /* BGRA8 sRGB */, tilingMode, Width, Height, pitch);

    [Fact]
    public void Tiled_Detiles_ToOriginalSparseImage()
    {
        var tiled = DecodeTiledFixture();
        Assert.Equal(64 * 1024, tiled.Length);

        var memory = new FakeCpuMemory(MemoryBase, 0x20000);
        Assert.True(memory.TryWrite(BufferAddress, tiled));

        var linear = new byte[Width * Height * Bpp];
        byte[]? scratch = null;
        var status = VideoOutScanoutDetile.TryReadLinearPixels(
            Buffer(tilingMode: 0, pitch: Width), memory, linear, ref scratch);

        Assert.Equal(VideoOutScanoutDetile.ScanoutReadStatus.Ok, status);
        Assert.Equal(ExpectedSparseLinear(), linear);
    }

    [Fact]
    public void Linear_TightlyPacked_ReadsDirectly()
    {
        var expected = ExpectedSparseLinear();
        var memory = new FakeCpuMemory(MemoryBase, 0x20000);
        Assert.True(memory.TryWrite(BufferAddress, expected));

        var linear = new byte[Width * Height * Bpp];
        byte[]? scratch = null;
        var status = VideoOutScanoutDetile.TryReadLinearPixels(
            Buffer(tilingMode: 1, pitch: Width), memory, linear, ref scratch);

        Assert.Equal(VideoOutScanoutDetile.ScanoutReadStatus.Ok, status);
        Assert.Equal(expected, linear);
        Assert.Null(scratch); // linear path never allocates a tiled scratch
    }

    [Fact]
    public void Linear_PaddedPitch_IsUnsupported()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x20000);
        var linear = new byte[Width * Height * Bpp];
        byte[]? scratch = null;
        var status = VideoOutScanoutDetile.TryReadLinearPixels(
            Buffer(tilingMode: 1, pitch: Width + 64), memory, linear, ref scratch);

        Assert.Equal(VideoOutScanoutDetile.ScanoutReadStatus.Unsupported, status);
    }

    [Fact]
    public void UnknownTilingMode_IsUnsupported()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x20000);
        var linear = new byte[Width * Height * Bpp];
        byte[]? scratch = null;
        var status = VideoOutScanoutDetile.TryReadLinearPixels(
            Buffer(tilingMode: 2, pitch: Width), memory, linear, ref scratch);

        Assert.Equal(VideoOutScanoutDetile.ScanoutReadStatus.Unsupported, status);
    }

    [Fact]
    public void Tiled_UnmappedMemory_IsFault()
    {
        // A tiny region cannot hold the 64 KiB tiled buffer, so the read faults.
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var linear = new byte[Width * Height * Bpp];
        byte[]? scratch = null;
        var status = VideoOutScanoutDetile.TryReadLinearPixels(
            Buffer(tilingMode: 0, pitch: Width), memory, linear, ref scratch);

        Assert.Equal(VideoOutScanoutDetile.ScanoutReadStatus.Fault, status);
    }
    private static uint MbMarker(int x, int y) =>
        0x80000000u | ((uint)(y & 0xFF) << 8) | (uint)(x & 0xFF)
        | ((uint)((x >> 8) & 1) << 16) | ((uint)((y >> 8) & 1) << 17);

    [Fact]
    public void Tiled_MultiBlock_DetilesCorrectlyAcrossBlocks()
    {
        var gz = System.Convert.FromBase64String(MultiBlockTiledGzipB64);
        using var input = new MemoryStream(gz);
        using var stream = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        stream.CopyTo(output);
        var tiled = output.ToArray();
        Assert.Equal(384 * 384 * 4, tiled.Length); // 3x3 blocks

        var linear = new byte[MbWidth * MbHeight * Bpp];
        VideoOutScanoutDetile.Detile32Bpp(tiled, linear, MbWidth, MbHeight);

        foreach (var y in MbYs)
        foreach (var x in MbXs)
        {
            var o = (y * MbWidth + x) * Bpp;
            var got = (uint)(linear[o] | (linear[o + 1] << 8) | (linear[o + 2] << 16) | (linear[o + 3] << 24));
            Assert.Equal(MbMarker(x, y), got);
        }
    }
}

