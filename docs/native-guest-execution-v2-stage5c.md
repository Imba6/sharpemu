# Native Guest Execution V2 — Stage 5C: primary cooperative wake-churn investigation

Status: investigation complete + **dedicated-worker implemented, validated, and reverted**
(branch `gpt-dlsym`, not pushed). See §10–§11 for the implementation/validation result and
the recovery patch. Follows Stage 5A
(docs `df6a655`): the cooperative primary **eliminates the UCO `__fastfail`** (host-park
nested 0x1E delivery gone) but **stalls Cocoon at ~0 fps**, churning block→wake→resume→
reblock at guest RIP `0x800D2622A`. This document decides the Phase-6 question the Stage
5A follow-up left open:

> **Is the churn a wake bug (CASE A) or legitimate high-frequency polling (CASE B)?**

**Verdict: CASE B — legitimate hot polling.** The wake logic is correct; every wake at
`0x800D2622A` is a genuine producer signal. The pathology is the per-block pooled-worker
rent/return + cross-thread handoff paid on a primary that acquires a Unity Baselib job
semaphore many times per frame. **Do not "fix" the wake logic.** The correct remedy is a
dedicated persistent primary native worker (design in §7).

## 1. What the wait at `0x800D2622A` actually is

`0x800D2622A` is the guest return address just after a `sceKernelWaitSema` call
(NID `Zxa0VhQVTsk`, libKernel) on a Unity **`Baselib_SystemSemaphore`** — a Baselib
worker/job semaphore. It is a **job fence**: the Unity main thread waits `need=1` on a
per-frame job-completion semaphore that a job worker signals. It is *not* a vblank/flip
wait (`sceVideoOutWaitVblank` never calls `RequestCurrentThreadBlock` — it does an inline
`HostTiming.SleepUntil`, `VideoOutExports.cs:588`), *not* a futex, *not* a condvar/mutex.

Evidence — already captured in `real-tests/Cocoon/PPSA08766-app0/run_fix.log` (the current
default/host-park run that reaches ~30 fps). Every one of the 28 `0x800D2622A` hits is a
semaphore block/wake pair, e.g.:

```
sema.wait-host-block handle=0x80 name='Baselib_SystemSemaphore' need=1 count=0 timeout=infinite guest=0x0        ret=0x800D2622A
sema.signal          handle=0x80 name='Baselib_SystemSemaphore' signal=1 count=1 waiters=1 guest=0x2C788EFC480 ret=0x800B8F0C3
sema.wait-host-wake  handle=0x80 name='Baselib_SystemSemaphore' need=1 count=0 guest=0x0        ret=0x800D2622A
```

The primary (`guest=0x0`) blocks on handles 0x80/0x81; a Unity job worker
(`guest=0x2C788EFC480`, signal call site `0x800B8F0C3`) signals them. `timeout=infinite`.

- Block: `KernelSemaphoreCompatExports.cs:186` — `RequestCurrentThreadBlock(ctx,
  "sceKernelWaitSema", GetSemaphoreWakeKey(handle), ResumeWait, WakePredicate, deadline)`.
  wakeKey = `sceKernelWaitSema:{handle:X8}` (per-handle); `deadline=0` (infinite).
- `WakePredicate` (`:142`) **atomically** checks `Count >= needCount` under
  `semaphore.Gate` and decrements — a real predicate, not a proxy.
- Waker: `sceKernelSignalSema` → `WakeBlockedThreads(GetSemaphoreWakeKey(handle))`
  (`:365`).
- Default (current) primary path: `guest=0x0` ⇒ not a guest thread ⇒
  `WaitSemaphoreOnHostThread` (`:227`), a same-thread `Monitor.Wait(gate, ≤100ms)` loop
  woken by `Monitor.PulseAll` in `SignalSema`. Cheap, on the primary's own OS thread.

## 2. Is every wake legitimate? (Phase 1/2)

Yes. In `run_fix.log` the primary's semaphore **signals and wakes are 1:1**: 14 signals to
0x80/0x81 → 14 host-wakes; 8 signals to 0x80 → 8 wakes on 0x80. Each `wait-host-wake` is
immediately preceded by a real `sema.signal … count=1 waiters=1` from the worker. **Zero
spurious wakes.** Every wake is condition-genuinely-satisfied (classification 1).

## 3. Wake-correctness audit — no CASE-A bug on this path (Phase 4/5)

A five-way read-only audit + direct code reading ruled out every generic wake bug for the
semaphore path:

