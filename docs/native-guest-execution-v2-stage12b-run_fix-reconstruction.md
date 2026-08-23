# Stage 12B — run_fix reconstruction: it was ff66b25 + the (then-uncommitted) 28cab08 GC-suspend fix + trace flags; git bisect is invalid

Status: **forensic analysis only, no production runtime change** (branch `gpt-dlsym`, not pushed).
ff66b25 (0/3) and 28cab08 (0/5) both fail to reproduce run_fix gameplay, so "one later commit
regressed Cocoon" is disproven. This reconstructs what was special about the run_fix build.

## The proof chain (the ff66b25 stall IS the pre-28cab08 GC-suspend deadlock)

- **Sema handle 0x3F = `SuspendSemaphore`** (`attr=0x0 init=0 max=256`) — the Boehm GC stop-the-world
  suspend/resume ack semaphore. (Handles are per-run creation-ordered and NOT stable across runs; the
  stable key is the **caller RIP `0x805D48C9F`**, a `sceKernelWaitSema` site in the loaded GC/pthread
  runtime module, above eboot.)
- **ff66b25 (BAD, 0/3):** `Thread-283F48DCFD0` is `Blocked` in `sceKernelWaitSema` at `ret=0x805D48C9F`
  while `libKernel:pthread_cond_wait` (`Op8TBGY5KHg`) is wedged at `rip=0x6FFFF8000750`; watchdog
  "No import progress for 20s" repeats; `veh_entry_lock owner=0 depth=0` (VEH lock not held). All three
  runs converge here. This is a **mutator thread host-parked in WaitSema that the GC collector cannot
  suspend** — the classic pre-fix GC-suspend deadlock.
- **run_fix (GOOD):** the **same** `0x805D48C9F` waiter blocks but is **woken** (28 `sema.wait-host-wake`
  of 28 host-blocks), the `SuspendSemaphore` is signaled heavily (136×, need=1/2/21), and **0x1E
  GC-suspend exceptions are delivered 133/136 successfully**. The GC handshake completes → gameplay.
- **`28cab08` = "cpu(scheduler): deliver kernel exceptions to host-parked guest threads"** is exactly
  the mechanism that lets the 0x1E suspend reach a host-parked WaitSema thread. It was committed
  **2026-08-20 17:51**, **13 minutes after** run_fix.log's 17:38 mtime.

⟹ **run_fix ran with the 28cab08 change already present in the working tree (uncommitted).** ff66b25
(the last commit whose time ≤ run_fix) lacks it and therefore deadlocks — its 0/3 is *expected* and
says nothing about the run_fix state.

## 1. run_fix runtime-state fingerprint

- **Execution mode:** pre-V2 **inline** (`native_run_enter=0`; no `SHARPEMU_NATIVE_GUEST_V2`).
- **Env flags (NOT zero-flag):** `SHARPEMU_LOG_SEMA=1` (169 `sema.create`) and
  `SHARPEMU_LOG_GUEST_EXCEPTIONS=1` (408 `guest_exception.*`). The current historical/HEAD comparison
  runs were **zero-flag**, so they differ from run_fix in tracing (and thus timing).
- **Runtime behavior:** 0x1E delivered to host-parked threads (133/136) — i.e. the 28cab08 fix active.
- **Title:** Cocoon PPSA08766 **v01.004.000**, firmware `0x07000038`. Cwd on WSL (`\\wsl.localhost\…`),
  vs the current Windows `E:\claude_src\virtualps5\…` checkout.

## 2–3. Best estimate of the actual code state & checked-out-vs-dirty distinction

**ff66b25 checked out, with the 28cab08 diff applied dirty** (high confidence, from the GC-suspend
handshake + 0x1E-delivery evidence). Possibly additional small uncommitted state, but the 28cab08 delta
is the one proven to be present and decisive.

## 4. Stale build artifacts — for/against

Not disproven, lower priority. The GC-suspend/0x1E behavior is a **source** difference (28cab08), not
an artifact one, and it fully explains the ff66b25→run_fix gap. No evidence points to a stale-artifact
cause; not investigated further because the source explanation is sufficient.

## 5. Generated Aerolib mismatch — for/against

ff66b25/28cab08 predate the aerolib build-race fix `5f69b92` (08-21). A run_fix build *could* in
principle have used newer generated `aerolib.bin`, but there is **no evidence** for it and it would not
produce the GC-suspend/0x1E delivery difference (a scheduler behavior, not NID resolution). **Low
probability**; unresolved but not the explanation.

