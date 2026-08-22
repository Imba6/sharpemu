# Stage 12A — current-HEAD default (V2-off) is 0/5 gameplay via THREE failure modes; historical-baseline recipe

Status: **analysis + read-only, no production runtime change** (branch `gpt-dlsym`, not pushed). Five
zero-flag (V2-off, default) Windows captures analyzed. **V2 is now proven NOT to be the cause of the
Baselib stall** (it reproduces on the inline path too). No regression yet proven — historical
reproduction pending.

## 1. Five current-default runs

| run | first frame | suspendPts | cache saves | classification | first abnormal event | terminal reason |
|---|---|---|---|---|---|---|
| 1 | **no** | 0 | 0 | **CRASH — access violation** | `NATIVE EXCEPTION Code=0xC0000005 @ RIP 0x8015A027C` (managed=2 main, sym `ayuoL6Vjz2k+0x6E040C`) | AV before first frame |
| 2 | yes | 1 | 0 | **CRASH — access violation** | AV `0xC0000005 @ 0x8015A027C` (same), then 2nd AV `@ 0x800F09AAB` | AV shortly after first frame |
| 3 | yes | **57** | **2** | **STALL — Baselib 0x86** | `sceKernelWaitSema(0x86)` caller `0x800D18129` TIMED_OUT ×**10820** | user closed window (~20 s stall) |
| 4 | yes | 2 | 0 | **GUARD — scePthreadYield** | `Import-loop guard fired nid=T72hz6ffq08 (scePthreadYield) ret=0x8006B8B5B` @import#23,296,768 | guard force-unwind → `Native backend FAILED: unknown backend error` (fallback) |
| 5 | yes | 2 | 0 | **GUARD — scePthreadYield** | same guard fire @import#24,167,168 | same backend fallback |

No import-loop guard fired in runs 1–3; no `Native backend FAILED` in runs 1–3; no LastError surfaced
(runs 1/2 are caught native exceptions; run 3 is a clean stall; runs 4/5 are the guard fallback with
LastError raced to null as in Stage-10B).

## 2. Crash root-cause grouping (A: same-cause-different-timing, or B: different classes)

**Two root families, not four independent crashes:**

- **Family AV (runs 1, 2):** hard **access violation 0xC0000005 at guest RIP `0x8015A027C`** (in the
  eboot/Unity image, base 0x800000000). Identical fault address in both ⟹ **same root cause at
  different timing** (run 1 before first frame, run 2 just after; run 2's second AV at `0x800F09AAB` is
  a teardown cascade). Fault registers point at a guest heap pointer `RBX=RDI=0x100009A40` (0x1_00000000
  range) — a bad/stale pointer dereference, not a UCO `__fastfail`. This is a **guest-memory fault**,
  the failure mode V2's native-worker routing was meant to avoid.