- **Wake-key over-match / null predicate:** No HLE primitive registers `waiter=null`. The
  semaphore's `WakePredicate` re-checks `Count>=need` atomically, so
  `WakeBlockedThreads` (`DirectExecutionBackend.cs:4248`) readies the primary only when a
  signal genuinely satisfied it. (The one primitive whose `TryWake` is a *generation
  proxy* rather than the real condition is **sync-on-address / futex**,
  `KernelSyncOnAddressCompatExports.cs` — a real latent over-match/wake-all, but **not**
  the `0x800D2622A` primitive and **not** exercised here. Noted in §6 as separate.)
- **Timeout poll churn:** The wait is `timeout=infinite` ⇒ `deadline=0` ⇒
  `WakeExpiredBlockedGuestThreads` (`:4333`) never wakes it. No global min-deadline clamp
  exists. (Only the futex forces a 100 ms self-heal deadline on an infinite wait — again
  not this primitive, and far too coarse to explain a 15× burst.)
- **0x1E interaction:** A pending 0x1E on a parked cooperative thread runs the handler on
  the thread's own `GuestExecutionRunner` with the full block state saved and restored
  (`:5095`–`:5160`); the only Ready transition in that path is gated behind
  `BlockWaiter.TryWake()` (`:5167`), i.e. a lost-wake guard, not an unconditional wake.
  For the host-parked/primary path, `InterruptHostPark` only pulses the Monitor and never
  records progress on the sync object, so the wait re-checks its own predicate and
  re-parks. **A pending 0x1E does not ready a thread whose predicate is still false.**
- **Duplicate ready-enqueue / stale key:** `TryClaimReadyGuestThreadLocked` (`:7303`)
  uses `ExecutorActive` as an authoritative single-owner token and skips any dequeued
  entry whose `State != Ready`, so a double-enqueue is **idempotent** (no double-resume).
  Block state (continuation/wakeKey/waiter/deadline) is atomically consumed and nulled at
  the Ready→Running transition (`:4676`, `:6021`), so no stale key can rematch.

Conclusion: on the `sceKernelWaitSema` path there is **no spurious wake, no over-match, no
timeout poll, no 0x1E-induced wake, no duplicate resume**. The churn is real job-fence
traffic.

## 4. Host-park vs cooperative — why one is 30 fps and the other 0 fps (Phase 3)

Both paths see the *same* legitimate high-frequency handoff. The difference is cost per
acquire:

| | default / host-park (30 fps) | cooperative primary (0 fps, 5A) |
|---|---|---|
| block | `Monitor.Wait(gate)` on the **primary's own OS thread** | `RequestCurrentThreadBlock` → continuation capture |
| wake | `Monitor.PulseAll` same gate, same thread | `WakeBlockedThreads` → ready-queue enqueue → cross-thread dispatch |
| resume | returns inline on the same thread | `RunGuestEntryStub` → `pool.Rent()` → `worker.Run()` → `pool.Return()` (`DirectExecutionBackend.NativeWorker.cs:174,215,234`) |
| per-acquire cost | one Monitor pulse (ns) | SemaphoreSlim.Wait + gate lock + Stack pop/push + cross-thread signal + continuation capture/restore (µs), **every** acquire |

The main thread hits this semaphore **many times per frame**. Host-park pays a Monitor
pulse each time (negligible, same thread); the 5A cooperative primary pays a full pooled-
worker rent/return + cross-thread handoff each time → `draw_ms` ~4865 ms. So the answer to
Phase 3's critical question — *does host-park also wake thousands of times but cheaply on
the same OS thread?* — is **YES**. The wake frequency is legitimate; the **pooled-worker
migration overhead is the problem.**

Scale of the traffic (`run_fix.log`, one Cocoon session): **3325 cooperative
`sema.wait-block` / 3322 `sema.wait-wake` / 3775 `sema.signal`** (the Unity job workers,
which already run cooperatively and handle this cheaply because their waits are infrequent),
plus **140 `sema.wait-host-block`** on the primary (`guest=0x0`) — consistent with the
Stage-5A "3,632 cooperative resumes". The primary alone hot-waits across several Baselib
semaphore RIPs, not just one — e.g. `0x800C1BC92` ×31, `0x805D546B2` ×29, `0x805D48C9F`
×28, `0x800D18129` ×16, `0x800D2622A` ×14 in this short capture — all fed by one dominant
worker signal site (`0x800D3A9FB`, 3288 signals). This is a per-frame job-graph handshake,
not a single hot RIP; `0x800D2622A` is merely the diagnostic exemplar.

## 5. Verdict (Phase 6)

**CASE B — legitimate hot polling.** The cooperative primary must not pay per-block pooled-
worker overhead on its hot Baselib job-fence acquires. No wake-logic change is warranted or
safe. Proceed to the dedicated-worker design (§7).

## 6. Separate latent finding (NOT this bug, do not conflate)

