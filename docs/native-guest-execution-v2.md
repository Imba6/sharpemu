# Native Guest Execution V2 — architecture & prototype strategy

Status: design + prototype (branch `gpt-dlsym`, not pushed). This document is the
architecture and the minimal prototype plan; it precedes any production migration.

## 0. Problem statement (proven)

Top-level guest execution runs through `CallNativeEntry` (a `delegate* unmanaged`
inline `calli`) on **CLR-managed threads**:

- the main entry, via `ExecuteEntry` → `CallNativeEntry` (the primary managed thread), and
- ordinary guest pthreads, via `GuestExecutionRunner._thread = new Thread(...)` →
  `RunGuestThread` → `ExecuteGuestThreadEntry` → `CallNativeEntry`.

This places arbitrary guest x86-64 frames (no CLR unwind info) **above** CLR-managed
frames on a GC-managed thread. During sustained Unity/il2cpp workloads the .NET GC
suspends/hijacks these threads; walking across the guest frames corrupts the CLR
GC-mode bookkeeping, and the next guest→host reverse-P/Invoke into the import gateway
`__fastfail`s with *"attempted to call a UnmanagedCallersOnly method from managed
code."* Characterized in `cocoon-gameplay-uco-crash` (main thread, during ~30fps
gameplay and pre-gameplay, concurrent with the il2cpp Boehm GC stop-the-world). The
`__fastfail` (`int 29h`) bypasses the process VEH, so it cannot be trapped.

Ruled out earlier and NOT causes: continuation nonvolatile-register transfer (proven
correct), nested TLS/RSP preservation (correct), mutex mutual exclusion (correct),
unwind host-bottom-frame NOT_FOUND (benign), FMOD module init (fixed). Routing only
nested/re-entrant execution through native workers made it WORSE and broke the GC
stop-the-world handshake — the crash is on the TOP-LEVEL guest loop, not the nested
path.

**Goal:** an execution model where arbitrary guest frames NEVER sit above CLR-managed
frames, so the CLR GC never walks/hijacks through guest frames.

---

## 1. Audit of the existing native worker (`DirectExecutionBackend.NativeWorker.cs`)

The `tbb_thead` path already runs guest code on a raw OS thread and is the working
kernel of the fix. Understanding it precisely is the foundation of V2.

### OS thread creation
`NativeGuestExecutor.Initialize` emits a ~512-byte machine-code loop into a
`VirtualAlloc(EXECUTE_READWRITE)` page and starts it with **kernel32 `CreateThread`**
(4 MiB reserved stack), NOT `System.Threading.Thread`. The loop is:

```
loop: WaitForSingleObject(work, INFINITE)      ; kernel event, emitted call
      if (*stopFlag) { SetEvent(done); ExitThread(0) }
      rax = RunPrologue(selfHandle)             ; [UnmanagedCallersOnly] managed
      if (rax != 0) eax = rax()                 ; call the guest entry stub
      RunEpilogue(selfHandle, eax)              ; [UnmanagedCallersOnly] managed
      SetEvent(done); goto loop
```

### Does the CLR know about it?
Only as a **foreign thread**. It is never a `Thread` object created by the runtime.
The CLR first sees it when the emitted loop calls `RunPrologue` — a reverse-P/Invoke
that lazily attaches the OS thread to the runtime for the duration of that managed
call and leaves it **preemptive** in between. Because the loop body between managed
calls is emitted native code, **there is never a managed frame below the guest code**.

### How imports enter managed HLE safely
`RunPrologue` (managed) returns the guest entry-stub pointer; the emitted loop then
`call rax` into the guest stub directly. Guest code runs with the thread in
**preemptive** GC mode (the return from the `RunPrologue` UnmanagedCallersOnly method
transitioned managed→native). When the guest hits an import it calls an emitted import
trampoline → the marshaled-delegate reverse-P/Invoke gateway `ImportDispatchGatewayManaged`
(`Marshal.GetFunctionPointerForDelegate`), which transitions preemptive→cooperative,
runs the managed HLE, and returns to preemptive. The GC walk of the cooperative gateway
frame stops at the reverse-P/Invoke boundary — **the emitted loop below is native, so
the GC never descends into guest frames.** This is exactly the property the inline
`CallNativeEntry` path lacks.

