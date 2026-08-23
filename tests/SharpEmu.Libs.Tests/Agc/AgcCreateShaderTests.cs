// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using Xunit;

namespace SharpEmu.Libs.Tests.Agc;

// sceAgcCreateShader header validation / relocation-boundary coverage. These pin
// the failure return codes so the improved SHARPEMU_LOG_AGC_SHADER diagnostics
// (which name the exact failing relocation field) do not drift from the control
// flow they annotate. The shader-header magic/version are the stable on-disk
// format constants (ShaderFileHeader=0x34333231, ShaderVersion=0x18).
public sealed class AgcCreateShaderTests
{
    private const uint ShaderFileHeader = 0x34333231;
    private const uint ShaderVersion = 0x18;

    [Fact]
    public void RejectsNullHeaderOrCode()
    {
        var ctx = new CpuContext(new FakeCpuMemory(0x3_0000_0000, 0x1000), Generation.Gen5);
        ctx[CpuRegister.Rdi] = 0;                 // destination
        ctx[CpuRegister.Rsi] = 0;                 // header == 0
        ctx[CpuRegister.Rdx] = 0x3_0000_0800;     // code
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.CreateShader(ctx));
    }

    [Fact]
    public void RejectsInvalidMagic()
    {
        const ulong Base = 0x3_0000_0000;
        var ctx = new CpuContext(new FakeCpuMemory(Base, 0x1000), Generation.Gen5);
        Assert.True(ctx.TryWriteUInt32(Base, 0xDEADBEEF));          // bad magic
        Assert.True(ctx.TryWriteUInt32(Base + 4, ShaderVersion));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = Base;
        ctx[CpuRegister.Rdx] = Base + 0x800;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            AgcExports.CreateShader(ctx));
    }

    // Valid magic/version, but the header is mapped only up to offset 0x18, so the
    // first pointer-field relocation (cx@0x18) reads out of bounds -> MEMORY_FAULT.
    // This is the boundary the improved trace now labels precisely.
    [Fact]
    public void ReturnsMemoryFaultWhenHeaderTruncatedBeforeFirstRelocation()
    {
        const ulong Base = 0x4_0000_0000;
        var ctx = new CpuContext(new FakeCpuMemory(Base, 0x18), Generation.Gen5);
        Assert.True(ctx.TryWriteUInt32(Base, ShaderFileHeader));
        Assert.True(ctx.TryWriteUInt32(Base + 4, ShaderVersion));

        ctx[CpuRegister.Rdi] = 0;
        ctx[CpuRegister.Rsi] = Base;
        ctx[CpuRegister.Rdx] = Base + 0x10;   // code addr inside the mapped region
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            AgcExports.CreateShader(ctx));
    }
}
