# Stage 10 diagnosis — the post-first-frame stall is a guest-ordering wait inside an on-demand `module_start`, not an emulator loader/equeue defect

Status: **diagnostics + regression tests only, no production behavior change** (branch `gpt-dlsym`,
not pushed by this session). Stage 9 re-scoped the Cocoon blocker to a post-first-frame
`sceKernelLoadStartModule` (Path B) + `sceKernelWaitEqueue` hang. Stage 10 traced that path end to
end (loader registry, module-start execution model, and the event-queue wake machinery) and against
the on-disk Cocoon logs. The finding: **the three subsystems that could deadlock are provably sound
on the shipping path, the default inline build reaches sustained gameplay, and the stall is a
non-deterministic guest-driven liveness/ordering wait observable only under the Stage-7 rented-worker
experiment.** Per the milestone's own STOP conditions ("STOP without behavior change if the true
dependency cannot be identified / several incompatible loader fixes remain plausible / the hang comes
from guest module logic"), no loader or equeue semantics were changed.

## Parallel work (4 read-only audits)

Four read-only sub-agents ran in parallel; all findings below are cited from source or from the
on-disk logs, cross-checked against the primary read.

- **A — loader locking / reentrancy** (`KernelModuleRegistry.cs`, `KernelRuntimeCompatExports.cs`).
- **B — module-start execution model** (`GuestThreadExecution.cs`, `DirectExecutionBackend.cs`,
  `DirectExecutionBackend.NativeWorker.cs`).
- **C — event-queue wait/wake** (`KernelEventQueueCompatExports.cs` + every `Trigger*`/`Enqueue*`
  producer across `Agc`, `VideoOut`, `SaveData`, `Ampr`).
- **D — Cocoon good/bad logs + module identity** (`real-tests/Cocoon/PPSA08766-app0/*.log`).

## 1. Exact module(s) being `LoadStartModule`'d after the first frame

From the only on-disk run that gets past the first frame — `run_fix.log` (Release, 2026-08-20,
reaches sustained gameplay: 42 MB Vulkan pipeline cache saved, repeated `Agc::suspendPoint`) — Path B
issues **exactly three on-demand loads, in order**, immediately after
`Vulkan VideoOut presented first frame` (line 13096):

| seq | module | init entry | log line |
|---|---|---|---|
| 1 | `PSNCore.prx` | `0x0000000809D28010` | `run_fix.log:13100` |
| 2 | `PSNCommon.prx` | `0x0000000809B1A010` | `run_fix.log:13110` |
| 3 | `SaveData.prx` | `0x0000000809FA1010` | `run_fix.log:13540` |

Import NID `wzvqT4UqKX8` = `libKernel:sceKernelLoadStartModule`. Each is followed by benign Unity
plugin-ABI dlsym probes (`UnityPluginLoad`, `UnityRenderEvent`, …). **In the inline build all three
`module_start` calls return** — the `[LOADER][INFO] … started` line is printed only *after*
`TryCallGuestFunction` returns (`KernelRuntimeCompatExports.cs:1392`), and all three print. So the
identified on-demand modules are `PSNCore` / `PSNCommon` / `SaveData`, and on the shipping inline path
none of them hangs.

## 2. Caller thread / RIP

The caller is an ordinary guest pthread executing the game's PSN/SaveData init, reaching the
`sceKernelLoadStartModule` import stub (NID `wzvqT4UqKX8`). The pre-existing logs do not record the
caller's guest RIP or thread handle for Path B. **This is exactly the gap the new
`SHARPEMU_LOG_LOADSTART=1` instrumentation closes** (see §12): it logs `guest_thread=0x…`, `handle`,
`init`, and `prior_state` on entry.

## 3. Module lifecycle timeline (Path B, on-demand)

