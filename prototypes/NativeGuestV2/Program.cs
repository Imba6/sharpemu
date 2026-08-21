// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
//
// Native Guest Execution V2 prototype / stress harness.
//
// Reproduces, in isolation, the native-worker CLR/GC boundary proposed in
// docs/native-guest-execution-v2.md and stresses it far past concurrency 2:
//
//   raw OS thread (kernel32 CreateThread, NOT System.Threading.Thread)
//     -> emitted native worker loop (waits on a kernel event)
//        -> emitted native "guest body" (arbitrary native frames)
//           -> reverse-P/Invoke into managed HLE  [UnmanagedCallersOnly]
//              (allocates -> GC pressure; may request BLOCK / NEST / FINISH)
//           -> emitted native "nested guest" -> managed HLE   (host->guest->host)
//
// A pool of these workers is rented per guest slice; a guest that BLOCKs yields
// its worker and resumes on whatever worker is rented next slice (worker reuse /
// migration). A background thread hammers GC.Collect. The invariant under test:
// no CLR UnmanagedCallersOnly __fastfail while guest frames stay native-only,
// with block/resume and nested callbacks correct, at concurrency >> 2.

using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace NativeGuestV2;

internal static unsafe class Program
{
    // Guest -> HLE command codes (returned by HleStep, acted on by emitted guest body).
    private const int CmdContinue = 0;
    private const int CmdBlock = 2;
    private const int CmdNest = 3;
    private const int CmdFinish = 4;

