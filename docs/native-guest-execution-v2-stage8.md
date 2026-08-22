# Native Guest Execution V2 — Stage 8: ModuleInitializer top-level inline UCO elimination

Status: **implemented, validated, landed** (branch `gpt-dlsym`, not pushed by this session).
Follows Stage 7, whose gated rented-worker experiment eliminated the 0x1E/nested UCO class
but left one crash: the **top-level module-initializer inline path** (1/10 UCO).

## The remaining UCO (proven)

Two read-only audits + the Stage-7 crash evidence pin it:

- **`ExecuteEntry`'s `CallNativeEntry(ptr)` (`DirectExecutionBackend.cs`) is the only unsafe
  `CallNativeEntry` with no worker-routing escape hatch.** It runs BOTH the process entry
  AND boot module initializers inline on the CLR main thread. `6426`/`6598` route to workers
  under V2/experiment; `217` is the pool fallback; `6920` is a diagnostic probe.
- **The boot-init path** `SharpEmuRuntime.RunPreloadedModuleInitializers` →
  `CpuDispatcher.DispatchModuleInitializer` (its only caller) →
  `DispatchEntryCore(frameKind=ModuleInitializer)` → `TryExecute` → `ExecuteEntry` → inline
  `CallNativeEntry`. A module `DT_INIT` runs arbitrary guest code that reverse-P/Invokes into
  HLE — **guest-above-managed on the CLR main thread → `__fastfail` under a concurrent GC**.
  This includes the FMOD-promoted plugin `DT_INIT`s (commit `17687b7`).
- **`sceKernelLoadStartModule` (Path B) is NOT this path** — it uses
  `scheduler.TryCallGuestFunction` (nested), already worker-routable under the Stage-7 flag.

## Key semantic finding (what makes the fix safe)

A boot module initializer on Path A has **no guest-thread identity**:
`CurrentGuestThreadHandle == 0`, `_activeGuestThreadState == null`, so `IsGuestThread==false`.
Therefore a guest kernel wait **cannot cooperatively yield** (`RequestCurrentThreadBlock`
returns false) and instead **host-parks inline** → the initializer is **run-to-completion**;
`ExecuteEntry` never observes `Blocked`, and `num6` is always a genuine return value.

⇒ It is **safe to run a module initializer synchronously on a rented native worker WITHOUT
`EnterGuestThread`** (keeping `IsGuestThread==false` so waits still host-park), as long as
`num6` is propagated. Run-once (`KernelModuleRegistry.TryBeginModuleStart`, shared with Path
B) and per-module ordering are untouched. (Caveat noted by the audit: `scePthreadSelf`
resolves to a per-host-thread synthetic handle; each init is self-contained on one worker so
mutex-owner identity stays consistent within it — validated empirically below.)

## The fix (landed, gated by V2)

- `CpuDispatcher` tells the backend the entry kind directly
  (`SetActiveEntryIsModuleInitializer(frameKind == ModuleInitializer)`) — the debug frame is
  null without a debugger, so `_activeDebugFrame.Kind` can't be used.
- `ExecuteEntry` routes a module initializer through `RunGuestEntryStub(ptr, hostRspSlot,
  requireNativeWorker: false)` — a **rented pool worker (clean native base)** — instead of
  inline `CallNativeEntry`. The decision is the pure, tested
  `ShouldRunModuleInitializerOnNativeWorker(isModuleInitializer, v2Enabled)`.
  `requireNativeWorker:false` keeps the inline fallback if the pool is momentarily empty.
- The **process entry stays inline** (Stage 5 owns it, cooperatively); only module
  initializers are routed here.

## Validation (Windows-native)

**Cocoon — 10 runs** (V2 + Stage-7 rented-worker experiment + Stage-8 module-init routing +
the dedicated-primary patch applied for testing only, `NATIVE_WORKER_MAX=128`):

| Metric | Result |
|---|---|
| ModuleInitializer UCO | **0 / 10** |
| Total UnmanagedCallersOnly `__fastfail` | **0 / 10** |
| Heap/object/GC corruption (AV, heap-corrupt, double-free, TLSF) | **0 / 10** |
| GC-suspend deadlock | none |
| First-frame Vulkan present | 8 / 10 |
| Module-init ordering / boot completion | intact (all boot inits `Guest returned`; "Warmed" reached) |
| Pool | peak_concurrent=13, live_workers=14, **0 rent timeouts / unavailable** |
| Next blocker reached consistently | **yes — the unresolved-import spin** (`VkqLPArfFdc` / `4fU5yvOkVG4`, ~2.79–2.92 M imports) = blocker #2, the accepted Stage-8 end-state |

Compared to Stage 7 (9/10 UCO=0, the 1 being this module-init path), Stage 8 is **10/10
UCO=0** — the module-init crash class is eliminated.

**Cross-title module-init check** (V2 + Stage-7 experiment, ~45 s startup each): Dreaming
Sarah, Hotline Miami, Solitaire, Unpacking — all **UCO=0**, module inits complete
(`Guest returned`), "Warmed" reached. **No module-init regression.**

## CallNativeEntry classification (final)

| Site | Runs | Safe now? |
|---|---|---|
| `ExecuteEntry` process entry | inline on CLR main thread | Stage 5 (cooperative primary) — separate, patch preserved |
| `ExecuteEntry` **module initializer** | **rented worker (Stage 8)** | **YES (this milestone)** |
| `ExecuteGuestThreadEntry` / `ExecuteGuestContinuationEntry` | worker under V2 (top-level) / Stage-7 (reentrant); inline otherwise | YES under V2/experiment |
| `RunGuestEntryStub` inline fallback (217) | only when pool unavailable | degenerate fallback |
| Sentinel probe (6920) | diagnostic, no real guest code | N/A |

No remaining top-level inline path runs arbitrary guest code guest-above-managed except the
process entry, which Stage 5 already solves.

## Performance / commits / scope

- Performance: module inits are short-lived and sequential; routing each to a rented worker
  adds one pool rent/return per module (peak 13 workers, 0 timeouts) — negligible, no
  startup slowdown observed.
- Landed gated by **V2** (not the Stage-7 experiment flag, which stays default-off). Commits:
  `b908e41` (routing), `b260bdf` (testable decision + regression tests). The dedicated-primary
  patch was applied only for validation and reversed; the Stage-7 experiment remains gated.
- **Blocker #2 (`VkqLPArfFdc` / `4fU5yvOkVG4` unresolved-import / libunwind spin) untouched**
  — the next, separate milestone.