### How guest state is parked while HLE runs / blocks
Two block shapes coexist:
- **Cooperative yield (resumable continuation).** An HLE that must block sets
  `ActiveGuestThreadYieldRequested` and the guest stub returns to the loop with
  `GuestNativeCallExitReason.Blocked`, carrying a `GuestCpuContinuation` (RIP, RSP,
  return-slot, nonvolatiles). The worker is released; the scheduler later resumes the
  continuation via `ExecuteBlockedGuestThreadContinuation` → `ExecuteGuestContinuationEntry`.
  This machinery is proven correct (register transfer + TLS/RSP).
- **Host park (thread block).** For threads that cannot yield cooperatively (external
  threads, and the GC-suspend 0x1E delivery target), `EnterInterruptibleHostPark`
  blocks the thread in a `Monitor.Wait`; a queued guest exception interrupts the park
  and is delivered **on that same host thread** (`TryDeliverQueuedGuestException`).

### How callbacks resume guest
Host→guest re-entry (`TryCallGuestFunction`) builds a fresh `CpuContext` on a callback
stack and calls `ExecuteGuestThreadEntry` again. Nested callbacks stack another guest
run. On the inline path this is where the frame interleaving gets deep; on a worker the
nested run is just another emitted-loop invocation.

### Why tbb bursts previously `__fastfail`ed under concurrency — and why the limiter exists
From commit `96fde57` ("soft-fail TBB native worker storms and cap concurrent Runs"):
*"Throwing on worker/prologue faults killed the process mid tbb_thead burst (FailFast
0xC0000409). Soft-return 0x80020012, limit in-flight native Runs (default 2), and keep
prewarm small."* The failure was **not** that GC-safe native workers cannot be
concurrent — it was that a worker-create/prologue fault during a large tbb spawn burst
was **thrown**, and an uncaught throw mid-burst FailFast-killed the process; a
create-storm also stressed the runtime. The fixes were: (1) `RunPrologue`/`RunEpilogue`
now **catch and soft-return** (never throw across the boundary), and (2)
`_nativeWorkerRunLimiter` (a `SemaphoreSlim(2)`) caps concurrent `Run`s so a burst
cannot storm-create workers.

So the "~2" is a **throttle against creation/fault storms**, not a proven ceiling on
GC-safe concurrency. It must not be raised blindly (the create-storm and fault-storm
are real), but it is not a correctness bound. The prototype's job is to prove GC-safe
concurrency well above 2 with a robust (pre-created, reused) pool.

### Persistent vs recreated / ownership
Workers are **persistent and pooled** (`_idleNativeWorkers` stack, `_allNativeWorkers`
list); `RentNativeGuestExecutor` reuses an idle worker or creates one on demand,
`ReturnNativeGuestExecutor` pushes it back. On a TBB worker-abort the OS thread is
`TerminateThread`'d and the loop recreated. Ownership is **transient**: a worker carries
no guest identity of its own — `RunPrologue`/`EnterRun` rebind guest TLS, host-RSP slot,
the `Active*` thread-statics and the `GuestThreadExecution` ambient on every run, so any
worker can run any guest. A guest pthread is bound to a worker only for the duration of
one `Run` (one un-blocked slice).

---

## 2. Design options

### A. One persistent native OS thread per active guest pthread
- **Correctness / GC:** clean — guest frames only ever sit above the emitted loop.
- **Blocking HLE:** the worker thread blocks (`Monitor.Wait`) on its own thread; no
  continuation capture needed for the steady state.
- **Continuation migration:** none in steady state (guest wakes on its own thread).
- **Exception delivery:** naturally **on-thread** (0x1E delivered to the guest's own
  worker) — matches the host-park requirement with no special case.
- **Scalability:** one OS thread per guest thread. Unity ≈ 30–45 threads → fine, but a
  tbb spawn burst of hundreds of short-lived threads → thread-creation churn.
