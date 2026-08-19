// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;

namespace SharpEmu.Libs.VideoOut;

/// <summary>
/// Reads a registered VideoOut scan-out framebuffer out of guest memory as tightly
/// packed linear pixels, detiling a CPU-produced tiled buffer when the display was
/// registered tiled.
/// </summary>
/// <remarks>
/// <para>
/// This is a Vulkan/Metal-independent helper so the presenter's CPU display-buffer
/// bootstrap/refresh and the unit tests share one implementation. It does not mutate
/// guest memory and never faults the host on malformed metadata.
/// </para>
/// <para>
/// The only tiled layout supported is the PS5 main scan-out layout the display uses
/// for an uncompressed 32-bit render target: GFX10 <c>SW_64KB_R_X</c> (AGC tile mode
/// <c>RenderTarget</c>, 2D, single sample, one mip). That is exactly what a
/// SharpProspero application registers. The deswizzle equation below is the AMD GFX10
/// address-library <c>R_X</c> swizzle specialised to 32 bits/element, and is verified
/// byte-exactly against a SharpProspero-tiled fixture (see the unit tests). Any other
/// tiled layout is reported <see cref="ScanoutReadStatus.Unsupported"/> rather than
/// being misread.
/// </para>
/// <para>
/// The existing <c>GnmTiling</c> texture detiler is intentionally NOT reused here: its
/// mode-27 equation does not invert the render-target tiling this scan-out buffer was
/// produced with, so it would present a scrambled frame.
/// </para>
/// </remarks>
internal static class VideoOutScanoutDetile
{
    // sceVideoOut tiling-mode field values (SCE_VIDEO_OUT_TILING_MODE_*).
    private const uint TilingModeTiled = 0;
    private const uint TilingModeLinear = 1;

    // The supported scan-out element size: 32-bit BGRA/RGBA. The presenter validates
    // the concrete pixel layout before calling; the byte permutation itself is
    // channel-order agnostic and only needs the element size.
    private const int BytesPerPixel = 4;

    // SW_64KB_R_X 32bpp block: 64 KiB holds a 128x128 grid of 4-byte elements.
    private const int BlockWidth = 128;
    private const int BlockHeight = 128;
    private const int BlockBytes = 64 * 1024;

    internal enum ScanoutReadStatus
    {
        /// <summary>Linear pixels were produced into the destination.</summary>
        Ok,

        /// <summary>The layout/metadata is not one this helper handles.</summary>
        Unsupported,

        /// <summary>A guest read went out of bounds; nothing usable was produced.</summary>
        Fault,
    }

    /// <summary>
    /// Fills <paramref name="linearDestination"/> (which must be exactly
    /// <c>width * height * 4</c> bytes) with tightly packed linear pixels for the
    /// registered display buffer, detiling when it was registered tiled.
    /// <paramref name="tiledScratch"/> is a caller-owned reusable buffer for the raw
    /// tiled bytes (grown as needed) so a per-frame refresh avoids reallocation.
    /// </summary>
    internal static ScanoutReadStatus TryReadLinearPixels(
        in VideoOutExports.DisplayBufferInfo buffer,
        ICpuMemory guestMemory,
        Span<byte> linearDestination,
        ref byte[]? tiledScratch)
    {
        var width = buffer.Width;
        var height = buffer.Height;
        if (width == 0 || height == 0)
        {
            return ScanoutReadStatus.Unsupported;
        }

        // width*height*4 must fit an int (matches the caller's destination sizing).
        var linearByteCount = (ulong)width * height * (ulong)BytesPerPixel;
        if (linearByteCount > int.MaxValue ||
            linearDestination.Length != (int)linearByteCount)
        {
            return ScanoutReadStatus.Unsupported;
        }

        switch (buffer.TilingMode)
        {
            case TilingModeLinear:
                // Only a tightly packed linear buffer maps straight to the output;
                // a padded pitch would need a per-row copy we do not do here.
                if (buffer.PitchInPixel != width)
                {
                    return ScanoutReadStatus.Unsupported;
                }

                return guestMemory.TryRead(buffer.Address, linearDestination)
                    ? ScanoutReadStatus.Ok
                    : ScanoutReadStatus.Fault;

            case TilingModeTiled:
                return TryDetileScanout(buffer, guestMemory, linearDestination, ref tiledScratch);

            default:
                return ScanoutReadStatus.Unsupported;
        }
    }

    /// <summary>
    /// The number of bytes a tiled SW_64KB_R_X 32bpp scan-out surface of this size
    /// occupies in guest memory: whole 64 KiB blocks over the padded extent.
    /// </summary>
    internal static bool TryGetTiledByteCount(uint width, uint height, out long byteCount)
    {
        byteCount = 0;
        if (width == 0 || height == 0)
        {
            return false;
        }

        long blocksWide = ((long)width + BlockWidth - 1) / BlockWidth;
        long blocksHigh = ((long)height + BlockHeight - 1) / BlockHeight;
        var total = blocksWide * blocksHigh * BlockBytes;
        if (total <= 0 || total > int.MaxValue)
        {
            return false;
        }

        byteCount = total;
        return true;
    }

