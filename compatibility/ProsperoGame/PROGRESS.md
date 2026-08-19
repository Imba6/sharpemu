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

- Commit: 343b009
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

### M4 — expose current cpu query

- Commit: 6248481
- API: `sceKernelGetCurrentCpu` (g0VTBxfJyu0), libKernel.
- Before: unresolved; called during runtime scheduling/GC, returning NOT_FOUND.
- After: resolved. Returns the processor actually executing the calling thread
  (`Thread.GetCurrentProcessorId()`), folded into the PS5 8-core range so callers
  using it as a per-core index stay in bounds. No more g0VTBxfJyu0 warnings; frame
  loop unchanged (submitted_fps ~16).
- Scanner after: SharpEmu 117, Missing 27, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Kernel/KernelGetCurrentCpuTests.cs`
  (result always in [0,7] across many calls).
- Managed suite: 901 passed, 0 failed.
- Next runtime-hit missing import: `pthread_sigmask` (JZKw5+Wrnaw).

### M5 — pthread_sigmask thread signal mask

- Commit: 7456ec6
- API: `pthread_sigmask` (JZKw5+Wrnaw), libKernel.
- Before: unresolved; called during runtime thread init, returning NOT_FOUND.
- After: resolved. Maintains the calling thread's blocked-signal mask in
  [ThreadStatic] storage (FreeBSD sigset_t, 16 bytes), honouring
  SIG_BLOCK/UNBLOCK/SETMASK, round-tripping the previous mask through oldset, and
  returning EINVAL (bad how) / EFAULT (bad pointer) as the POSIX return value. We
  do not deliver async POSIX signals to guest threads, so the mask has no delivery
  effect, but the save/restore round-trip callers rely on is correct. No more
  JZKw5+Wrnaw warnings; frame loop unchanged (submitted_fps ~16).
- Scanner after: SharpEmu 118, Missing 26, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Pthread/PthreadSigmaskTests.cs`
  (round-trip, block/unblock bit ops, invalid how, null-set query, bad pointers).
- Managed suite: 907 passed, 0 failed.
- Next runtime-hit missing import: `ceil` (gacfOmO8hNs) — used in the frame loop.

### M6 — libc ceil

- Commit: 11b8e69
- API: `ceil` (gacfOmO8hNs), libc.
- Before: unresolved; called ~1078 times across the frame loop (FrameStats
  overlay math), returning NOT_FOUND each time.
- After: resolved via a scalar-double libm export (arg/result in XMM0, which the
  import dispatcher captures). Math.Ceiling matches C ceil for NaN/+-inf/-0.0.
  ceil now dispatches ORBIS_GEN2_OK; frame loop unchanged (submitted_fps ~16).
- Scanner after: SharpEmu 119, Missing 25, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Libc/LibmExportsTests.cs` (values,
  negative-zero sign, NaN, infinities).
- Managed suite: 918 passed, 0 failed.
- Remaining runtime-hit missing imports: `srand48`/`lrand48` (+KSnjvZ0NMc /
  5IpoNfxu84U, RNG seeding), `time` (wLlFkwG9UcQ).

### M7 — libc drand48 family (srand48 + lrand48)

- Commit: c52268b
- APIs: `srand48` (+KSnjvZ0NMc), `lrand48` (5IpoNfxu84U), libc. One shared 48-bit
  LCG state in `src/SharpEmu.Libs/LibcRand48Exports.cs`.
- Before: both unresolved; the runtime seeds/draws RNG during init (srand48 x1,
  lrand48 x8), returning NOT_FOUND.
- After: resolved with the standard recurrence (a=0x5DEECE66D, c=0xB, mod 2^48);
  srand48 seeds X=(seed<<16)|0x330E, lrand48 returns the top 31 bits. No more
  unresolved RNG imports; frame loop unchanged (submitted_fps ~16).
- Scanner after: SharpEmu 121, Missing 23, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Libc/LibcRand48ExportsTests.cs`
  (independently-computed reference sequences for seeds 0/1/12345, low-32-bit
  seeding, reseed reproducibility, 31-bit range).
- Managed suite: 924 passed, 0 failed.
- Only remaining runtime-hit missing import: `time` (wLlFkwG9UcQ).

### M8 — libc time

- Commit: 4f0e09e
- API: `time` (wLlFkwG9UcQ), libc.
- Before: unresolved; called once during init, returning NOT_FOUND.
- After: resolved. Returns seconds since the Unix epoch (using the same wall clock
  as gettimeofday) and stores it through tloc when non-null; a bad tloc returns
  (time_t)-1 instead of faulting. All runtime-hit imports are now resolved: a
  target run shows zero unresolved imports.
- Scanner after: SharpEmu 122, Missing 22, Data miss 3, Blockers 0.
- Regression: `tests/SharpEmu.Libs.Tests/Kernel/KernelTimeTests.cs` (null tloc,
  tloc stores == return, bad pointer).
- Managed suite: 927 passed, 0 failed.
- Next observed blocker: the existing `fopen` HLE (xeYO4u7uyJ0) throws a host
  ArgumentException during init because it receives an empty path string — a real
  robustness bug flagged in the task. Investigating next.

### M9 — fopen/freopen: empty resolved path is ENOENT, not a host crash

- Commit: c99d20e
- API: `fopen` (xeYO4u7uyJ0), `freopen` — existing exports, corrected. Bug fix,
  not new NIDs.
