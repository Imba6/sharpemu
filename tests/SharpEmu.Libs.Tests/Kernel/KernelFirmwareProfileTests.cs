// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;

using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The firmware profile is process-global static state, so these tests configure
// it explicitly and reset afterwards rather than running in parallel with each
// other.
[Collection(nameof(KernelFirmwareProfileTests))]
[CollectionDefinition(nameof(KernelFirmwareProfileTests), DisableParallelization = true)]
public sealed class KernelFirmwareProfileTests : IDisposable
{
    private const ulong Base = 0x4_0000_0000;
    private const ulong NameAddress = Base + 0x100;
    private const ulong OldAddress = Base + 0x200;
    private const ulong OldLenAddress = Base + 0x300;

    // FreeBSD MIB {CTL_KERN, 46} — the PS5 firmware-version node.
    private static readonly int[] FwVersionMib = [1, 46];

    private const int Enoent = 2;
    private const int Enomem = 12;

    public void Dispose() => KernelFirmwareProfile.Reset();

    [Fact]
    public void ExplicitVersion_WinsOverAuto()
    {
        KernelFirmwareProfile.Configure(explicitVersion: 0x07610000, autoSdkPs5Version: 0x05000000);

        Assert.True(KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out var version));
        Assert.Equal(0x07610000u, version);
    }

    [Fact]
    public void Auto_UsesGuestSdkPs5Version_WhenNoExplicit()
    {
        KernelFirmwareProfile.Configure(explicitVersion: null, autoSdkPs5Version: 0x07000000);

        Assert.True(KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out var version));
        Assert.Equal(0x07000000u, version);
    }

    [Fact]
    public void Neither_IsUnavailable()
    {
        KernelFirmwareProfile.Configure(explicitVersion: null, autoSdkPs5Version: null);

        Assert.False(KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out _));
    }

    [Fact]
    public void Reconfigure_ExplicitThenEmpty_DoesNotLeakIntoNextExecution()
    {
        // A run with an explicit firmware value...
        KernelFirmwareProfile.Configure(explicitVersion: 0x07610000, autoSdkPs5Version: null);
        Assert.True(KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out var version));
        Assert.Equal(0x07610000u, version);

        // ...followed by a run with neither an explicit env value nor a guest
        // sdk_ps5_ver — exactly what SharpEmuRuntime.ConfigureFirmwareProfile
        // passes when both sources are absent. The prior explicit value must
        // not survive into this execution.
        KernelFirmwareProfile.Configure(explicitVersion: null, autoSdkPs5Version: null);
        Assert.False(KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out var leaked));
        Assert.Equal(0u, leaked);
    }

    [Fact]
    public void AutoZero_IsTreatedAsAbsent()
    {
        KernelFirmwareProfile.Configure(explicitVersion: null, autoSdkPs5Version: 0);

        Assert.False(KernelFirmwareProfile.TryGetPresentedFirmwareVersion(out _));
    }

    [Theory]
    [InlineData("0x07610000", 0x07610000u)]
    [InlineData("0x07000000", 0x07000000u)]
    [InlineData("117506048", 0x07010000u)]
    public void TryParseConfiguredVersion_AcceptsHexAndDecimal(string text, uint expected)
    {
        Assert.True(KernelFirmwareProfile.TryParseConfiguredVersion(text, out var version));
        Assert.Equal(expected, version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void TryParseConfiguredVersion_RejectsGarbage(string? text)
    {
        Assert.False(KernelFirmwareProfile.TryParseConfiguredVersion(text, out _));
    }

    [Fact]
    public void Kern46_ExplicitVersion_IsReturnedThroughSysctl()
    {
        KernelFirmwareProfile.Configure(explicitVersion: 0x07610000, autoSdkPs5Version: null);
        var ctx = NewFwVersionQuery(sizeof(int), out var memory);

        var result = KernelSysctlCompatExports.Sysctl(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal((ulong)sizeof(int), ReadUInt64(memory, OldLenAddress));
        Assert.Equal(0x07610000u, ReadUInt32(memory, OldAddress));
    }

    [Fact]
    public void Kern46_Auto_IsReturnedThroughSysctl()
    {
        KernelFirmwareProfile.Configure(explicitVersion: null, autoSdkPs5Version: 0x07000000);
        var ctx = NewFwVersionQuery(sizeof(int), out var memory);

        var result = KernelSysctlCompatExports.Sysctl(ctx);

        Assert.Equal(0, result);
        Assert.Equal(0x07000000u, ReadUInt32(memory, OldAddress));
    }

    [Fact]
    public void Kern46_SizeQuery_ReportsWidthWithoutBuffer()
    {
        KernelFirmwareProfile.Configure(explicitVersion: 0x07610000, autoSdkPs5Version: null);
        var ctx = NewFwVersionQuery(0xdead, out var memory);
        ctx[CpuRegister.Rdx] = 0; // oldp == NULL

        var result = KernelSysctlCompatExports.Sysctl(ctx);

        Assert.Equal(0, result);
        Assert.Equal((ulong)sizeof(int), ReadUInt64(memory, OldLenAddress));
    }

    [Fact]
    public void Kern46_BufferTooSmall_ReportsRequiredSizeAndFails()
    {
        KernelFirmwareProfile.Configure(explicitVersion: 0x07610000, autoSdkPs5Version: null);
        var ctx = NewFwVersionQuery(available: 2, out var memory);

        var result = KernelSysctlCompatExports.Sysctl(ctx);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        Assert.Equal((ulong)sizeof(int), ReadUInt64(memory, OldLenAddress));
    }

    [Fact]
    public void Kern46_Unavailable_FailsWithoutFabricatingValue()
    {
        KernelFirmwareProfile.Configure(explicitVersion: null, autoSdkPs5Version: null);
        var ctx = NewFwVersionQuery(sizeof(int), out var memory);

        var result = KernelSysctlCompatExports.Sysctl(ctx);

        Assert.Equal(-1, result);
        Assert.Equal(ulong.MaxValue, ctx[CpuRegister.Rax]);
        // The output buffer must be left untouched — no invented version.
        Assert.Equal(0u, ReadUInt32(memory, OldAddress));
    }

    private static CpuContext NewFwVersionQuery(ulong available, out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(Base, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        Span<byte> nameBytes = stackalloc byte[FwVersionMib.Length * sizeof(int)];
        for (var index = 0; index < FwVersionMib.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(nameBytes.Slice(index * sizeof(int)), FwVersionMib[index]);
        }

        Assert.True(memory.TryWrite(NameAddress, nameBytes));
        Span<byte> lenBytes = stackalloc byte[sizeof(ulong)];
        BinaryPrimitives.WriteUInt64LittleEndian(lenBytes, available);
        Assert.True(memory.TryWrite(OldLenAddress, lenBytes));

        ctx[CpuRegister.Rdi] = NameAddress;
        ctx[CpuRegister.Rsi] = (ulong)FwVersionMib.Length;
        ctx[CpuRegister.Rdx] = OldAddress;
        ctx[CpuRegister.Rcx] = OldLenAddress;
        ctx[CpuRegister.R8] = 0;
        ctx[CpuRegister.R9] = 0;
        return ctx;
    }

    private static ulong ReadUInt64(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt64LittleEndian(bytes);
    }

    private static uint ReadUInt32(FakeCpuMemory memory, ulong address)
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        Assert.True(memory.TryRead(address, bytes));
        return BinaryPrimitives.ReadUInt32LittleEndian(bytes);
    }
}