## 6. Game-input identity

All runs (run_fix, ff66b25, 28cab08, HEAD-default) load **Cocoon PPSA08766 v01.004.000** — same title
version banner. eboot path differs only by host (WSL vs `E:\`), pointing at the same dump. No evidence
of a different game/asset set. (Proprietary contents not inspected; compared by title/version only.)

## 7. Env/config differences (the second real factor)

run_fix had `SHARPEMU_LOG_SEMA=1` + `SHARPEMU_LOG_GUEST_EXCEPTIONS=1`; the current historical runs had
**none**. Heavy tracing slows execution and **shifts scheduling races** — which can matter for a
GC-suspend/Baselib handshake that is otherwise a race. So the comparison was not apples-to-apples: it
differed in *both* the 28cab08 delta *and* the trace flags.

## 8–10. Sema 0x3F identity / whether run_fix clears it / signaler

- **0x3F = SuspendSemaphore** (GC-suspend ack). **run_fix clears the handshake** (waiter at
  `0x805D48C9F` woken; SuspendSemaphore signaled 136×). **ff66b25 does not** (deadlock).
- **Signaler:** the GC-suspend machinery — the 0x1E kernel-exception delivery path (`guest_exception.raise
  type=0x1E`) that acknowledges suspend and posts the SuspendSemaphore. The unblock is enabled by
  28cab08's "deliver kernel exceptions to host-parked guest threads", not a normal guest `SignalSema`.

## 11. Historical binary/build-artifact findings

No run_fix-era `SharpEmu.exe` was located from this WSL session (Windows-side artifacts; not inspected).
Not required — the source-level 28cab08 explanation is conclusive.

## 12. Most likely explanation for run_fix success

**Two necessary/contributing factors, no single-commit regression:**
1. **Necessary:** the 28cab08 GC-suspend / 0x1E-to-host-parked-thread fix (without it: deadlock, per
   ff66b25). run_fix had it dirty.
2. **Reaching gameplay past that point is nondeterministic** — even the committed 28cab08 is 0/5, and
   HEAD hits AV `@0x8015A027C` / Baselib 0x86 / the scePthreadYield guard. run_fix **won that race
   once**, plausibly aided by the `SHARPEMU_LOG_SEMA`/`GUEST_EXCEPTIONS` tracing timing.

## 13. Confidence

- ff66b25 stall = pre-28cab08 GC-suspend deadlock, and run_fix had the 28cab08 fix: **high.**
- run_fix wasn't zero-flag (LOG_SEMA + GUEST_EXCEPTIONS): **high** (log-evidenced).
- Gameplay-past-GC-suspend is nondeterministic (vs additional hidden dirty state): **medium** — 28cab08
  being 0/5 supports it, but 28cab08 was run zero-flag, not with run_fix's flags.

## 14. Is normal git bisect still valid?

**No.** The good/bad boundary is not a single commit: the earliest committed run_fix-equivalent
(28cab08) does not reliably reach gameplay. Bisecting would chase noise. Do **not** bisect.

## 15. Next smallest Windows experiment

**Re-run `28cab08` (the correct baseline, NOT ff66b25) with run_fix's ACTUAL flags** —
`SHARPEMU_LOG_SEMA=1 SHARPEMU_LOG_GUEST_EXCEPTIONS=1`, no others, pre-V2 default — **~8-10 runs**
(`run_stage12_28cab08_flags_N.log`):
- If it reaches sustained gameplay in ≥1 run → **nondeterminism confirmed** (run_fix was a real but
  rare success with the fix); the milestone becomes making the post-GC-suspend path deterministic
  (characterize the AV `@0x8015A027C` and the Baselib 0x86 handshake as same-revision races).
- If 0/10 → run_fix depended on more than 28cab08 (additional dirty state / a generated-artifact
  difference); then reconstruct the 28cab08-vs-run_fix delta more precisely before any fix.

Optionally also run **current HEAD with the same two flags** ×5 — tests whether the flag timing alone
ever lets HEAD reach gameplay.

## 16–17. Analysis-tool changes / commits

No analyzer change needed (it already classifies these logs). Commit: this document only.

## 18. git status

Branch `gpt-dlsym`. New: this doc. Captures git-ignored (not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