- Root cause (source + runtime trace, no binary analysis): the .NET NativeAOT
  runtime probes Linux-only paths during init — `/proc/self/mountinfo`,
  `/sys/devices/system/cpu/cpu0/cache/index*/size` (CPU cache detection). These
  have no PS5 mount, so `ResolveGuestPath` returns "" (it also returns "" for a
  denied mount and for empty input). `new FileStream("")` throws an
  ArgumentException, which is not IOException and so escaped the catch as a host
  `HLE dispatch error`.
- Fix: treat an empty/whitespace resolved host path as ENOENT and return NULL, as
  the C library does for `fopen("", ...)` and for a nonexistent file; also broaden
  the fopen/freopen catch to ArgumentException/NotSupportedException so no other
  malformed path can crash the host. Not ProsperoGame-specific.
- Before: 8+ `HLE dispatch error for xeYO4u7uyJ0: ArgumentException` per run.
- After: 0 dispatch errors of any kind; the runtime gets NULL and falls back to
  defaults for the probed values. Target runs its frame loop with zero unresolved
  imports and zero dispatch errors (submitted_fps ~16).
- Scanner after: SharpEmu 122, Missing 22, Data miss 3, Blockers 0 (unchanged —
  bug fix).
- Regression: `tests/SharpEmu.Libs.Tests/Libc/LibcStdioFopenTests.cs` (fopen/
  freopen empty and whitespace path -> NOT_FOUND, no throw, NULL FILE*).
- Managed suite: 930 passed, 0 failed.

## Static backlog (not runtime-hit by ProsperoGame)

With every runtime-hit import resolved and the frame loop healthy, the remaining
progress is the low-risk fallback backlog from the task: standard libc functions
the target does not itself call but that lower the static missing count and are
correct to have. Each gets real semantics, pointer validation, a focused
regression, and its own commit.

### M10 — libc bcmp

- Commit: a366112
- API: `bcmp` (5TjaJwkLWxE), libc.
- Byte comparison returning 0 when equal, nonzero otherwise (mirrors the existing
  Memcmp; validates guest pointers, MEMORY_FAULT on a bad address).
- Not runtime-hit; resolved statically. Scanner SharpEmu 122->123, Missing 22->21.
- Regression: `tests/SharpEmu.Libs.Tests/Kernel/KernelBcmpTests.cs`.
- Managed suite: 935 passed, 0 failed.

### M11 — libc sched_yield

- Commit: dbc1bbb
- API: `sched_yield` (6XG4B33N09g), libc.
- Yields the processor via Thread.Yield (same as the existing scePthreadYield) and
  returns 0. Not runtime-hit; resolved statically. Scanner SharpEmu 123->124,
  Missing 21->20.
- Regression: `tests/SharpEmu.Libs.Tests/Pthread/SchedYieldTests.cs`.
- Managed suite: 936 passed, 0 failed.

### M12 — libc strerror + strerror_r

- Commit: 720f626
- APIs: `strerror` (RIa6GnWp+iU), `strerror_r` (RBcs3uut1TA), libc. Shared FreeBSD
  sys_errlist message table in `src/SharpEmu.Libs/LibcStrerrorExports.cs`.
- strerror returns a pointer to guest-resident static storage per distinct message
  (materialized once and cached); strerror_r is the XSI variant returning 0 /
  EINVAL (unknown errno) / ERANGE (truncated, overrides EINVAL) and always
  null-terminating. Unknown errnos render "Unknown error: N".
- Not runtime-hit; resolved statically. Scanner SharpEmu 124->126, Missing 20->18.
- Regression: `tests/SharpEmu.Libs.Tests/Libc/LibcStrerrorExportsTests.cs`
  (message table known/unknown, strerror_r return codes and truncation, strerror
  pointer reuse where the harness backs allocation).
- Managed suite: 949 passed, 0 failed.

### M13 — libc strtok_r

- Commit: (this milestone)
- API: `strtok_r` (enqPGLfmVNU), libc.
- Reentrant tokenizer that splits a guest string in place on any delimiter
  character, resuming from the caller's saveptr; skips leading/repeated
  delimiters, null-terminates each token in guest memory, and bounds its scan
  against an unterminated string. Guest-pointer safe (returns NULL on a null
  saveptr, unreadable delim, or a write it cannot make).
- Not runtime-hit; resolved statically. Scanner SharpEmu 126->127, Missing 18->17.
- Regression: `tests/SharpEmu.Libs.Tests/Libc/LibcStrtokRTests.cs` (single/multi
  delimiter, leading/trailing/repeated delimiters, empty and all-delimiter input,
  no-delimiter single token, null saveptr).
- Managed suite: 957 passed, 0 failed.

## Current state / remaining work

The target runs its application frame loop cleanly: zero unresolved imports, zero
HLE dispatch errors, both framebuffers flipping every frame at ~16 submitted fps.

The one remaining gap to a visible frame is architecture-gated and left for a
supervised decision: frames present black (`vk.present_dropped ... hasPixels=False
version=0`). SharpProspero draws on the CPU into a tiled scan-out direct-memory
buffer (`AgcTiler.Tile`), and the Vulkan present path does not surface those
CPU-written tiled scan-out buffers to the swapchain. Wiring that up is AGC/GPU
scan-out architecture (out of scope for the unattended pass).

Data imports: scanner still reports 3 unresolved-data candidates (Need_sceLibc,
_Stderr, _Stdout); runtime shows no data-import faults and execution does not
depend on them, so they are left as-is per the task guidance.
