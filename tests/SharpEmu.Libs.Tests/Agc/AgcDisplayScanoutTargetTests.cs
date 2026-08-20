// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Libs.Agc;
using SharpEmu.Libs.VideoOut;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// Regression for the Unpacking (PPSA07117) blank-screen blocker, and the generic
// AGC->VideoOut scanout bridge it exercised.
//
// A Unity-style engine renders the scene to an offscreen color target (0x6...) and
// flips a SEPARATE registered VideoOut scanout buffer (0x10000000/0x10870000) with a
// targetless final blit that omits the CB colour registers. That scanout buffer is
// never a known render target, so at flip time we synthesize its colour descriptor
// from the registered VideoOut display-buffer attributes and composite the blit into
// it. These tests pin that synthesis: correct address/size, PS5-correct scanout
// colour format, and a normal failure for an unusable descriptor.
public sealed class AgcDisplayScanoutTargetTests
{
    // Unpacking's scanout format (0x8000000000000000 = 2B8G8R8A8_SRGB); also the
    // classic 0x80000000 = A8R8G8B8_SRGB. Both are 8-bit sRGB.
    private const ulong EightBitSrgb = 0x8000000000000000UL;
    private const ulong EightBitSrgbClassic = 0x80000000UL;
    private const ulong TenBitPacked = 0x88060000UL; // A2R10G10B10

    [Theory]
    [InlineData(EightBitSrgb)]
    [InlineData(EightBitSrgbClassic)]
    public void SynthesizesEightBitSrgbScanoutTarget(ulong pixelFormat)
    {
        var displayBuffer = new VideoOutExports.DisplayBufferInfo(
            Address: 0x10000000,
            PixelFormat: pixelFormat,
            TilingMode: 0,
            Width: 1920,
            Height: 1080,
            PitchInPixel: 1920);

        Assert.True(AgcExports.TryCreateScanoutRenderTarget(displayBuffer, out var target));
        Assert.Equal(0x10000000UL, target.Address);
        Assert.Equal(1920u, target.Width);
        Assert.Equal(1080u, target.Height);
        // CB colour format 10 + numberType 9 decodes to R8G8B8A8_SRGB.
        Assert.Equal(10u, target.Format);
        Assert.Equal(9u, target.NumberType);
    }

    [Fact]
    public void SynthesizesTenBitPackedScanoutTarget()
    {
        var displayBuffer = new VideoOutExports.DisplayBufferInfo(
            Address: 0x10870000,
            PixelFormat: TenBitPacked,
            TilingMode: 0,
            Width: 3840,
            Height: 2160,
            PitchInPixel: 3840);

        Assert.True(AgcExports.TryCreateScanoutRenderTarget(displayBuffer, out var target));
        Assert.Equal(0x10870000UL, target.Address);
        Assert.Equal(3840u, target.Width);
        Assert.Equal(2160u, target.Height);
        // CB colour format 9 decodes to A2R10G10B10.
        Assert.Equal(9u, target.Format);
        Assert.Equal(0u, target.NumberType);
    }

    [Theory]
    [InlineData(0UL, 1920u, 1080u)] // no address
    [InlineData(0x10000000UL, 0u, 1080u)] // no width
    [InlineData(0x10000000UL, 1920u, 0u)] // no height
    public void RejectsUnusableDisplayBuffer(ulong address, uint width, uint height)
    {
        var displayBuffer = new VideoOutExports.DisplayBufferInfo(
            Address: address,
            PixelFormat: EightBitSrgb,
            TilingMode: 0,
            Width: width,
            Height: height,
            PitchInPixel: width);

        Assert.False(AgcExports.TryCreateScanoutRenderTarget(displayBuffer, out _));
    }
}
