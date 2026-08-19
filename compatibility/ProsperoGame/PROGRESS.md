# ProsperoGame compatibility progress

Target: `real-tests/ProsperoGame/eboot.bin` (Gen5, self-built SharpProspero
`prospero-game` template). Source of truth for application behavior:
`~/projects/SharpProspero/VirtualPS5GameTarget/Program.cs`.

Run command (logs kept under /tmp so git stays clean):

```
make real-run ELF=real-tests/ProsperoGame/eboot.bin TRACE=512 2>&1 | tee /tmp/prospero-game-run.log
python3 scripts/vps5_scan.py --elf real-tests/ProsperoGame/eboot.bin --log /tmp/prospero-game-run.log --output compatibility/ProsperoGame/missing-nids.json
```

## Baseline (before this pass)

Scanner: Gen5, imports 154, VPS5 7, SharpEmu 111, Missing 33, Data miss 3, Blockers 0.

Runtime: earliest unresolved import `libc:strtoull` (NID `5OqszGpy7Mg`) hit
repeatedly during NativeAOT/GC init, immediately followed by
`global region allocator failed to allocate 17592186042368 bytes during init`
and an early `catchReturnFromMain(status=-1)` exit. The ~16 TiB figure is the
garbage a missing `strtoull` feeds into the runtime's numeric config parse.

## Milestones

### M1 — libc integer string conversions

- Commit: 47f6139
- APIs: `strtol` (mXlxhmLNMPg), `strtoll` (VOBg+iNwB-4), `strtoul` (QxmSHBCuKTk),
  `strtoull` (5OqszGpy7Mg). One shared C-standard scanner in
  `src/SharpEmu.Libs/LibcStrtolExports.cs`.
- Before: `strtoull` unresolved → GC allocator asked for 17.5 TB → early
  `catchReturnFromMain` exit, no graphics.
- After: runtime init completes; execution reaches the application frame loop.
  Log shows `Vulkan VideoOut ready: 1920x1080` and a steady import loop
  (`SbU3dwp80lQ` / `j6RaAUlaLv0`). No more allocator failure, no early exit.
- Scanner after: Gen5, imports 154, VPS5 7, SharpEmu 115, Missing 29, Data miss 3, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Libc/LibcStrtolExportsTests.cs` (33 cases:
  bases 2..36, base-0 detection, 0x prefix, endptr semantics, signed/unsigned
  overflow clamps, negative wrap, invalid base, null/unmapped pointer safety).
- Managed suite: 883 passed, 0 failed.
- Next observed blocker: the game submitted frame 0 then livelocked in
  `WaitUntilOnScreen` (spinning `sceVideoOutGetFlipStatus` + `sceVideoOutWaitVblank`)
  because the reported flip status never advanced its flipArg. Addressed in M2.

### M2 — VideoOut flip status reports the submitted flip arg

- Commit: 6f7dcf6
- API: `sceVideoOutGetFlipStatus` (SbU3dwp80lQ) — existing export, corrected. Not
  a new NID; a bookkeeping/layout bug fix in `VideoOutExports.cs`.
- Before: `sceVideoOutGetFlipStatus` always wrote 0 into the struct's `flipArg`
  field (offset 0x18) and wrote `currentBuffer` at the wrong offset (0x20 instead
  of 0x38). SharpProspero's `DisplayDevice.Present` -> `WaitUntilOnScreen` spins
  `while (flipStatus.flipArg < submittedFrame)`; with flipArg pinned at 0 the loop
  never exits from the second frame onward. The game livelocked at ~0.5 submitted
  fps, only the two initial buffers ever reaching present (both black).
- After: `SubmitFlip` records the submitted `flipArg` on the port (flips retire
  synchronously here, so the arg on screen is the last submitted), and
  `GetFlipStatus` reports it at 0x18 with `currentBuffer` moved to 0x38. The game
  now runs its real frame loop: `submitted_fps` ~16-17, both framebuffers
  alternating in present every frame. No livelock, no early exit.
- Scanner after: Gen5, imports 154, VPS5 7, SharpEmu 115, Missing 29, Data miss 3,
  Blockers 0 (unchanged — bug fix, not a new import).
- Regression: `tests/SharpEmu.Libs.Tests/VideoOut/VideoOutFlipStatusTests.cs`
  (struct field offsets, invalid handle, null address). Behavioral proof:
  target submitted_fps 0.5 -> 16.
- Managed suite: 886 passed, 0 failed.
- Next observed blocker: frames still present black — `vk.present_dropped ...
  hasPixels=False version=0`. The game draws with the CPU (SharpProspero
  `AgcTiler.Tile` into the tiled scan-out direct-memory buffer) and our Vulkan
  present path does not pick up those CPU-written tiled scan-out buffers. This is
  AGC/GPU-scanout architecture (surfacing a CPU-tiled direct-memory framebuffer to
  the swapchain), which is out of scope for the unattended pass; recorded for a
  supervised decision. Frame-loop execution itself is healthy.
- Remaining low-risk runtime-hit imports (do not affect the black-frame issue):
  `scePthreadCondattrSetclock` (c-bxj027czs), `sceKernelGetCurrentCpu`
  (g0VTBxfJyu0), `pthread_sigmask` (JZKw5+Wrnaw), `ceil` (gacfOmO8hNs),
  `time` (wLlFkwG9UcQ), `srand48`/`lrand48` (+KSnjvZ0NMc / 5IpoNfxu84U).

## Backlog (frame loop healthy; black-frame issue is architecture-gated)

The game now reaches and runs its frame loop; the remaining visible-output gap
(CPU-tiled scan-out -> Vulkan swapchain) is AGC/GPU-scanout architecture and is
left for a supervised decision. Continuing with low-risk runtime-hit imports.

### M3 — pthread condattr clock

- Commit: (this milestone)
- API: `scePthreadCondattrSetclock` (c-bxj027czs), libKernel.
- Before: unresolved; called early and repeatedly (the game sets CLOCK_MONOTONIC
  on a condattr), returning NOT_FOUND each time.
- After: resolved. Records the selected clock on the attribute (converting the
  condattr tracking set to a clock map), validating against the FreeBSD accepted
  clock set and rejecting others / null attr with EINVAL. No more c-bxj027czs
  warnings; frame loop unchanged (submitted_fps ~16). The game uses untimed
  pthread_cond_wait, so the recorded clock is not yet consumed by the bounded-wait
  timed path (documented in the export).
- Scanner after: SharpEmu 116, Missing 28, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Pthread/PthreadCondattrClockTests.cs`
  (accepted/rejected clocks, null attr, store/destroy lifecycle, upsert).
- Managed suite: 900 passed, 0 failed.
- Next runtime-hit missing import: `sceKernelGetCurrentCpu` (g0VTBxfJyu0).