`sceKernelSyncOnAddressWait/Wake` (`KernelSyncOnAddressCompatExports.cs`) parks on a wake
*generation* rather than the real futex compare value, and `SyncOnAddressWake` defaults to
wake-**all** (`int.MaxValue`) when the count arg looks unset (`:114`). That is a genuine
wake-key over-match / thundering-herd that *would* cause spurious resume-then-reblock — but
it is a different primitive, is not the `0x800D2622A` wait, and is not implicated in the
Cocoon stall. Filed here so it is not lost; fix it on its own merits, not as part of 5C.

## 7. Design — dedicated persistent primary native worker

Justified by CASE B. The primary needs cooperative `GuestThreadState` semantics (so 0x1E is
delivered GC-safely on a native runner, never host-park nested) **without** per-block pool
rent/handoff.

Requirements (from the milestone):
- Created once for the primary; owned for the whole session (never rented per block).
- Stays associated with the logical primary guest thread; resumes its continuations on the
  **same** worker (no cross-thread migration per acquire).
- Remains foreign/native to the CLR (GC-safe base for guest frames).
- Still uses cooperative `GuestThreadState` block/yield/resume; still avoids host-park
  nested `TryCallGuestFunction`; still allows 0x1E through normal guest execution.
- Does **not** consume a general pool run-slot permanently (must not shrink effective
  `NativeWorkerMax` for ordinary pthreads → allocate a dedicated worker *outside* the
  `_runSlots`-bounded pool, or grow `MaxWorkers` by one for it).
- Deterministic shutdown.

Sketch: give the primary `GuestThreadState` a pinned worker created via the same factory as
the pool but held in a dedicated field (not `_idle`/`_runSlots`). The ready-dispatcher, when
it claims the primary, routes its `RunGuestEntryStub` to the pinned worker directly instead
of `pool.Rent`. Block/resume then becomes: worker runs guest until `sceKernelWaitSema`
yields → continuation captured → worker parks on a per-primary event → `WakeBlockedThreads`
sets that event → the **same** worker resumes the continuation. No Rent/Return, no
cross-thread hop, no Stack/SemaphoreSlim traffic per acquire. 0x1E still delivered via the
worker's `GuestExecutionRunner` (GC-safe), block state saved/restored as today.

**Two integration constraints the audit surfaced:**
- The primary must be registered in **`_guestThreads`** (as Stage 5A did), not left in
  `_externalGuestThreads`. Both wake iterators — `WakeBlockedThreads` (`:4234`) and
  `WakeExpiredBlockedGuestThreads` (`:4339`) — scan only `_guestThreads.Values`, and
  `RegisterBlockedGuestThreadContinuation` (`:4315`) silently no-ops for a handle not in
  `_guestThreads`. A cooperative primary registered only externally would park with **no
  waker → permanent hang** (lost-wakeup by construction). `IsExternalExecutor` is currently
  never assigned `true` anywhere; the dedicated-worker primary should be a normal
  `_guestThreads` entry whose *executor* is pinned, not an "external" thread.
- The per-block predicate re-check that saves ordinary threads from the registration-race
  lost wakeup lives in `RunGuestThread`'s exit path (`:6060`) and the HLE-level recheck
  (`KernelSemaphoreCompatExports.cs:200`). The pinned-worker primary must flow through the
  same commit-Blocked-then-re-test-predicate sequence, or reproduce it, so a signal landing
  during the block-registration window is not lost.

## 8. Stop criteria before landing (unchanged intent)

Implementation is a threading change to `DirectExecutionBackend` and must be validated live
(≥10 Cocoon sessions on Windows) for: sustained ~30 fps, zero UCO `__fastfail`, no return of
the 28cab08 GC-suspend deadlock, clean shutdown, and no pool starvation for ordinary
pthreads. Keep commits separate: (1) this diagnostics/verdict doc; (2) the dedicated-worker
implementation, only after the design is greenlit. No speculative production code landed by
this investigation.

## 9. Parallel work

Five read-only sub-agents (wake-key/TryWake predicate audit; timeout/deadline audit; 0x1E
interaction audit; ready-queue duplicate-enqueue + ordinary-pthread comparison; RIP
`0x800D2622A` identification) plus direct reading of the scheduler and the captured
`run_fix.log`. None edited the tree. Findings were integrated here; the log evidence
(1:1 signal:wake) is the decisive proof.

## 10. Dedicated-worker implementation + validation (Windows-native, 14 Cocoon runs)

The design in §7 was implemented behind `SHARPEMU_NATIVE_GUEST_V2_PRIMARY=1` (requires
`SHARPEMU_NATIVE_GUEST_V2=1`) and validated on Windows. The full diff is preserved as
`docs/native-guest-execution-v2-stage5c-dedicated-primary-worker.patch` (apply with
`git apply`); it was **reverted from the tree** per the milestone stop-rule (see §11).

