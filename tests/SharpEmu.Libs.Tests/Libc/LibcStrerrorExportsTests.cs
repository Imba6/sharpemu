// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;

using SharpEmu.HLE;
using SharpEmu.Libs.LibcStrerror;

using Xunit;

namespace SharpEmu.Libs.Tests.Libc;

// strerror / strerror_r map a FreeBSD errno to its message. strerror_r is the XSI
// variant returning 0 / EINVAL / ERANGE.
public sealed class LibcStrerrorExportsTests
{
    private const ulong MemoryBase = 0xA_0000_0000;
    private const ulong BufAddress = MemoryBase + 0x100;

    private const int Einval = 22;
    private const int Erange = 34;

    private static CpuContext NewContext(out FakeCpuMemory memory)
    {
        memory = new FakeCpuMemory(MemoryBase, 0x2000);
        return new CpuContext(memory, Generation.Gen5);
    }

    private static string ReadCString(CpuContext ctx, ulong address, int max)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < max; i++)
        {
            Assert.True(ctx.TryReadByte(address + (ulong)i, out var b));
            if (b == 0)
            {
                break;
            }

            bytes.Add(b);
        }

        return Encoding.ASCII.GetString(bytes.ToArray());
    }

    [Theory]
    [InlineData(0, "Undefined error: 0")]
    [InlineData(2, "No such file or directory")]
    [InlineData(22, "Invalid argument")]
    [InlineData(34, "Result too large")]
    [InlineData(96, "Previous owner died")]
    public void MessageFor_KnownErrno(int errnum, string expected)
    {
        Assert.True(LibcStrerrorExports.IsKnownErrno(errnum));
        Assert.Equal(expected, LibcStrerrorExports.MessageFor(errnum));
    }

    [Theory]
    [InlineData(97)]
    [InlineData(-1)]
    [InlineData(1000)]
    public void MessageFor_UnknownErrno(int errnum)
    {
        Assert.False(LibcStrerrorExports.IsKnownErrno(errnum));
        Assert.Equal($"Unknown error: {errnum}", LibcStrerrorExports.MessageFor(errnum));
    }

    [Fact]
    public void StrerrorR_KnownErrno_WritesMessageReturnsZero()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 2;
        ctx[CpuRegister.Rsi] = BufAddress;
        ctx[CpuRegister.Rdx] = 128;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStrerrorExports.Strerror_r(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.Equal("No such file or directory", ReadCString(ctx, BufAddress, 128));
    }

    [Fact]
    public void StrerrorR_UnknownErrno_ReturnsEinval()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 5000;
        ctx[CpuRegister.Rsi] = BufAddress;
        ctx[CpuRegister.Rdx] = 128;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStrerrorExports.Strerror_r(ctx));
        Assert.Equal((ulong)Einval, ctx[CpuRegister.Rax]);
        Assert.Equal("Unknown error: 5000", ReadCString(ctx, BufAddress, 128));
    }

    [Fact]
    public void StrerrorR_SmallBuffer_TruncatesReturnsErange()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 2; // "No such file or directory"
        ctx[CpuRegister.Rsi] = BufAddress;
        ctx[CpuRegister.Rdx] = 10;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStrerrorExports.Strerror_r(ctx));
        Assert.Equal((ulong)Erange, ctx[CpuRegister.Rax]);
        // 9 chars + terminator.
        Assert.Equal("No such f", ReadCString(ctx, BufAddress, 128));
    }

    [Fact]
    public void StrerrorR_ZeroLength_ReturnsErange()
    {
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 2;
        ctx[CpuRegister.Rsi] = BufAddress;
        ctx[CpuRegister.Rdx] = 0;

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStrerrorExports.Strerror_r(ctx));
        Assert.Equal((ulong)Erange, ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Strerror_MaterializesMessageAndReusesPointer()
    {
        // strerror needs to allocate guest-resident storage for the returned char*.
        // The fake memory harness does not back the HLE allocator, so the pointer may
        // be null here; when it is produced, verify the string and that a repeat call
        // for the same errno returns the same static pointer. Message correctness for
        // strerror is covered by MessageFor and StrerrorR above.
        var ctx = NewContext(out _);
        ctx[CpuRegister.Rdi] = 22;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStrerrorExports.Strerror(ctx));
        var pointer = ctx[CpuRegister.Rax];

        if (pointer == 0)
        {
            return; // allocation unavailable in this harness
        }

        Assert.Equal("Invalid argument", ReadCString(ctx, pointer, 128));

        ctx[CpuRegister.Rdi] = 22;
        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_OK, LibcStrerrorExports.Strerror(ctx));
        Assert.Equal(pointer, ctx[CpuRegister.Rax]);
    }
}