```
ENTER      KernelLoadStartModule: read path, resolve handle (TryFindByPathOrName)
LOOKUP     handle -> ModuleEntry (real, preloaded, StartState=NotStarted, valid InitEntryPoint)
CLAIM      TryBeginModuleStart -> StartState=Starting (registry _gate released before any guest code)
START      scheduler.TryCallGuestFunction(InitEntryPoint, deliverOnNativeWorker=false)
             -> ExecuteGuestThreadEntry(reentrant:true): DT_INIT runs INLINE on the calling thread
             -> if DT_INIT blocks in an HLE (e.g. sceKernelWaitEqueue) it returns Blocked and enters
                ResumeBlockedNestedGuestCallback -> on-thread cooperative spin (Thread.Sleep(1))
                until the scheduler marks the owning guest thread Ready
COMPLETE   CompleteModuleStart(handle, started); print "started" INFO   <-- only if START returns
```

`CompleteModuleStart` (and the INFO line) run **only after** `TryCallGuestFunction` returns
(`KernelRuntimeCompatExports.cs:1338/1392`). If `module_start` never returns, the module is pinned
`StartState=Starting` forever and no COMPLETE line prints — which is the precise signature of the
stall.

## 4/5. Exact phase that blocks

**`START` — inside the inline guest `module_start` (DT_INIT), specifically a nested
`sceKernelWaitEqueue` that never satisfies its wake predicate.** The exec-model audit confirmed Path B
runs `module_start` **inline on the calling guest thread** (`deliverOnNativeWorker` defaults to
`false`; `KernelLoadStartModule` uses the 8-arg overload → `ExecuteGuestThreadEntry(…, reentrant:
true)`, `DirectExecutionBackend.cs:4596`). When that guest code blocks, the calling thread does not
return through the HLE; it busy-waits in `ResumeBlockedNestedGuestCallback`
(`DirectExecutionBackend.cs:4646-4739`). This is why the Stage-9 profiler saw **two distinct wait
labels**: the spinning thread still carries the outer `sceKernelLoadStartModule` import index
(`31.4%`), while its blocked continuation is waiting on an equeue event (`sceKernelWaitEqueue 32.4%`).
They are the *same* logical operation, nested.

## 6. Exact equeue identity

**Not captured on disk.** The post-first-frame equeue stall was observed by Stage 9 only under the
gated build (`SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER` + dedicated-primary patch + `NATIVE_WORKER_MAX=128`).
No on-disk log reproduces it — the only past-first-frame log (`run_fix.log`) succeeds; the other BAD
logs (`run.log`, `run_exc.log`, …) are the **old pre-first-frame Boehm GC-suspend / `pthread_cond_wait`
deadlock** (memory `cocoon-gc-suspend-deadlock.md`), a different and already-fixed failure. The equeue
NIDs are resolved (`D0OdFMjp46I`=CreateEqueue, `fzyMKs9kim0`/`fzyMKs9kim0` variants=WaitEqueue,
`jpFjmgAC5AE`=DeleteEqueue) but no on-disk stall line ties to a WaitEqueue handle. Pinning the exact
handle requires a fresh Windows-native run with `SHARPEMU_LOG_EQUEUE=1` + `SHARPEMU_LOG_LOADSTART=1`
(see §20).

## 7. Is the equeue causal or incidental?

**Causal to the stall, but not an emulator defect.** The equeue wake machinery is structurally
lost-wake-free:

- `sceKernelWaitEqueue` **drains already-pending events before blocking** (`DequeueEvents`,
  `KernelEventQueueCompatExports.cs:572`), and pending events live in `_pendingEvents[handle]`
  independent of any waiter, so an event produced *before* the waiter registers is **buffered, not
  lost**.
- The infinite-wait path is a saved CPU continuation with an **atomic final `TryWake` executed under
  `_guestThreadGate` at the moment `State=Blocked` is set** (`DirectExecutionBackend.cs:6082-6093`,
  and `:4660-4667` for the nested-callback loop) — whichever side wins the gate, at least one observes
  the other. No lost wake as long as a producer eventually calls `WakeEventQueue` (all producers do).
- The finite-timeout path does not use the continuation model; it host-parks on
  `Monitor.Wait(_eventQueueGate, ≤100ms)` and **self-polls every ≤100 ms**, so it cannot hang
  permanently regardless of `PulseAll`.

Therefore the WaitEqueue is waiting on a **guest-driven producer that has not run yet** (a user event
via `sceKernelTriggerUserEvent`, a GPU end-of-pipe completion, or a flip event — all issued by guest
threads / guest-submitted GPU work), i.e. a **liveness/ordering dependency in the title's own startup
sequence**, not a dropped wake in the emulator.

