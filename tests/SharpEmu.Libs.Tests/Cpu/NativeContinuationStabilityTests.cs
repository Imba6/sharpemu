// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using SharpEmu.Core.Cpu.Native;
using SharpEmu.Core.Cpu.Native.Windows;
using SharpEmu.HLE;
using Xunit;

namespace SharpEmu.Libs.Tests.Cpu;

// CPU-backend stability milestone: guards the two failure modes seen when a
// guest thread blocks through HLE and resumes via
// DirectExecutionBackend.ExecuteBlockedGuestThreadContinuation -> CallNativeEntry:
//   1. "Invalid Program: attempted to call a UnmanagedCallersOnly method from
//      managed code" — the CLR fail-fast when a cooperative-GC-mode fault is
//      allowed to enter the managed vectored-exception handler. Mitigated by the
//      native VEH pre-filter (WindowsFaultCodes.NonManagedVehPreFilterCodes).
//   2. An AVX store access violation (vmovups [r15]) — a stale/dropped register
//      surfacing across the block/resume register transfer (ApplyGuestContinuation).
public sealed class NativeContinuationStabilityTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const int MemorySize = 0x1000;

    // --- Failure mode 1: the VEH pre-filter must stay the single source of truth. ---
    // Both WindowsFaultHandling.CreateHandlerThunk and
    // DirectExecutionBackend.CreateExceptionHandlerTrampoline emit their pre-filter
    // from this one set; a divergence would let a cooperative-mode fault reach the
    // managed handler and reintroduce the "UnmanagedCallersOnly" fatal.
    [Fact]
    public void VehPreFilter_CoversEveryCooperativeModeFaultCode_InEmissionOrder()
    {
        var codes = WindowsFaultCodes.NonManagedVehPreFilterCodes.ToArray();

        // A throwing C# exception (RaiseException 0xE0434352), host MSVC C++
        // exceptions, FailFast and stack overflow all arrive while the thread may
        // be cooperative; none may enter the managed VEH.
        Assert.Equal(
            new uint[]
            {
                WindowsFaultCodes.ClrManagedException, // 0xE0434352
                WindowsFaultCodes.MsvcCppException,    // 0xE06D7363
                WindowsFaultCodes.FastFail,            // 0xC0000409
                WindowsFaultCodes.StackOverflow,       // 0xC00000FD
            },
            codes);

        // Exact numeric values are load-bearing (compared against EXCEPTION_RECORD
        // .ExceptionCode inside the emitted thunk); pin them so a rename can't drift.
        Assert.Equal(0xE0434352u, WindowsFaultCodes.ClrManagedException);
        Assert.Equal(0xE06D7363u, WindowsFaultCodes.MsvcCppException);
        Assert.Equal(0xC0000409u, WindowsFaultCodes.FastFail);
        Assert.Equal(0xC00000FDu, WindowsFaultCodes.StackOverflow);
    }

    // --- Failure mode 2: block/resume must transfer the entire integer register file. ---
    // ExecuteBlockedGuestThreadContinuation copies the captured GuestCpuContinuation
    // into the live CpuContext via ApplyGuestContinuation before re-entering native
    // guest code. If any register is dropped, the guest resumes with a stale value —
    // exactly the class that produced the vmovups [r15] store fault (r15 is fully
    // guest-controlled and only reaches guest code through this transfer).
    [Fact]
    public void ApplyGuestContinuation_TransfersEveryIntegerRegisterAndControlWord()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, MemorySize), Generation.Gen5);

        // Distinct, non-zero sentinels so a copy/paste that reuses one field's value
        // for another register is caught, and so control-word/segment defaulting
        // (applied only when the captured value is zero) never masks a real transfer.
        var continuation = new GuestCpuContinuation
        {
            Rip = 0x2000_0000_0000_0001,
            Rsp = 0x2000_0000_0000_0002,
            Rflags = 0x2000_0000_0000_0003,
            FsBase = 0x2000_0000_0000_0004,
            GsBase = 0x2000_0000_0000_0005,
            Rax = 0x2000_0000_0000_0010,
            Rcx = 0x2000_0000_0000_0011,
            Rdx = 0x2000_0000_0000_0012,
            Rbx = 0x2000_0000_0000_0013,
            Rbp = 0x2000_0000_0000_0014,
            Rsi = 0x2000_0000_0000_0015,
            Rdi = 0x2000_0000_0000_0016,
            R8 = 0x2000_0000_0000_0017,
            R9 = 0x2000_0000_0000_0018,
            R10 = 0x2000_0000_0000_0019,
            R11 = 0x2000_0000_0000_001A,
            R12 = 0x2000_0000_0000_001B,
            R13 = 0x2000_0000_0000_001C,
            R14 = 0x2000_0000_0000_001D,
            R15 = 0x2000_0000_0000_001E,
            FpuControlWord = 0x0F7F,
            Mxcsr = 0x0000_9FC0,
        };

        DirectExecutionBackend.ApplyGuestContinuation(context, continuation);

        Assert.Equal(continuation.Rip, context.Rip);
        Assert.Equal(continuation.Rflags, context.Rflags);
        Assert.Equal(continuation.FsBase, context.FsBase);
        Assert.Equal(continuation.GsBase, context.GsBase);
        Assert.Equal(continuation.FpuControlWord, context.FpuControlWord);
        Assert.Equal(continuation.Mxcsr, context.Mxcsr);

        Assert.Equal(continuation.Rax, context[CpuRegister.Rax]);
        Assert.Equal(continuation.Rcx, context[CpuRegister.Rcx]);
        Assert.Equal(continuation.Rdx, context[CpuRegister.Rdx]);
        Assert.Equal(continuation.Rbx, context[CpuRegister.Rbx]);
        Assert.Equal(continuation.Rbp, context[CpuRegister.Rbp]);
        Assert.Equal(continuation.Rsi, context[CpuRegister.Rsi]);
        Assert.Equal(continuation.Rdi, context[CpuRegister.Rdi]);
        Assert.Equal(continuation.R8, context[CpuRegister.R8]);
        Assert.Equal(continuation.R9, context[CpuRegister.R9]);
        Assert.Equal(continuation.R10, context[CpuRegister.R10]);
        Assert.Equal(continuation.R11, context[CpuRegister.R11]);
        Assert.Equal(continuation.R12, context[CpuRegister.R12]);
        Assert.Equal(continuation.R13, context[CpuRegister.R13]);
        Assert.Equal(continuation.R14, context[CpuRegister.R14]);
        Assert.Equal(continuation.R15, context[CpuRegister.R15]);
        Assert.Equal(continuation.Rsp, context[CpuRegister.Rsp]);
    }

    // --- Failure mode 1 (static form): no managed call path may invoke a
    // [UnmanagedCallersOnly] method directly. C# refuses to compile such a call,
    // but hand-emitted IL or a future refactor to Reflection.Emit could; the CLR
    // then fatally fails with the exact "UnmanagedCallersOnly method from managed
    // code" message. This walks the IL of every method in the CPU-backend
    // assemblies and asserts none call/callvirt one of these methods directly. ---
    [Fact]
    public void NoManagedCode_DirectlyCalls_AnUnmanagedCallersOnlyMethod()
    {
        Assembly[] assemblies =
        {
            typeof(DirectExecutionBackend).Assembly, // SharpEmu.Core
            typeof(GuestThreadExecution).Assembly,   // SharpEmu.HLE
        };

        var unmanagedCallersOnly = new HashSet<(Module Module, int Token)>();
        var allMethods = new List<MethodBase>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in SafeGetTypes(assembly))
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
                foreach (var method in type.GetMethods(flags).Cast<MethodBase>()
                             .Concat(type.GetConstructors(flags)))
                {
                    allMethods.Add(method);
                    if (method.GetCustomAttribute<UnmanagedCallersOnlyAttribute>() is not null)
                    {
                        unmanagedCallersOnly.Add((method.Module, method.MetadataToken));
                    }
                }
            }
        }

        // Sanity: the reflection walk must actually see the known UCO methods
        // (RunPrologue / RunEpilogue / HandlePosixSignal). If this drops to zero the
        // test is silently vacuous.
        Assert.True(
            unmanagedCallersOnly.Count >= 3,
            $"expected to find the CPU-backend UnmanagedCallersOnly methods, found {unmanagedCallersOnly.Count}");

        var (singleByte, twoByte) = BuildOpcodeTables();
        var violations = new List<string>();

        foreach (var method in allMethods)
        {
            byte[]? il;
            try
            {
                il = method.GetMethodBody()?.GetILAsByteArray();
            }
            catch
            {
                continue; // abstract / runtime / no accessible body
            }

            if (il is null)
            {
                continue;
            }

            foreach (var callToken in EnumerateCallTokens(il, singleByte, twoByte))
            {
                MethodBase? callee = ResolveMethod(method, callToken);
                if (callee is null)
                {
                    continue;
                }

                if (unmanagedCallersOnly.Contains((callee.Module, callee.MetadataToken)))
                {
                    violations.Add(
                        $"{method.DeclaringType?.FullName}.{method.Name} directly calls " +
                        $"UnmanagedCallersOnly {callee.DeclaringType?.FullName}.{callee.Name}");
                }
            }
        }

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static (OpCode?[] SingleByte, OpCode?[] TwoByte) BuildOpcodeTables()
    {
        var singleByte = new OpCode?[256];
        var twoByte = new OpCode?[256];
        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
            {
                continue;
            }

            var value = (ushort)op.Value;
            if (op.Size == 1)
            {
                singleByte[value & 0xFF] = op;
            }
            else
            {
                twoByte[value & 0xFF] = op;
            }
        }

        return (singleByte, twoByte);
    }

    // Walks IL, yielding the metadata token operand of every call / callvirt.
    private static IEnumerable<int> EnumerateCallTokens(byte[] il, OpCode?[] singleByte, OpCode?[] twoByte)
    {
        var position = 0;
        while (position < il.Length)
        {
            OpCode? op;
            if (il[position] == 0xFE && position + 1 < il.Length)
            {
                op = twoByte[il[position + 1]];
                position += 2;
            }
            else
            {
                op = singleByte[il[position]];
                position += 1;
            }

            if (op is not { } opcode)
            {
                yield break; // unknown byte: stop rather than misparse operands
            }

            if ((opcode == OpCodes.Call || opcode == OpCodes.Callvirt) &&
                position + 4 <= il.Length)
            {
                yield return BitConverter.ToInt32(il, position);
            }

            position += OperandSize(opcode.OperandType, il, position);
        }
    }

    private static int OperandSize(OperandType operandType, byte[] il, int position)
    {
        switch (operandType)
        {
            case OperandType.InlineNone:
                return 0;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                return 1;
            case OperandType.InlineVar:
                return 2;
            case OperandType.InlineBrTarget:
            case OperandType.InlineField:
            case OperandType.InlineI:
            case OperandType.InlineMethod:
            case OperandType.InlineSig:
            case OperandType.InlineString:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            case OperandType.ShortInlineR:
                return 4;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                return 8;
            case OperandType.InlineSwitch:
                if (position + 4 > il.Length)
                {
                    return il.Length - position;
                }

                var count = BitConverter.ToInt32(il, position);
                return 4 + (count * 4);
            default:
                return 0;
        }
    }

    private static MethodBase? ResolveMethod(MethodBase context, int token)
    {
        try
        {
            var typeArgs = context.DeclaringType?.IsGenericType == true
                ? context.DeclaringType.GetGenericArguments()
                : null;
            var methodArgs = context is MethodInfo { IsGenericMethod: true } mi
                ? mi.GetGenericArguments()
                : null;
            return context.Module.ResolveMethod(token, typeArgs, methodArgs);
        }
        catch
        {
            return null; // token in another module / unresolvable generic instantiation
        }
    }
}