- **Family Baselib-progress (runs 3, 4, 5):** the game reaches the render loop, then Unity **Baselib
  job/scene progress never completes**. It manifests two ways depending on whether the import-loop
  guard trips first: **guard-fires-on-`scePthreadYield`** (runs 4/5, the main thread spins yielding
  while waiting) or, if the spin instead parks on the recycled Baselib semaphore, the **0x86
  timed-wait stall** (run 3). Same underlying "Baselib work never finishes"; the guard vs 0x86 split is
  just which wait the main thread happened to be in when >5 s elapsed. Run 3 got furthest (57
  suspendPoints, 2 cache saves — *more* than historical `run_fix`'s 33) before stalling.

So the four visible "crashes" = **2 AV crashes (same cause) + 2 guard-unwinds (Baselib-progress) ; run
3 = the same Baselib-progress as a stall.**

## 3. Run-3 stall characterization

Baselib `Baselib_SystemSemaphore` handle **0x86**, consumer caller `0x800D18129`, `needCount=1`, finite
1000 µs poll, TIMED_OUT ×10,820 (import#2.75 M → 31.9 M). This is the exact Stage-11 stall — **now
reproduced on the DEFAULT inline path (V2 off)**. It rendered 57 forced-suspends and saved the pipeline
cache twice, i.e. it was briefly in early rendering, then wedged on the Baselib handshake and the user
closed the window.

## 4. Current-HEAD gameplay count

**0 / 5** reached sustained gameplay (analyzer: all "did NOT reach sustained gameplay").

## 5. Is V2 relevant to the current failure?

**No.** The Baselib 0x86 stall (run 3) and the `scePthreadYield` guard-fire (runs 4/5) occur with
**V2 OFF**. So V2 is **not** the cause of the Baselib-progress stall — that stall is present on the
inline path too. The inline path is, if anything, *worse*: it **additionally** hits the AV crash
(runs 1/2) that V2 does not. Net: neither path reaches gameplay at current HEAD; the inline path adds
an AV crash on top of the shared Baselib-progress stall.

## 6–7. Selected historical revision

- **Primary: `28cab08`** (2026-08-20 17:51, "cpu(scheduler): deliver kernel exceptions to host-parked
  guest threads"). Chosen because: it is the last commit before the post-`run_fix` default-path
  regression candidates (`291de64` VEH @23:29, `2ff1d2a` pthread-mutex @08-21), it is pre-V2, and
  memory ties Cocoon startup-gameplay to this GC-suspend fix — so a gameplay-capable `run_fix` build
  most plausibly matched it (the 17:38 log mtime vs 17:51 commit time implies `run_fix` ran on a dirty
  tree that likely contained this change).
- **Strict-lower-bound alternative: `ff66b25`** (2026-08-20 15:23) — the last commit whose time is ≤
  `run_fix.log`'s 17:38 mtime. Test this if `28cab08` does not reach gameplay.
- Both predate the aerolib build-race fix (`5f69b92`, 08-21); a clean Windows build may hit MSB4062 —
  it was a race, so re-running the build (or deleting `obj/` and rebuilding serially) clears it.

## 8. Windows historical worktree/build/run recipe (user)

Fully isolated clone (safest — cannot touch the main tree or its `artifacts/obj`):

```powershell
# 1. Separate checkout of the historical revision
git clone E:\claude_src\virtualps5 E:\claude_src\virtualps5-historical
cd E:\claude_src\virtualps5-historical
git checkout 28cab08        # primary; use ff66b25 for the alternative run

# 2. Clean Windows-native build (its own artifacts/, no reuse of HEAD's)
dotnet build src\SharpEmu.CLI\SharpEmu.CLI.csproj -c Release -r win-x64
#   if MSB4062/aerolib race: rmdir /s /q artifacts obj  &&  re-run the build

# 3. Exe path
#   E:\claude_src\virtualps5-historical\artifacts\bin\Release\net10.0\win-x64\SharpEmu.exe

# 4. Run Cocoon 3-5x with ZERO SHARPEMU_* flags, pointing at the SAME game files
for ($i=1; $i -le 5; $i++) {
  & "E:\claude_src\virtualps5-historical\artifacts\bin\Release\net10.0\win-x64\SharpEmu.exe" `
    "E:\claude_src\virtualps5\real-tests\Cocoon\PPSA08766-app0\eboot.bin" 2>&1 |
    Tee-Object -FilePath "E:\claude_src\virtualps5\real-tests\Cocoon\PPSA08766-app0\run_stage12_hist_$i.log"
}
```

Point the eboot at the **main** checkout's app0 (the game dumps are git-ignored, not in the clone) so
only the emulator code differs. Drop `run_stage12_hist_1..5.log` under
`real-tests/Cocoon/PPSA08766-app0/` for analysis.

## Decision after historical runs

- **Historical reaches gameplay reliably (≥1–2 of 3–5) while HEAD is 0/5 → REGRESSION PROVEN.** Then
  bisect the default-path candidates in separate clones/worktrees, ≥3 runs each, prioritizing (not
  assuming) `2ff1d2a` (pthread-mutex hand-off — most likely to cause the AV via a corrupted Unity list
  unlink), then `291de64` (VEH), then `17687b7` (FMOD).
- **Historical also crashes/stalls similarly → run_fix was nondeterministic/environment/dirty-tree.**
  Do not bisect; characterize the AV `@0x8015A027C` and the Baselib-progress stall as same-revision
  nondeterminism instead.

## 9. git status

Branch `gpt-dlsym`. New: this doc (analyzer unchanged this pass — it already classifies all five
correctly). Captures git-ignored (not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