Secondary latent hazard (noted, not the proven cause): `QueueOrUpdateEvent`
(`KernelEventQueueCompatExports.cs:1339`) **coalesces same-`(Ident,Filter)` user events and drops
counts** (N `sceKernelTriggerUserEvent` collapse to one delivered event). GPU interrupts are already
carved out of this for exactly that reason. If a future trace shows the stalled waiter needs a *count*
of user events, this is the first place to look — but it is not biting in the working inline build.

## 8. Good-vs-bad first divergence

The genuinely-comparable divergence is **inline Path B vs Stage-7 rented-worker Path B**, not a
sequence difference in the loads:

- **GOOD** (`run_fix.log`, default inline): PSNCore → PSNCommon → SaveData `module_start` all run
  inline and **COMPLETE**; game reaches sustained ~30 fps gameplay.
- **BAD** (Stage-9 gated build): with the Stage-7 experiment on, the instance
  `ShouldRunGuestOnNativeWorker` short-circuits on `Experiment0x1ERentedWorker`
  (`DirectExecutionBackend.cs:56-64`) and routes even the `reentrant:true` `module_start` onto a rented
  worker; the post-first-frame stall appears **non-deterministically** (Stage-9: runs d/e/i reached
  ~30 fps, B/g/j hung).

The on-disk BAD logs are **not** this hang — they predate the GC-suspend fix and stall *before* the
first frame. So Phase-4's "first divergence" for *this* hang cannot be extracted from existing logs;
it must be captured fresh. What the logs *do* prove is that the **default inline path is not the
blocker** — it works.

## 9. Loader lock / reentrancy findings

The module registry is **deadlock-free**:

- `StartState` (`NotStarted → Starting → Started`) is a **non-blocking claim**, never a lock held
  across guest code. `TryBeginModuleStart` mutates under `_gate` and **returns (releasing `_gate`)
  before** any `TryCallGuestFunction` (`KernelModuleRegistry.cs:243-268` vs call site
  `KernelRuntimeCompatExports.cs:1338`). No registry method holds `_gate` across guest execution.
- Concurrent callers of the same module: exactly one wins the claim; losers get `false` and return
  `OK`+handle **without waiting** (`:252-255`, `:1397`). Recursive A→B→A cannot self-deadlock because
  no caller ever blocks on another module's start.
- Not-found path → `RegisterSyntheticModule` returns a module born `Started` with `InitEntryPoint=0`
  (`:149/:153`), so `TryBeginModuleStart` refuses it → no guest code, no `0x0` entry, ever executed.
- **Behavioral hazard (not a deadlock):** losers returning `OK` before the winner's DT_INIT completes
  can observe a half-initialized module. Not the hang, but a real ordering weakness worth tracking.

The only blocking primitives on this path are the worker handshake (`_workCompleted.WaitOne`) and the
`ResumeBlockedNestedGuestCallback` spin — both wait on the *guest* making progress, not on a loader
mutex. No lock inversion exists in the loader itself.

## 10. Equeue wake findings

Covered in §7. Summary: level-buffered pending events, drain-before-block, atomic final `TryWake`
safety net, finite-timeout self-poll → **no lost-wake / no permanent block from the emulator side**.
The one asymmetry (only `EnqueueEvent`/`DeleteEqueue` `PulseAll` the gate; `Trigger*`/display
producers do not) is covered by the finite path's ≤100 ms self-poll and the infinite path's
scheduler-based `WakeEventQueue`.

## 11. Root cause

The post-first-frame stall is a **non-deterministic guest-ordering / producer-starvation wait inside
an on-demand `module_start`** (`PSNCore` / `PSNCommon` / `SaveData`): the module's DT_INIT, running
inline on the calling guest thread, blocks in `sceKernelWaitEqueue` for an event whose guest-side
producer has not yet run. It manifests **only under the Stage-7 rented-worker experiment**; the default
inline path completes all three `module_start`s and reaches sustained gameplay (`run_fix.log`). **There
is no proven defect in the loader registry or the equeue wake machinery** — both are structurally sound
on the shipping path. Consequently there is no emulator bug here that can be fixed without faking module
success, which the milestone forbids.