    // ---- Win32 ----------------------------------------------------------------
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void* VirtualAlloc(void* addr, nuint size, uint type, uint protect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(void* addr, nuint size, uint newProtect, uint* oldProtect);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FlushInstructionCache(nint process, void* addr, nuint size);
    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateThread(nint attrs, nuint stackSize, nint start, nint param, uint flags, out uint tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateEventW(nint attrs, bool manualReset, bool initialState, nint name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetEvent(nint h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GetProcAddress(nint mod, [MarshalAs(UnmanagedType.LPStr)] string name);

    private const uint MEM_COMMIT_RESERVE = 0x3000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READ = 0x20;
    private const uint STACK_SIZE_PARAM_IS_A_RESERVATION = 0x00010000;

    // ---- Per-guest managed state ---------------------------------------------
    private sealed class GuestState
    {
        public long Steps;
        public long NestedCalls;
        public long Slices;
        public int TargetSteps;
        public volatile bool Finished;
        public object? Sink; // keep an allocation briefly alive to churn gen0/1
    }

    private static GuestState[] _guests = Array.Empty<GuestState>();
    private static int _blockInterval = 400;
    private static int _nestInterval = 37;
    private static bool _inlineMode;

    // Non-fatal correctness/health counters.
    private static long _totalHleCalls;
    private static long _totalNestedCalls;
    private static long _hleExceptions;

    // ---- Managed HLE (reverse-P/Invoke targets) ------------------------------
    // Called from emitted native "guest" code. This is the exact boundary that
    // __fastfails on the inline path when guest frames sit above managed frames;
    // here the frames below are native (emitted), so it must stay legal under GC.
    [UnmanagedCallersOnly]
    private static int HleStep(nint guestIndex)
    {
        try
        {
            var g = _guests[(int)guestIndex];
            var n = ++g.Steps;
            Interlocked.Increment(ref _totalHleCalls);

            // GC pressure: churn short-lived objects; occasionally retain one.
            var buf = new byte[64];
            buf[0] = (byte)n;
            if ((n & 0x3F) == 0)
            {
                g.Sink = buf;
            }

            if (n >= g.TargetSteps)
            {
                return CmdFinish;
            }
            if ((n % _blockInterval) == 0)
            {
                return CmdBlock;
            }
            if ((n % _nestInterval) == 0)
            {
                return CmdNest;
            }
            return CmdContinue;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _hleExceptions);
            return CmdFinish;
        }
    }

    [UnmanagedCallersOnly]
    private static int HleStepNested(nint guestIndex)
    {
        try
        {
            var g = _guests[(int)guestIndex];
            g.NestedCalls++;
            Interlocked.Increment(ref _totalNestedCalls);
            var buf = new byte[48];
            buf[0] = 1;
            g.Sink = buf;
            return CmdContinue;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref _hleExceptions);
            return CmdContinue;
        }
    }

    // ---- x86-64 emitter -------------------------------------------------------
    private sealed unsafe class Code
    {
        private byte* _p;
        private int _o;
        public Code(byte* p) { _p = p; _o = 0; }
        public int Offset => _o;
        public void U8(byte b) => _p[_o++] = b;
        public void U32(uint v) { *(uint*)(_p + _o) = v; _o += 4; }
        public void U64(ulong v) { *(ulong*)(_p + _o) = v; _o += 8; }
        public void Bytes(params byte[] bs) { foreach (var b in bs) _p[_o++] = b; }
        // records a rel32 site (operand start) to patch to an absolute target offset
        public int Rel32Site() { var s = _o; U32(0); return s; }
        public void PatchRel32(int site, int targetOffset) => *(int*)(_p + site) = targetOffset - (site + 4);
    }

    private static nint _waitForSingleObject;
    private static nint _setEvent;

    private static void ResolveKernel32()
    {
        var k = GetModuleHandleW("kernel32.dll");
        _waitForSingleObject = GetProcAddress(k, "WaitForSingleObject");
        _setEvent = GetProcAddress(k, "SetEvent");
        if (_waitForSingleObject == 0 || _setEvent == 0)
        {
            throw new InvalidOperationException("kernel32 resolve failed");
        }
    }

    // Emit guestBody(): loops calling HleStep(guestIndex); on NEST calls nestedStub;
    // returns the terminating command (BLOCK or FINISH) in eax.
    private static nint EmitGuestBody(long guestIndex, nint hleStep, nint nestedStub)
    {
        byte* mem = (byte*)VirtualAlloc(null, 256, MEM_COMMIT_RESERVE, PAGE_READWRITE);
        var c = new Code(mem);
        c.Bytes(0x48, 0x83, 0xEC, 0x28);                 // sub rsp,0x28
        int loop = c.Offset;
        c.Bytes(0x48, 0xB9); c.U64((ulong)guestIndex);   // mov rcx, guestIndex
        c.Bytes(0x48, 0xB8); c.U64((ulong)hleStep);      // mov rax, HleStep
        c.Bytes(0xFF, 0xD0);                             // call rax
        c.Bytes(0x83, 0xF8, (byte)CmdBlock);             // cmp eax, BLOCK
        c.Bytes(0x0F, 0x84); int jBlock = c.Rel32Site(); // je done
        c.Bytes(0x83, 0xF8, (byte)CmdFinish);            // cmp eax, FINISH
        c.Bytes(0x0F, 0x84); int jFinish = c.Rel32Site();// je done
        c.Bytes(0x83, 0xF8, (byte)CmdNest);              // cmp eax, NEST
        c.Bytes(0x0F, 0x85); int jLoop = c.Rel32Site();  // jne loop
        c.Bytes(0x48, 0xB9); c.U64((ulong)guestIndex);   // mov rcx, guestIndex
        c.Bytes(0x48, 0xB8); c.U64((ulong)nestedStub);   // mov rax, nestedStub
        c.Bytes(0xFF, 0xD0);                             // call rax
        c.Bytes(0xE9); int jLoop2 = c.Rel32Site();       // jmp loop
        int done = c.Offset;
        c.Bytes(0x48, 0x83, 0xC4, 0x28);                 // add rsp,0x28
        c.Bytes(0xC3);                                   // ret
        c.PatchRel32(jBlock, done);
        c.PatchRel32(jFinish, done);
        c.PatchRel32(jLoop, loop);
        c.PatchRel32(jLoop2, loop);
        Seal(mem, c.Offset);
        return (nint)mem;
    }

    private static nint EmitNestedStub(long guestIndex, nint hleNested)
    {
        byte* mem = (byte*)VirtualAlloc(null, 64, MEM_COMMIT_RESERVE, PAGE_READWRITE);
        var c = new Code(mem);
        c.Bytes(0x48, 0x83, 0xEC, 0x28);                 // sub rsp,0x28
        c.Bytes(0x48, 0xB9); c.U64((ulong)guestIndex);   // mov rcx, guestIndex
        c.Bytes(0x48, 0xB8); c.U64((ulong)hleNested);    // mov rax, HleStepNested
        c.Bytes(0xFF, 0xD0);                             // call rax
        c.Bytes(0x48, 0x83, 0xC4, 0x28);                 // add rsp,0x28
        c.Bytes(0xC3);                                   // ret
        Seal(mem, c.Offset);
        return (nint)mem;
    }

    // Emit the persistent worker loop. rcx (thread param) = control block:
    //   [+0x00]=workEvent [+0x08]=doneEvent [+0x10]=stopFlag(int)
    //   [+0x18]=guestStubPtr [+0x20]=lastResult(int)
    private static nint EmitWorkerLoop()
    {
        byte* mem = (byte*)VirtualAlloc(null, 256, MEM_COMMIT_RESERVE, PAGE_READWRITE);
        var c = new Code(mem);
        c.Bytes(0x53);                                   // push rbx
        c.Bytes(0x48, 0x89, 0xCB);                       // mov rbx, rcx
        c.Bytes(0x48, 0x83, 0xEC, 0x20);                 // sub rsp,0x20  (16-aligned calls)
        int loop = c.Offset;
        c.Bytes(0x48, 0x8B, 0x4B, 0x00);                 // mov rcx,[rbx+0x00] workEvent
        c.Bytes(0xBA); c.U32(0xFFFFFFFF);                // mov edx, INFINITE
        c.Bytes(0x48, 0xB8); c.U64((ulong)_waitForSingleObject);
        c.Bytes(0xFF, 0xD0);                             // call rax
        c.Bytes(0x8B, 0x43, 0x10);                       // mov eax,[rbx+0x10] stopFlag
        c.Bytes(0x85, 0xC0);                             // test eax,eax
        c.Bytes(0x0F, 0x85); int jStop = c.Rel32Site();  // jnz stop
        c.Bytes(0x48, 0x8B, 0x43, 0x18);                 // mov rax,[rbx+0x18] guestStubPtr
        c.Bytes(0xFF, 0xD0);                             // call rax   (guest body)
        c.Bytes(0x89, 0x43, 0x20);                       // mov [rbx+0x20],eax  lastResult
        c.Bytes(0x48, 0x8B, 0x4B, 0x08);                 // mov rcx,[rbx+0x08] doneEvent
        c.Bytes(0x48, 0xB8); c.U64((ulong)_setEvent);
        c.Bytes(0xFF, 0xD0);                             // call rax
        c.Bytes(0xE9); int jLoop = c.Rel32Site();        // jmp loop
        int stop = c.Offset;
        c.Bytes(0x48, 0x8B, 0x4B, 0x08);                 // mov rcx,[rbx+0x08] doneEvent
        c.Bytes(0x48, 0xB8); c.U64((ulong)_setEvent);
        c.Bytes(0xFF, 0xD0);                             // call rax
        c.Bytes(0x48, 0x83, 0xC4, 0x20);                 // add rsp,0x20
        c.Bytes(0x5B);                                   // pop rbx
        c.Bytes(0xC3);                                   // ret
        c.PatchRel32(jStop, stop);
        c.PatchRel32(jLoop, loop);
        Seal(mem, c.Offset);
        return (nint)mem;
    }

    private static void Seal(byte* mem, int len)
    {
        uint old;
        if (!VirtualProtect(mem, 256, PAGE_EXECUTE_READ, &old))
        {
            throw new InvalidOperationException("VirtualProtect failed");
        }
        FlushInstructionCache(GetCurrentProcess(), mem, (nuint)len);
    }

    // ---- Native worker (foreign OS thread + control block) --------------------
    private sealed unsafe class Worker
    {
        public byte* Cb;              // control block
        public nint WorkEvent;
        public nint DoneEvent;
        public nint ThreadHandle;

        public static Worker Create(nint loopStub)
        {
            var w = new Worker();
            w.Cb = (byte*)NativeMemory.AllocZeroed(0x40);
            w.WorkEvent = CreateEventW(0, false, false, 0);
            w.DoneEvent = CreateEventW(0, false, false, 0);
            *(nint*)(w.Cb + 0x00) = w.WorkEvent;
            *(nint*)(w.Cb + 0x08) = w.DoneEvent;
            *(int*)(w.Cb + 0x10) = 0;
            w.ThreadHandle = CreateThread(0, 1u << 20, loopStub, (nint)w.Cb,
                STACK_SIZE_PARAM_IS_A_RESERVATION, out _);
            if (w.ThreadHandle == 0)
            {
                throw new InvalidOperationException("CreateThread failed");
            }
            return w;
        }

        // Runs one guest slice on this worker; the calling (managed) thread parks
        // in WaitForSingleObject (preemptive) while the worker runs guest code.
        public int RunSlice(nint guestStub)
        {
            *(nint*)(Cb + 0x18) = guestStub;
            SetEvent(WorkEvent);
            WaitForSingleObject(DoneEvent, 0xFFFFFFFF);
            return *(int*)(Cb + 0x20);
        }

        public void Stop()
        {
            *(int*)(Cb + 0x10) = 1;
            SetEvent(WorkEvent);
            WaitForSingleObject(DoneEvent, 2000);
        }
    }

    private static readonly ConcurrentBag<Worker> _idle = new();
    private static nint _loopStub;
    private static int _liveWorkers;
    private static int _peakWorkers;
    private static int _maxWorkers;

    private static Worker RentWorker()
    {
        if (_idle.TryTake(out var w))
        {
            return w;
        }
        // Bounded grow-on-demand; spin briefly if at the cap (self-throttling).
        while (Volatile.Read(ref _liveWorkers) >= _maxWorkers)
        {
            if (_idle.TryTake(out var w2))
            {
                return w2;
            }
            Thread.SpinWait(200);
        }
        var created = Worker.Create(_loopStub);
        var live = Interlocked.Increment(ref _liveWorkers);
        int peak;
        do { peak = Volatile.Read(ref _peakWorkers); if (live <= peak) break; }
        while (Interlocked.CompareExchange(ref _peakWorkers, live, peak) != peak);
        return created;
    }

    private static void ReturnWorker(Worker w) => _idle.Add(w);

    // ---- Main -----------------------------------------------------------------
    private static int Main(string[] args)
    {
        int numGuests = ArgInt(args, "guests", 24);
        int poolCap = ArgInt(args, "pool", 32);
        int stepsPerGuest = ArgInt(args, "steps", 200_000);
        bool forceGc = ArgInt(args, "gc", 1) != 0;
        _blockInterval = ArgInt(args, "block", 400);
        _nestInterval = ArgInt(args, "nest", 37);
        _inlineMode = ArgInt(args, "inline", 0) != 0;
        _maxWorkers = poolCap;

        Console.WriteLine($"[V2] mode={(_inlineMode ? "INLINE-CONTROL(managed-thread calli)" : "NATIVE-WORKER")} " +
                          $"guests={numGuests} poolCap={poolCap} steps/guest={stepsPerGuest} " +
                          $"forceGC={forceGc} blockEvery={_blockInterval} nestEvery={_nestInterval} " +
                          $"serverGC={System.Runtime.GCSettings.IsServerGC}");

        ResolveKernel32();
        var hleStep = (nint)(delegate* unmanaged<nint, int>)&HleStep;
        var hleNested = (nint)(delegate* unmanaged<nint, int>)&HleStepNested;
        _loopStub = EmitWorkerLoop();

        _guests = new GuestState[numGuests];
        var guestStub = new nint[numGuests];
        for (int i = 0; i < numGuests; i++)
        {
            _guests[i] = new GuestState { TargetSteps = stepsPerGuest };
            var nested = EmitNestedStub(i, hleNested);
            guestStub[i] = EmitGuestBody(i, hleStep, nested);
        }

        // Pre-grow a few workers so first slices do not all create at once.
        for (int i = 0; i < Math.Min(4, poolCap); i++)
        {
            var w = Worker.Create(_loopStub);
            Interlocked.Increment(ref _liveWorkers);
            _idle.Add(w);
        }

        using var stop = new ManualResetEventSlim(false);
        long gcCount = 0;
        Thread? gcThread = null;
        if (forceGc)
        {
            gcThread = new Thread(() =>
            {
                while (!stop.IsSet)
                {
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    gcCount++;
                    Thread.SpinWait(500);
                }
            }) { IsBackground = true, Name = "gc-hammer" };
            gcThread.Start();
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // One managed driver thread per guest. Each drives its guest across slices,
        // renting a (possibly different) worker per slice -> worker reuse/migration.
        var drivers = new Thread[numGuests];
        for (int i = 0; i < numGuests; i++)
        {
            int gi = i;
            drivers[i] = new Thread(() =>
            {
                var g = _guests[gi];
                while (!g.Finished)
                {
                    int cmd;
                    if (_inlineMode)
                    {
                        // CONTROL: run the emitted guest body directly on THIS managed
                        // thread via an inline `delegate* unmanaged` calli -- exactly the
                        // production CallNativeEntry path. Arbitrary guest (emitted)
                        // frames now sit ABOVE this managed driver thread's frames on a
                        // GC-managed thread; under forced GC this is the FailFast source.
                        cmd = ((delegate* unmanaged[Cdecl]<int>)guestStub[gi])();
                    }
                    else
                    {
                        var w = RentWorker();
                        try
                        {
                            cmd = w.RunSlice(guestStub[gi]);
                        }
                        finally
                        {
                            ReturnWorker(w);
                        }
                    }
                    g.Slices++;
                    if (cmd == CmdFinish)
                    {
                        g.Finished = true;
                    }
                    // cmd == CmdBlock -> loop: resume (next rented worker, or re-enter inline).
                }
            }) { IsBackground = true, Name = $"driver-{i}" };
            drivers[i].Start();
        }

        foreach (var d in drivers) d.Join();
        sw.Stop();
        stop.Set();
        gcThread?.Join(2000);

        // Drain workers.
        foreach (var w in _idle)
        {
            try { w.Stop(); } catch { /* best effort */ }
        }

        // ---- Verify ----
        long totalSteps = 0, totalNested = 0, totalSlices = 0;
        bool allFinished = true;
        long expectedNestedPerGuest = stepsPerGuest / _nestInterval;
        // A step that is a multiple of both block and nest intervals returns BLOCK
        // (checked first) and skips the NEST, so the exact nested count is
        // expectedNestedPerGuest minus those collisions (~ steps / lcm). Bound the
        // shortfall by the number of blocks; require nested callbacks actually ran.
        long maxNestShortfall = (stepsPerGuest / _blockInterval) + 2;
        bool nestedOk = true;
        for (int i = 0; i < numGuests; i++)
        {
            var g = _guests[i];
            totalSteps += g.Steps;
            totalNested += g.NestedCalls;
            totalSlices += g.Slices;
            if (!g.Finished || g.Steps < stepsPerGuest) allFinished = false;
            if (g.NestedCalls < expectedNestedPerGuest - maxNestShortfall || g.NestedCalls == 0)
            {
                nestedOk = false;
            }
        }
        long expectedSlices = ((long)stepsPerGuest / _blockInterval) * numGuests;

        Console.WriteLine($"[V2] elapsed={sw.ElapsedMilliseconds}ms gcCollects={gcCount} " +
                          $"peakConcurrentWorkers={_peakWorkers} liveWorkers={_liveWorkers}");
        Console.WriteLine($"[V2] totalHleCalls={_totalHleCalls} totalNested={_totalNestedCalls} " +
                          $"totalSlices={totalSlices} (expected>= ~{expectedSlices}) hleExceptions={_hleExceptions}");
        Console.WriteLine($"[V2] allGuestsFinished={allFinished} nestedCountsOk={nestedOk} " +
                          $"stepsTotal={totalSteps} (expected={(long)stepsPerGuest * numGuests})");

        // The core invariant: ran to completion with correct block/resume + nested
        // counts and zero exceptions (i.e. no CLR __fastfail took the process down --
        // a FailFast would have killed us before this line). The native-worker mode
        // additionally proves concurrency > 2; the inline control just documents
        // whether the synthetic guest reproduces the crash.
        bool ranClean = allFinished && nestedOk && _hleExceptions == 0 &&
                        totalSteps == (long)stepsPerGuest * numGuests;
        bool pass = ranClean && (_inlineMode || _peakWorkers > 2);
        if (_inlineMode)
        {
            Console.WriteLine(ranClean
                ? "[V2] RESULT: PASS-INLINE (control ran clean under forced GC; this synthetic guest " +
                  "does NOT reproduce the inline __fastfail -- real crash needs genuine arbitrary guest code)"
                : "[V2] RESULT: FAIL-INLINE (control crashed/incorrect)");
        }
        else
        {
            Console.WriteLine(pass
                ? "[V2] RESULT: PASS (no UCO FailFast; block/resume + nested callbacks correct; concurrency>2 under forced GC)"
                : "[V2] RESULT: FAIL");
        }
        return pass ? 0 : 1;
    }

    private static int ArgInt(string[] args, string key, int dflt)
    {
        foreach (var a in args)
        {
            if (a.StartsWith("--" + key + "=", StringComparison.Ordinal) &&
                int.TryParse(a.Substring(key.Length + 3), out var v))
            {
                return v;
            }
        }
        return dflt;
    }
}
