# Native Guest Execution V2 — Stage 5: cooperative primary guest thread

Status: design + audit (branch `gpt-dlsym`, not pushed). Follows Stage 4's finding
(docs `67ffa17`): the remaining Cocoon UnmanagedCallersOnly crash is the **0x1E
GC-suspend host-park nested delivery** on the primary/main guest, not the top-level
inline execution — so routing the top level to a worker (Stage 4) regressed 10/10.

This document is the consolidated result of a five-way parallel read-only audit
(primary lifecycle+shutdown, GuestThreadState+blocking, thread identity, 0x1E
delivery, and the 28cab08 host-park history) and the resulting design.

## 1. Historical primary execution model

`SharpEmuRuntime.Run → CpuDispatcher.DispatchEntryCore` (builds the one primary
`CpuContext`, `ProcessEntry` frame) `→ DirectExecutionBackend.TryExecute → ExecuteEntry`.
`ExecuteEntry` emits an entry stub and runs guest main **inline via `CallNativeEntry`
on the CLR main thread**; `int num6` is the guest's raw return value. There is **no
Blocked/yield path** — `num6` is treated unconditionally as "process completed", after
which `PumpUntilGuestThreadsIdle` drains the *other* guest threads. Background threads
`SharpEmu-ReadyDispatch` / `SharpEmu-GuestThreadDispatcher` / `SharpEmu-StallWatchdog`
drive ordinary threads and hard-`Environment.Exit(4)` on a 20 s import stall.

The primary is **not** a scheduler thread: `TryExecute` never calls `EnterGuestThread`,
so it runs with `CurrentGuestThreadHandle==0` / `IsGuestThread==false`. Its only guest
identity is an `ExternalGuestThreadState` registered lazily by `scePthreadSelf →
RegisterGuestThreadContext` (into `_externalGuestThreads`, targeted via
`_currentExternalGuestThreadHandle`), which exists solely so 0x1E can find it.

## 2. 28cab08 / host-park history — and how Stage 5 avoids reintroducing it

`28cab08` fixed the Cocoon startup GC-suspend **deadlock**: the Boehm collector raised
0x1E on the primary, but the primary was an *external* thread host-blocked in
`Monitor.Wait` (`pthread_cond_wait`) with **no resumable continuation**, so the
queue-and-defer-to-next-HLE-boundary delivery never ran → handler never acked → both
sides deadlocked. The fix added the **interruptible host-park**: the parked thread
registers its Monitor, the raiser pulses it, and the woken loop runs the handler **on
its own host thread** via `ServiceHostParkInterrupt → TryDeliverQueuedGuestException →
TryCallGuestFunction` (nested, inline).

Root reason the primary couldn't yield cooperatively then = (b)+(c): it was *external*
(self-executing, `_currentGuestThreadHandle==0`) **and** its block point was deep inside
`Monitor.Wait` where no continuation could be captured. Stage 5 removes both: the
primary gets a real handle (`IsGuestThread==true`), so its kernel waits go through
`RequestCurrentThreadBlock` and capture a continuation **at the import boundary** (the
same place ordinary pthreads capture), never bottoming out in `Monitor.Wait`. The wake
path (`WakeBlockedThreads`) and the exactly-once queue are reused unchanged.

## 3. Why this crash is a host-park problem, and what fixes it

- **Blocked-thread 0x1E is already GC-safe** for ordinary cooperative threads:
  `TryRaiseGuestException`'s parked-cooperative branch embeds the continuation into the
  ucontext and runs the handler through `DeliverException` **on the thread's own
  `GuestExecutionRunner`** (a dedicated thread, clean stack) — *not* nested above
  `Monitor.Wait`. This is exactly the path the primary should use.
- **Host-park 0x1E is the crash**: `TryDeliverQueuedGuestException → TryCallGuestFunction`
  runs the handler with `Monitor.Wait → ServiceHostParkInterrupt → TryDeliverQueued… →
  TryCallGuestFunction` managed frames directly beneath it; under concurrent .NET GC that
  guest-above-managed shape trips the reverse-P/Invoke `__fastfail`.
- **Running-thread 0x1E** (`DeliverPendingGuestExceptionAtSafePoint`) delivers inline on
  the *current* thread at an import boundary. For ordinary pthreads that thread is a
  native worker (GC-safe); for an **inline** primary it is the CLR main thread (not
  GC-safe). So making the primary cooperative fixes the **dominant blocked/host-park
  crash**, but the running-thread safe-point case is only fully fixed once the primary
  also runs on a worker (Stage 5B).

## 4. Primary identity plan (and the risk sites)

Mint one stable primary handle (an `AllocHGlobal`/scheduler handle, collision-free) and
`EnterGuestThread(primaryHandle)` before guest main runs; register a real
`GuestThreadState` in `_guestThreads` bound to the main `HostThread`/`HostThreadId`.
`scePthreadSelf` then returns that handle stably across block/resume (it lives in the
`[ThreadStatic]` and the primary always resumes on the same host thread in 5A).

**Behavior-changing sites to re-verify (were `IsGuestThread==false`/handle-0 special-cases
for the primary):**
- `DirectExecutionBackend.Imports.cs:~384` — the import-loop force-exit guard fires only
  when `!isGuestWorker`, i.e. **only on the primary today**. Making the primary a guest
  thread disables that watchdog for it. Highest-risk; keep an equivalent guard for the
  primary or prove it is unneeded.
- `FiberExports.cs:~812` — `GetThreadKey` special-cases the main thread (`handle==0 →
  FsBase`). Post-change it returns the real handle; verify fiber-key consistency.
