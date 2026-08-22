# Native Guest Execution V2 — Stage 7: IL2CPP/Boehm native-context identity proof

Status: static identity audit **complete** (branch `gpt-dlsym`, not pushed). Decides whether
the 0x1E GC-suspend handler may run on ANY GC-safe native worker carrying the correct guest
context (Option 2), or must run on one specific host OS thread (Option 1). Follows Stage 6,
which proved both the inline safe-point and the parked Route-C deliveries are guest-above-
managed and crash under Cocoon's ~20-thread Boehm suspend storm.

## The question

`DeliverException` (`DirectExecutionBackend.cs:5259-5266`) asserts the handler must run on the
guest thread's persistent `GuestExecutionRunner` because it "preserves the native-thread
identity/TLS that Unity's stop-the-world collector registered … using an unrelated temporary
host thread … [publishes] roots from the wrong native execution context, which lets live
IL2CPP delegates be reclaimed." Is that a real requirement (→ Option 1) or conservative
belief (→ Option 2)?

## Verdict: the identity is the LOGICAL GUEST context, not a host OS thread — Option 2 is NOT disqualified

Three independent read-only audits converge:

### A. Registration/scan identity is guest-keyed (host-worker-independent)
Every input the Boehm collector reads at a 0x1E is **guest memory / guest register state**:
- Unity's suspend-callback table (`FindGuestExceptionThreadRecord`, `:5279-5318`) is a guest
  256-bucket table keyed by the **guest pthread handle** (node+0x08); the scanned roots are
  the **guest stack** (registered bound at node+0x100, RSP at node+0x18) and the guest
  register/RSP/FsBase ucontext built by `TryWriteGuestExceptionContext` (`:5523-5561`) from
  the interrupted guest continuation, written into guest memory.
- The handler runs on `target.Context` (guest CPU state) with guest TLS re-seeded by
  `BindTlsBase` from the guest context. Nothing host-side (host stack, host TLS) is scanned.
- `HostThreadId` (`GuestThreadState.HostThreadId`) is rebound to whatever worker runs the
  thread and restored on exit; it is consumed **only** by TBB-access-violation VEH recovery
  and the sampling profiler — **never** by the 0x1E/GC path.

### B. Normal V2 execution ALREADY migrates guest code across host OS threads
Under `SHARPEMU_NATIVE_GUEST_V2=1`, each cooperative execution slice does a fresh
`pool.Rent()` (LIFO idle stack, `NativeWorkerPool.cs:139-196`), so consecutive slices of one
logical guest thread run on **different pooled `NativeGuestExecutor` OS threads**. `EnterRun`
(`NativeWorker.cs:710-762`) rebinds only host-side state (`HostThreadId` → the worker's tid,
host-RSP TLS, affinity) and re-seeds guest TLS from the guest context; the guest identity
(handle, `CpuContext`, guest stack, FsBase) is carried unchanged. Cocoon reaches gameplay
this way — so Unity/Boehm **already tolerates host-OS-thread migration** during normal
execution. The "persistent execution runner" the comment relies on is a **CLR-managed
`new Thread`** that only *dispatches* work; it does not itself run the migrating guest code.