- **tbb burst:** worst case here — needs a worker per concurrent tbb thread.
- **Complexity:** LOW–MODERATE; closest to today's tbb path.

### B. Bounded native-worker pool + resumable guest fibers/contexts
- **Correctness / GC:** clean.
- **Blocking HLE:** guest **yields** (continuation), worker returns to the pool; a small
  pool services many guest threads.
- **Continuation migration:** REQUIRED — a guest resumes on a different worker. The
  machinery already exists and is proven (`GuestCpuContinuation` transfer correct).
- **Exception delivery:** trickier — a yielded/parked guest has no fixed thread; 0x1E
  must be delivered via the continuation on resume, and the on-thread host-park case
  (which today runs the handler on the parking thread) must be preserved for the
  stop-the-world ack.
- **Scalability:** EXCELLENT — pool ≈ CPU count services all guest threads. Best for
  Unity job systems and tbb bursts (no thread explosion).
- **tbb burst:** EXCELLENT (bounded pool, guests yield).
- **Complexity:** HIGH — universal yield-on-block + migration + exception-delivery
  interplay.

### C. Native dispatcher threads with explicit guest contexts
- A few dispatcher OS threads each run an emitted scheduler loop that pops a ready guest
  context and resumes it until it yields.
- **GC:** clean. **Scalability:** excellent. **Exception delivery / complexity:**
  highest — the scheduler itself is emitted native code; hardest to debug.

### D. Hybrid (chosen) — native-worker-while-running + cooperative yield-on-block, scalable pool
- Guest execution is **always** on a native worker (never inline on a CLR thread).
- While a guest thread **runs**, it owns a worker. When it **blocks in HLE it yields**
  (existing continuation machinery), releasing the worker to the pool. So the pool size
  tracks **concurrently-running** guest threads (bounded by CPU count), not total guest
  threads — Unity's many-but-mostly-blocked threads need only a small pool.
- **Host-park / 0x1E stop-the-world:** keep the existing **on-thread** delivery. A
  thread that is host-parked keeps its worker parked and the queued exception handler
  runs on that same worker (do NOT migrate it — that broke the handshake).
- The `_nativeWorkerRunLimiter` is replaced by a **pool that grows on demand and is
  bounded by a high water mark tied to concurrency**, keeping the soft-fail-never-throw
  invariant from `96fde57`.

**Chosen: D.** It reuses today's proven pieces (the emitted-loop worker, the resumable
continuation, on-thread host-park delivery), scales like B for Unity/tbb without a
thread-per-guest explosion, and keeps exception delivery on-thread where the semantics
require it. A is the fallback if universal yield-on-block proves too invasive; C is
rejected as over-engineered.

---

## 3. Critical bridge design: guest/native thread → managed HLE → native guest resume

The invariant: **no arbitrary guest frame is ever part of a CLR-managed thread's stack,
and every managed HLE call is entered from native code via a legal reverse-P/Invoke.**

```
[worker OS thread]  emitted loop (native)                    preemptive
   └─ call guest entry stub (native, guest frames)           preemptive   <-- GC ignores
        └─ guest hits import: call import trampoline (native) preemptive
             └─ reverse-P/Invoke gateway (managed HLE)        COOPERATIVE  <-- GC-visible
                  · GC walk STOPS at this reverse-P/Invoke boundary;
                    the native/guest frames below are the "unmanaged" side
                  └─ if blocking: set yield + capture continuation, return
             └─ gateway returns                               preemptive
        └─ guest stub returns eax to the loop                 preemptive
   └─ RunEpilogue (managed) captures outcome                  COOPERATIVE (brief)
```

**Why this is legal where inline is not.** The reverse-P/Invoke gateway is only ever
entered from native code (the import trampoline). The CLR's transition frame marks the
boundary; a GC stack-walk of the cooperative gateway frame terminates at that frame and
never traverses the guest frames beneath it (they are unmanaged). On the inline
`CallNativeEntry` path the guest frames sit **above** live managed frames (`ExecuteEntry`,
`RunGuestThread`, `ThreadMain`) on a GC-managed thread, so the walk/hijack must cross
them — that is the corruption.