- `GuestThreadExecution.cs:514` (`!IsGuestThread` cooperative-block gate) and the
  pthread/rwlock `IsGuestThread &&` cooperative gates — these now let the primary block
  cooperatively (the intended change), must be validated end-to-end.

## 5. ExecuteEntry block/resume design (Stage 5A — top-level stays inline)

`ExecuteEntry` becomes the primary's driver on the CLR main thread:
```
register primary GuestThreadState (IsExternalExecutor=true so the ready-dispatcher skips it)
EnterGuestThread(primaryHandle)
exit = run guest main inline (CallNativeEntry)              // fresh entry
loop while exit == Blocked and not forced-exit:
    // continuation already captured onto the primary by the import block path
    State = Blocked; ExecutorActive = false
    wait on primaryReadyEvent                                // preemptive; other threads pumped by background dispatchers
    State = Running; ExecutorActive = true
    exit = resume primary continuation inline (CallNativeEntry)
propagate guest return (num6) exactly as today; PumpUntilGuestThreadsIdle
```
- **Blocked vs Completed:** after `CallNativeEntry`, `ActiveGuestThreadYieldRequested`
  distinguishes a cooperative yield (loop) from a true return (`num6`, exit). This is the
  generic extension of `ExecuteEntry`'s return contract the milestone asked for — a
  yielded primary is **not** mistaken for process exit.
- **Wake:** `WakeBlockedThreads` (and 0x1E readying) set the primary `Ready`; for an
  `IsExternalExecutor` primary they additionally `Set(primaryReadyEvent)` and do **not**
  enqueue it for the ready-dispatcher (ExecuteEntry owns resume).
- **0x1E while blocked:** the parked-cooperative branch runs the handler on the primary's
  `GuestExecutionRunner` (GC-safe) while `ExecuteEntry` waits; the primary's original
  block state is restored afterward, and its real wait later readies it → `ExecuteEntry`
  resumes. No host-park, no nested-above-`Monitor.Wait` delivery.

## 6. Stage 5B (conditional, after 5A proven)

Route the now-cooperative primary top-level through the same worker path ordinary
pthreads use, so its running-thread safe-point 0x1E is also GC-safe. Only meaningful
once 5A proves the cooperative primary model.

## 7. Parallel audit — integration record

Five read-only sub-agents produced the maps above; **none edited the tree** (read-only).
Their findings were reviewed and synthesized here by the primary agent; nothing was
integrated as code from a sub-agent — they are evidence only. See the milestone report's
"Parallel work" section.

## 8. Open risk / stop criteria

Stop and report (do not force 5B) if: `ExecuteEntry` cannot distinguish blocked vs
returned cleanly; the primary's cooperative continuation cannot integrate with the ready
queue; the Boehm handshake deadlocks; the import-loop guard loss hangs the primary;
process shutdown becomes ambiguous; or the 28cab08 deadlock returns.

## 9. Stage 5A implementation attempt — result (REVERTED)

An implementation was built and tested behind `SHARPEMU_NATIVE_GUEST_V2_PRIMARY=1`
(requires V2): the process entry (gated to `frameKind==ProcessEntry`, since module
initializers also flow through `ExecuteEntry`) is registered as a real cooperative
`GuestThreadState`; `ExecuteEntry` schedules it and waits on an exit event while the
existing ready-dispatcher drives it through `RunGuestThread` — reusing the whole
ordinary-pthread path (cooperative block/yield/resume, cooperative 0x1E on the
thread's `ExecutionRunner`). This unified 5A with 5B (the scheduled primary runs on a
native worker under V2), because the audit showed the cooperative delivery/resume path
is inherently runner-based and cannot be cleanly kept "inline."

**Validated (core hypothesis):** the host-park nested 0x1E delivery is GONE — Cocoon runs
show `kernel exception 0x1E host park` = 0 and **zero UnmanagedCallersOnly `__fastfail`**.
Cooperative block/resume works (guest-thread log: one fresh entry, 3,632 cooperative
resumes, no crash). So making the primary a cooperative scheduled thread does eliminate
the crash class this whole effort targeted.

**Regression (stop condition hit):** Cocoon stalls at 0 fps and never reaches gameplay.
The primary repeatedly resumes to the SAME guest RIP (`0x800D2622A` — a Unity
frame-sync / job-fence hot wait), ~15× in a row: it is woken, re-checks the wait, and
re-blocks without progress. That block/resume **churn** — each cycle paying pooled-worker
rent + cross-thread signalling + continuation capture/restore — crawls the render loop
(`draw_ms` ~4865 ms, only a handful of videoout samples in 200 s). The main thread's
execution pattern is high-frequency short poll-waits, for which the per-block cooperative
overhead is pathological (ordinary pthreads block on real, infrequent waits, so they do
not exhibit this). Reverted (no production code retained; design kept).

**Conclusion / recommended next step.** The crash is definitively the host-park nested
delivery, and cooperative blocking removes it — but the primary cannot pay per-block
pooled-worker overhead on its hot frame-sync waits. The correct model is a **cooperative
primary pinned to its own dedicated persistent native worker** (owned for the whole
session, never rented per block), so block/resume is a same-thread continuation with no
pool rent/handoff, while staying GC-safe on a native base. Alternatively, first determine
whether the `0x800D2622A` wake is spurious (a wake-key over-match exposed by the primary)
— if so, cooperative blocking without the worker churn may suffice. Either is a focused
follow-up; neither should be rushed into this milestone.