**What it did:** the true process entry (gated to `frameKind==ProcessEntry` via a
dispatcher-set flag — the debug frame is null without a debugger, so `_activeDebugFrame`
could not be used) runs as a cooperative `GuestThreadState` registered in `_guestThreads`,
pinned to its own persistent `NativeGuestExecutor` created outside the pool run-slot budget.
`RunGuestEntryStub` uses the pinned executor directly (no `pool.Rent/Return`). `ExecuteEntry`
parks the CLR main thread on an exit poll while the ready-dispatcher drives the primary; the
import-loop guard was re-enabled for the primary via an `IsCooperativePrimary` marker.

**Result across 14 gated runs (80–95 s each, `presented_fps` sampled):**

| Outcome | Runs | Share |
|---|---|---|
| Clean sustained ~25–32 fps, zero UCO | d, e, i | 3/14 (21%) |
| Reached ~30 fps then worker-0x1E UCO crash | h, l | 2/14 |
| Early/mid worker-0x1E UCO crash | C, f, k, m, n | 5/14 |
| Primary libunwind spin, 0 fps, no crash | B, g, j, p | 4/14 |
| **Primary host-park UCO crash (the TARGET)** | **none** | **0/14** |

**Confirmed positives:**
- **The target crash is eliminated.** `kernel exception 0x1E host park` = 0 and primary
  `UnmanagedCallersOnly __fastfail` = 0 in *every* run. The host-park nested delivery that
  Stages 4–5C targeted is gone.
- **~30 fps gameplay is reachable.** Runs d/e/i sustain ~25–32 fps for the whole session
  (draw_ms ~40–170 ms) — versus Stage 5A's ~0 fps / draw_ms ~4865 ms. So the dedicated
  pinned worker *did* remove enough per-block overhead to reach gameplay, validating the §7
  design direction. (Refinement of the §4 CASE-B note: pool rent was one cost among several;
  removing it was necessary but the cooperative scheduler round-trip still costs more than
  host-park's in-place `Monitor.Wait`, so the win is real but not free.)

**Two remaining non-deterministic blockers (why it is not production-ready):**
1. **Pre-existing worker-thread 0x1E-storm UCO crash (~50%).** The fatal flight recorder
   shows many `Job.Worker` threads (mtid 45–93) in a `kernel exception 0x1E` storm with
   `sceKernelSignalSema`/`sceKernelWaitEventFlag`/`G+/G- kernel exception 0x1E` rings — the
   Boehm stop-the-world suspending Unity job workers, the UCO firing on one. This is the
   *separate* pre-existing "worker-thread UnmanagedCallersOnly continuation crash" (memory
   `cocoon-gameplay-uco-crash`), not the primary; run h/l show it can hit mid-gameplay after
   30 fps. Making the primary cooperative removed the earlier primary crash and thereby
   *exposed* this one as the dominant blocker.
2. **Primary libunwind spin (~29%), a new stall class.** The stuck runs are not deadlocked —
   they reach ~3–9 million imports spinning in `sceSysmoduleGetModuleInfoForUnwind`
   (NID `4fU5yvOkVG4`) returning `NOT_FOUND`, i.e. the guest C++ unwinder looping on an
   address with no module info. The default (inline) path does not exhibit this. The
   re-enabled import-loop guard did **not** catch it (the spin interleaves other imports, so
   `HasRepeatingImportLoopPattern` does not match a single-NID loop).

## 11. Decision — reverted (patch preserved), CASE B design/docs kept

Success target was ~30 fps **and** zero UCO. It is met in 3/14 runs; ~50% still hit the
pre-existing worker crash and ~29% hit the new unwind-spin stall. Per the milestone stop-rule
("if the dedicated worker causes a new stall/crash class, revert only that implementation and
keep the proven CASE B design/docs"), the implementation was reverted from the tree. Nothing
is lost: the full diff is `native-guest-execution-v2-stage5c-dedicated-primary-worker.patch`,
and the empirical wins (target crash eliminated 0/14; ~30 fps proven) are recorded here.

**Recommended next steps, in order:**
1. Fix the **worker-thread 0x1E-storm UCO crash** (memory `cocoon-gameplay-uco-crash`) — now
   the dominant blocker: the Unity job workers' nested 0x1E continuation must be delivered
   off the CLR-GC path (same principle as the primary fix, applied to the pooled workers).
2. Handle the **primary libunwind `4fU5yvOkVG4` NOT_FOUND spin** — either register the queried
   ranges so the unwinder terminates, or extend the loop guard to multi-import unwinder loops.
3. Re-apply the patch and re-validate; with both blockers cleared the dedicated cooperative
   primary should give consistent ~30 fps with zero UCO (already demonstrated in d/e/i).

The default behavior is unchanged (feature gated + reverted); the tree builds clean; nothing
was pushed.