**GC transition control.** We rely only on the standard, documented transitions:
- managed→native at each `delegate* unmanaged` / marshaled-delegate call boundary
  (thread goes preemptive; GC ignores the thread while guest code runs),
- native→managed at each `[UnmanagedCallersOnly]` / marshaled reverse-P/Invoke entry
  (thread goes cooperative; a proper transition frame bounds the GC walk).
No `SuppressGCTransition` on the HLE gateway (it touches managed heap). No attempt to
disable GC, hijacking, or suspension. The managed **orchestrator** thread that kicks a
`Run` parks in a preemptive `WaitOne` for the duration, so its own managed stack is
never walked across guest frames either.

**Legality of the UnmanagedCallersOnly entry.** `RunPrologue`/`RunEpilogue` and the
import gateway are all entered from *native* code (emitted loop / trampoline). The
failure mode we are eliminating is precisely the *opposite*: a reverse-P/Invoke entered
while the thread is already cooperative because its GC-mode was corrupted by a walk
across guest frames. Removing guest frames from managed stacks removes the corruption
source.

---

## 4. Prototype strategy (smallest experiment first)

Do NOT migrate titles. Build one isolated, self-contained harness that reproduces the
CLR/GC boundary of the native-worker model and stresses it far past concurrency 2 under
forced GC. If the harness passes, the model is validated independently of the emulator.

**Harness (`prototypes/NativeGuestV2/`), pure synthetic — no guest images, no titles:**
1. Emit, per worker, the same shape as production: a native loop that calls a managed
   `[UnmanagedCallersOnly]` prologue, then `call rax` into an emitted **synthetic guest
   stub** that contains arbitrary native frames and repeatedly calls a managed
   `[UnmanagedCallersOnly]` **HLE step** (the reverse-P/Invoke boundary).
2. The HLE step (managed): allocate short-lived objects (GC pressure); on a schedule,
   (a) request a **block** → the synthetic guest yields to the loop, the harness parks
   the worker on a kernel event and later resumes it (**block → resume**); (b) request a
   **nested callback** → invoke a second emitted guest stub from inside the HLE step
   (**host→guest→host nesting**).
3. Spawn **many** such workers concurrently (sweep N = 4, 8, 16, 32, 64) — far above 2.
4. A separate thread calls `GC.Collect(2, Forced, blocking)` in a tight loop for the
   duration (also try `GCSettings` server/concurrent variants).
5. Run a fixed large iteration budget across all workers.

**Success = no `__fastfail`, no UnmanagedCallersOnly violation, correct block/resume and
nested-callback counts, at N ≫ 2 under continuous forced GC.** Failure at some N tells us
the real concurrency ceiling and whether it is creation-storm (throttle) or GC-boundary
(architecture).

---

## 5. Success criteria (gates before any backend replacement)

1. synthetic native-worker stress passes;
2. concurrency well above 2 works (target ≥ 16, ideally 32–64);
3. no UCO FailFast under continuous forced GC;
4. block / resume works and is counted correct;
5. guest-exception delivery semantics preserved (on-thread host-park handshake);
6. nested callbacks work.

Then, only if the prototype is mature: Cocoon repeated gameplay with no UCO FailFast,
then cross-title (Sarah, Solitaire, Hotline Miami, Animal Well, Unpacking, ProsperoGame,
DoomGeneric).

---

## 5a. Prototype results (`prototypes/NativeGuestV2/`, Windows x64)

The harness emits, per worker, a native loop on a raw kernel32 `CreateThread` thread
that calls an emitted synthetic guest body containing arbitrary native frames, which
reverse-P/Invokes managed `[UnmanagedCallersOnly]` HLE (allocating for GC pressure,
requesting BLOCK / NEST / FINISH). A pool of workers is rented per guest slice
(block → yield → resume on the next rented worker → worker reuse/migration); a
background thread hammers `GC.Collect(2, Forced, blocking)`.

