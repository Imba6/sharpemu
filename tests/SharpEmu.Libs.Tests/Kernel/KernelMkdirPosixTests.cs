// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// The libc mkdir() export is the POSIX-ABI face of sceKernelMkdir: it must
// translate the guest path through the mount table (never touching an arbitrary
// host path), return 0 on success and -1/errno on failure, and report an
// existing target as EEXIST. DoomGeneric's M_MakeDirectory reaches it when it
// prepares its save/config directory.
[Collection(KernelMemoryCompatStateCollection.Name)]
public sealed class KernelMkdirPosixTests : IDisposable
{
    private const ulong MemoryBase = 0xF_0000_0000;

    private readonly string _tempRoot;
    private readonly string _saveRoot;

    public KernelMkdirPosixTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"sharpemu-mkdir-{Guid.NewGuid():N}");
        _saveRoot = Path.Combine(_tempRoot, "save");
        Directory.CreateDirectory(_saveRoot);
        // A writable mount (not /app0, which is read-only for mutation).
        KernelMemoryCompatExports.RegisterGuestPathMount("/savedata", _saveRoot);
    }

    public void Dispose()
    {
        KernelMemoryCompatExports.UnregisterGuestPathMount("/savedata");
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    private static (CpuContext ctx, FakeCpuMemory memory) NewContext()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        return (new CpuContext(memory, Generation.Gen5), memory);
    }

    [Fact]
    public void Mkdir_NewDirectory_ReturnsZeroAndCreatesIt()
    {
        var (ctx, memory) = NewContext();
        memory.WriteCString(MemoryBase, "/savedata/prboom");
        ctx[CpuRegister.Rdi] = MemoryBase;
        ctx[CpuRegister.Rsi] = 0x1FF; // mode 0777, advisory

        Assert.Equal(0, KernelMemoryCompatExports.PosixMkdir(ctx));
        Assert.Equal(0UL, ctx[CpuRegister.Rax]);
        Assert.True(Directory.Exists(Path.Combine(_saveRoot, "prboom")));
    }

    [Fact]
    public void Mkdir_ExistingDirectory_ReturnsMinusOneWithEexist()
    {
        var (ctx, memory) = NewContext();
        memory.WriteCString(MemoryBase, "/savedata/prboom");
        ctx[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(0, KernelMemoryCompatExports.PosixMkdir(ctx));
        // Second attempt on the same path is EEXIST -> -1, which POSIX callers
        // treat as "already there". (errno lands in a guest TLS slot that this
        // fake context does not back, so only the -1 result is observable here.)
        Assert.Equal(-1, KernelMemoryCompatExports.PosixMkdir(ctx));
        Assert.Equal(unchecked((ulong)(-1L)), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Mkdir_NullPointer_ReturnsMinusOne()
    {
        var (ctx, _) = NewContext();
        ctx[CpuRegister.Rdi] = 0;

        Assert.Equal(-1, KernelMemoryCompatExports.PosixMkdir(ctx));
        Assert.Equal(unchecked((ulong)(-1L)), ctx[CpuRegister.Rax]);
    }

    [Fact]
    public void Mkdir_UnmappedPath_IsDeniedNotCreatedOnHost()
    {
        var (ctx, memory) = NewContext();
        // An absolute guest path that matches no mount must resolve to nothing and
        // fail -- the guest can never create a directory at an arbitrary host path.
        memory.WriteCString(MemoryBase, "/etc/sharpemu-should-not-exist");
        ctx[CpuRegister.Rdi] = MemoryBase;

        Assert.Equal(-1, KernelMemoryCompatExports.PosixMkdir(ctx));
        Assert.False(Directory.Exists("/etc/sharpemu-should-not-exist"));
    }
}
