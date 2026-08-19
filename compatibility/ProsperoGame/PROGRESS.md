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

- Commit: (this milestone)
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
- Next observed blocker: frames present black (`hasPixels=False
  hasTranslatedDraw=False`); the game is in its loop but not yet drawing. Next
  runtime-hit missing imports in loop order: `scePthreadCondattrSetclock`
  (c-bxj027czs), `sceKernelGetCurrentCpu` (g0VTBxfJyu0), `pthread_sigmask`
  (JZKw5+Wrnaw), `ceil` (gacfOmO8hNs), `time` (wLlFkwG9UcQ),
  `srand48`/`lrand48` (+KSnjvZ0NMc / 5IpoNfxu84U).