    private static ScanoutReadStatus TryDetileScanout(
        in VideoOutExports.DisplayBufferInfo buffer,
        ICpuMemory guestMemory,
        Span<byte> linearDestination,
        ref byte[]? tiledScratch)
    {
        var width = (int)buffer.Width;
        var height = (int)buffer.Height;

        if (!TryGetTiledByteCount(buffer.Width, buffer.Height, out var tiledByteCount))
        {
            return ScanoutReadStatus.Unsupported;
        }

        var tiledLength = (int)tiledByteCount;
        if (tiledScratch is null || tiledScratch.Length < tiledLength)
        {
            tiledScratch = GC.AllocateUninitializedArray<byte>(tiledLength);
        }

        var tiled = tiledScratch.AsSpan(0, tiledLength);
        if (!guestMemory.TryRead(buffer.Address, tiled))
        {
            return ScanoutReadStatus.Fault;
        }

        Detile32Bpp(tiled, linearDestination, width, height);
        return ScanoutReadStatus.Ok;
    }

    // The within-block byte offset for every element of a 128x128 block is the same
    // for any SW_64KB_R_X 32bpp surface, so build it once. Index by (inBlockY*128 +
    // inBlockX); the value is the swizzled byte offset inside the 64 KiB block.
    private static int[]? _blockOffsetTable;

    private static int[] BlockOffsetTable()
    {
        var table = _blockOffsetTable;
        if (table is not null)
        {
            return table;
        }

        table = new int[BlockWidth * BlockHeight];
        for (int inBlockY = 0; inBlockY < BlockHeight; inBlockY++)
        {
            int baseY = BaseOffsetY(inBlockY);
            int row = inBlockY * BlockWidth;
            for (int inBlockX = 0; inBlockX < BlockWidth; inBlockX++)
            {
                table[row + inBlockX] = ColumnOffsetX(inBlockX) ^ baseY;
            }
        }

        _blockOffsetTable = table;
        return table;
    }

    /// <summary>
    /// Deswizzles a SW_64KB_R_X 32bpp surface into tightly packed linear pixels.
    /// The tiled byte offset of element (x, y) is the whole-block base plus the
    /// swizzled offset within the 64 KiB block; the block is a 128x128 element grid,
    /// so higher bits of x/y select the block and the low bits index the precomputed
    /// within-block offset table. The source offset is provably in [0, tiled.Length)
    /// for a whole-block-sized tiled buffer, so the inner loop copies each 4-byte
    /// pixel directly with no per-pixel bounds check.
    /// </summary>
    internal static unsafe void Detile32Bpp(ReadOnlySpan<byte> tiled, Span<byte> linear, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return;
        }

        int blocksPerRow = (width + BlockWidth - 1) / BlockWidth;
        int blocksPerCol = (height + BlockHeight - 1) / BlockHeight;

        // Whole-block source and tightly packed destination must both be present;
        // validated up front so the hot loop is bounds-check free.
        long requiredTiled = (long)blocksPerRow * blocksPerCol * BlockBytes;
        long requiredLinear = (long)width * height * BytesPerPixel;
        if (tiled.Length < requiredTiled || linear.Length < requiredLinear)
        {
            return;
        }

        int[] table = BlockOffsetTable();

        fixed (byte* tiledBase = tiled)
        fixed (byte* linearBase = linear)
        fixed (int* tablePtr = table)
        {
            for (int y = 0; y < height; y++)
            {
                long blockRow = (long)(y >> 7) * blocksPerRow;
                int* tableRow = tablePtr + ((y & 127) << 7); // (y&127)*128
                uint* dst = (uint*)(linearBase + (long)y * width * BytesPerPixel);

                // Blocks are 128 wide; walk each 128-column span with a fixed block base.
                int x = 0;
                while (x < width)
                {
                    long blockBase = (blockRow + (x >> 7)) << 16;
                    byte* blockPtr = tiledBase + blockBase;
                    int inBlockX = x & 127;
                    int spanEnd = x - inBlockX + BlockWidth;
                    if (spanEnd > width)
                    {
                        spanEnd = width;
                    }

                    for (; x < spanEnd; x++, inBlockX++)
                    {
                        dst[x] = *(uint*)(blockPtr + tableRow[inBlockX]);
                    }
                }
            }
        }
    }

    // AMD GFX10 AddrLib R_X swizzle, 32bpp, 2D single-sample: the within-block byte
    // offset is ColumnOffsetX(x) ^ BaseOffsetY(y). These fold the low bits of x and y
    // into the byte offset; the terms are the address-library equation for this format
    // and are checked byte-exactly against a SharpProspero-tiled fixture in the tests.
    private static int ColumnOffsetX(int x) =>
        ((x << 2) & 0x000C) ^
        ((x << 5) & 0x0380) ^
        ((x << 4) & 0x0400) ^
        ((x << 6) & 0x0800) ^
        ((x << 9) & 0xA000);

    private static int BaseOffsetY(int y) =>
        ((y << 4) & 0x0070) ^
        ((y << 5) & 0x0F00) ^
        ((y << 9) & 0x1000) ^
        ((y << 8) & 0x4000);
}