Native-worker mode — **all PASS**, zero UnmanagedCallersOnly `__fastfail`, zero HLE
exceptions, all work completed exactly, block/resume + nested callbacks correct:

| config | peak workers | HLE calls | forced GCs | nested | result |
|---|---|---|---|---|---|
| 16 guests / pool 16 | 16 | 8.0M | 7,263 | 215,680 | PASS |
| 32 guests / pool 32 | 32 | 16.0M | 3,287 | 431,360 | PASS |
| 64 guests / pool 48 | 53 | 19.2M | 917 | 516,864 | PASS |
| 48 guests, **Server GC** | 48 | 19.2M | 1,978 | 1,126,608 | PASS |

- **Concurrency ≫ 2 works** (16 → 53 concurrent workers), disproving any GC-safety
  ceiling at 2 — the `_nativeWorkerRunLimiter` is a create/fault-storm throttle only.
- **No `__fastfail` under continuous forced GC**, including **Server GC** (more
  aggressive suspension) — the model's central claim holds.
- **block/resume + worker reuse across guests + nested host→guest→host callbacks** all
  exercised and verified at scale.

Inline control (`--inline=1`, the emitted guest body run directly on managed driver
threads via `delegate* unmanaged` calli — the production `CallNativeEntry` shape): ran
to completion cleanly under 1,000+ forced GCs and did **not** reproduce the
`__fastfail`. The synthetic guest body's frames are too shallow/uniform for the .NET GC
hijack-across-guest-frames pathology; the real crash needs genuine arbitrary guest
x86-64 code (deep, varied Unity/il2cpp/FMOD call chains) and is reproduced by Cocoon
itself (see `cocoon-gameplay-uco-crash`). This does not weaken the positive result: the
native-worker model passes the identical stress the inline path is theorised to fail,
and structurally removes guest frames from managed stacks so the pathology cannot arise.

Run: `dotnet build prototypes/NativeGuestV2 -c Release`, then
`dotnet <artifacts>/NativeGuestV2.dll --guests=32 --pool=32 --steps=500000 --gc=1`
(add `--inline=1` for the control; set `DOTNET_gcServer=1` for Server GC). Launch the
DLL with a Windows-local working directory — a `\\wsl.localhost` UNC cwd breaks apphost
launch.

## 6. Migration plan (staged, post-proof)

1. **Prototype proof** (this doc's harness) — no emulator changes.
2. **Pool hardening** — replace `_nativeWorkerRunLimiter` with a grow-on-demand pool
   bounded by a concurrency high-water mark, keep never-throw/soft-fail; validate tbb
   (Astro) still boots.
3. **Route ordinary guest pthreads** (`RunGuestThread`) to the worker path behind a flag
   (`SHARPEMU_NATIVE_GUEST_V2=1`); keep inline as fallback. Validate Cocoon + cross-title.
4. **Route the main entry** (`ExecuteEntry`) to the worker path. Validate.
5. **Flip the default**, keep the inline path as an env-gated escape hatch for one
   release.
Each stage is a separate small commit with its own validation; no stage lands without
the prior stage's cross-title pass.

---

## 7. Risks

- **tbb spawn bursts** still stress worker creation; mitigate with a pre-grown pool and
  the existing soft-fail. Must re-validate Astro at each stage.
- **Yield-on-block coverage**: any blocking HLE that today relies on the thread actually
  blocking (host-park) rather than yielding must keep on-thread delivery; a missed case
  would regress the 0x1E handshake (observed when we moved delivery off-thread).
- **Worker-count growth** for pathological all-running workloads; bound with a high-water
  mark and prefer yield-on-block over thread-per-guest.
- **Windows VEH interaction** (guest AV → VEH → managed pre-filter) must keep working on
  worker threads; the pre-filter is already native-first.
- **Stop condition**: if .NET reverse-P/Invoke semantics make a pure managed-side model
  impossible (e.g., the runtime still hijacks foreign threads across the transition under
  some GC mode), a small native C/C++ shim owning the scheduler loop + transition frames
  is architecturally acceptable — but only introduced if the managed-emitted harness
  fails.