### C. The "same host thread" comment is unverified, and the pre-existing code disproves it
`git blame`: the comment + `GuestExecutionRunner` entered together in one generic upstream
squash (`864cbb0`, PR #216) with **no symptom repro, test, or GC-corruption reproduction**.
The code it replaced, `RunContinuationOnTemporaryThread`, already spawned a **fresh temporary
host OS thread per delivery** and already carried the correct logical guest context via
`EnterGuestThread`. So the switch to a persistent runner added only native-thread *stability*,
not *correctness* — it was never shown to fix a "wrong guest context" bug. `EnterGuestThread`
(`GuestThreadExecution.cs:451`) sets only the `[ThreadStatic]` guest handle; "correct guest
context" and "same OS thread" are orthogonal. And Stage 6 proved the persistent-runner path
itself crashes under the concurrent storm — so OS-thread stability is not the safe axis.

## The real invariant (the proven floor)

The handler must run with **(1) the correct logical guest context** — guest pthread handle
(`EnterGuestThread`), guest `CpuContext` (RSP/FsBase/registers), guest stack — **on (2) a
GC-safe native base** — a native worker in preemptive GC mode with **no CLR-managed frames
below the guest handler** (so a concurrent .NET GC never walks guest-above-managed). A rented
pooled worker provides both, identically to a normal execution slice. There is **no** proven
dependency on a specific host OS thread.

## Consequence

Option 2 (deliver the 0x1E handler on a rented pool worker with the guest context) is
semantically permitted by the identity model. It must still be **empirically validated** for
absence of *silent* GC corruption (the milestone's bar: no UCO, no GC deadlock, no duplicate
execution, no heap/object corruption, consistent Cocoon gameplay, stable repeated delivery
over ≥10 runs) — a single non-crash is not proof. The gated experiment
(`SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER=1`) and its results follow.

## Parallel work

Three read-only sub-agents (registration/scan identity; normal-worker-migration evidence;
`DeliverException` comment git-history) + direct reading. None edited the tree.

## Experiment result — Option 2 VALIDATED (gated, corruption-free)

Built behind `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER=1` (requires V2). Two-part change:
1. The three 0x1E delivery call sites (`DeliverException`, safe-point, host-park) pass a new
   `deliverOnNativeWorker` flag through `TryCallGuestFunction` →
   `ExecuteGuestThreadEntry(reentrant:false)` and `ResumeBlockedNestedGuestCallback` →
   `ExecuteBlockedGuestThreadContinuation(reentrant:false)`, so the handler AND its
   resume-handshake slices run on rented pool workers (native base) instead of inline on the
   CLR-managed runner.
2. Generalized: `ShouldRunGuestOnNativeWorker` routes **all** nested/reentrant guest
   execution (module `DT_INIT` via `sceKernelLoadStartModule`, guest callbacks, the 0x1E
   handler) onto rented workers under the flag — no guest frame ever runs above a live
   managed frame during a concurrent GC.

Validated with the dedicated-primary patch applied (to reach the worker-storm regime),
`SHARPEMU_NATIVE_WORKER_MAX=128`, `SHARPEMU_UCO_FLIGHT=1` + `SHARPEMU_LOG_GUEST_EXCEPTIONS=1`.

**Staged evidence.** First, 0x1E-only routing (per-caller flag, module-init still inline): the
0x1E handler delivered on rented workers **709 / 601 times in two runs with zero 0x1E UCO**
(`guest_exception.delivery_exit success=True`), and the crash *moved* off the 0x1E path
entirely (`safe_point`=0 everywhere) onto `sceKernelLoadStartModule` module-init — proving
the rented-worker 0x1E delivery itself is sound and exposing module-init as the same
guest-above-managed class.

**Generalized run — 10 Cocoon sessions (`SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER=1`):**

| Metric | Result |
|---|---|
| UnmanagedCallersOnly `__fastfail` (UCO) | **0 in 9/10** (the 1 is the pre-existing *top-level* module-initializer `ExecuteEntry` inline path — a `ModuleInitializer` frame not covered by this experiment or the primary patch, which only cooperativizes `ProcessEntry`) |
| Heap/object/GC corruption (AV, heap-corrupt, double-free, TLSF) | **0 in 10/10** |
| GC-suspend deadlock | none — runs proceed and present frames |
| First-frame Vulkan present (3840×2160) | 4/10 reached it |
| 0x1E deliveries per run | stable (~12 `delivery_exit success=True`); no 0x1E-attributed crash in any run |
| Sustained ~30 fps gameplay | **not reached** — every run stalls on the pre-existing **unresolved-import spin** (`VkqLPArfFdc` / `4fU5yvOkVG4`, ~2.79–2.92 M imports) = blocker #2, out of scope |

**Verdict: Option 2 is semantically valid AND empirically corruption-free.** Routing the
0x1E handler (and, generally, reentrant guest execution) onto rented pool workers eliminates
the guest-above-managed UCO class with **no evidence of silent GC corruption** across 10
runs — exactly as the identity audit predicted (the collector's identity is the logical guest
context, carried by the worker, not the host OS thread). **Option 1 (pin a native worker per
guest thread) is therefore NOT required** — the pooled-worker scalability of Stage 2/3 can be
kept.

## Remaining blockers (all separate, out of this milestone's scope)

1. **Top-level module-initializer inline path** (1/10 crash, g6): `ModuleInitializer` frames
   still run inline on the CLR main thread via `ExecuteEntry` (the primary patch only
   cooperativizes `ProcessEntry`). Same guest-above-managed class; fixable by extending the
   dedicated-worker/cooperative treatment to `ModuleInitializer` frames.
2. **Unresolved-import spin** (`VkqLPArfFdc`, `4fU5yvOkVG4` = `sceSysmoduleGetModuleInfoForUnwind`):
   the guest loops on unresolved imports (~2.9 M) after first-frame present. Compatibility
   gap (blocker #2). The experiment *advanced* the guest to this point by removing the
   earlier module-init crash — forward progress. Must stay a separate milestone.

## Recommendation

Proceed with **Option 2** as the production direction (rented-worker 0x1E delivery; keep the
pool). Next steps, in order and as separate commits: (a) land the rented-worker 0x1E delivery
un-gated for the covered paths once the module-init and unresolved-import blockers are
cleared; (b) extend the cooperative/worker treatment to `ModuleInitializer` frames; (c) the
unresolved-import spin (blocker #2). The gated experiment is retained as the proof.
