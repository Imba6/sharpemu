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