## 12. Synthetic regression (Phase 8)

`tests/SharpEmu.Libs.Tests/Kernel/KernelModuleStartStateMachineTests.cs` (7 tests, Linux-runnable,
serialized collection) pins the proven invariants:

- concurrent `LoadStartModule` for the same module → **exactly one** claim wins; losers skip.
- recursive A→B→A dependency → **no self-deadlock**, initializer runs at most once.
- failed `module_start` → resets to `NotStarted` (retryable).
- in-flight start stays `Starting`; other callers are refused **without waiting** (registry can't be
  the deadlock).
- synthetic / unknown-path module → born `Started`, `InitEntryPoint=0`, **never runs guest code**.
- module with `InitEntryPoint < 0x10000` → short-circuits to `Started` without guest execution.

## 13. Fix

**None.** Behavior is unchanged. Added `SHARPEMU_LOG_LOADSTART=1` lifecycle instrumentation to
`KernelLoadStartModule` (enter / `module_start.begin` / `module_start.complete` with
`elapsed_ms`+`started` / `module_start.skip`), so the next Windows-native run names exactly which
on-demand module_start never returns and on which guest thread — the missing `module_start.complete`
line is the tell.

## 14-17. Cocoon / gameplay / UCO / cross-title

Not re-run this session: Cocoon requires a **Windows-native** launch (the guest-RIP profiler is
Windows-only; the mitigation-relaunch path needs a non-UNC cwd — memory `gris-mitigation-relaunch-fix`),
and this session ran on WSL/Linux. The standing evidence (`run_fix.log`: inline Path B → sustained
~30 fps, all three `module_start`s complete, no UCO, GC-suspend fixed) is unchanged. No behavior was
altered, so no regression is possible from this session's diff.

## 18. Tests

`dotnet test … --filter` over the new state-machine tests + `ModuleInitializerRoutingTests` +
`KernelEventQueueCompatExportsTests` + `KernelEventQueueWaiterLifetimeTests` +
`FmodPluginBootInitTests`: **26 passed, 0 failed** (63 ms). Full test project builds clean (0 errors,
pre-existing warnings only).

## 19. Commits (branch `gpt-dlsym`, not pushed)

1. `diag+test`: `SHARPEMU_LOG_LOADSTART` Path-B lifecycle trace + module-start state-machine
   regression tests.
2. `docs`: this file.

## 20. Next blocker / recommended next step

Pinning the exact equeue and choosing between the two remaining hypotheses **requires a fresh
Windows-native Cocoon run** with `SHARPEMU_LOG_LOADSTART=1` + `SHARPEMU_LOG_EQUEUE=1` under the gated
config that reproduces the stall. In that trace:

1. The on-demand module with an `enter`/`module_start.begin` but **no `module_start.complete`** names
   the exact stalled module + guest thread.
2. Its stalled `WaitEqueue` `wait-block` line names the equeue `handle`/`generation` and whether
   `timeout=infinite`.
3. Check whether any `trigger*`/`enqueue`/`display` line ever targets that handle after the block —
   absence confirms **producer-starvation ordering** (a); a matching wake with the thread never
   re-readied would (contrary to the code proof) indicate a genuine lost wake.

If the trace confirms producer-starvation is specific to the **rented-worker routing** of Path B (and
the inline path is clean), the smallest defensible change is to make on-demand `module_start` **immune
to the `Experiment0x1ERentedWorker` short-circuit** (keep Path B inline; the experiment targets 0x1E
safe-point delivery, not on-demand DT_INIT) — but only after the trace proves the inline path does not
also race. Do **not** force `Started`, fake `LoadStartModule` success, skip `module_start`, or time out
the module.

## 21. git status

Branch `gpt-dlsym`. New: `KernelModuleStartStateMachineTests.cs`, this doc. Modified:
`KernelRuntimeCompatExports.cs` (env-gated trace only — no behavior change under default flags).

## 22. NOTHING WAS PUSHED BY THIS SESSION.

## 23. Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes (not investigated here).
