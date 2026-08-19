# DoomGeneric compatibility progress

Target: `real-tests/DoomGeneric/eboot.bin` — DoomGeneric (https://github.com/ozkl/doomgeneric)
built as a native Gen5 **application** under the VirtualPS5 CRT/runtime model (our
`guest-tests/runtime/crt0.S`, `-nostartfiles`, no ps5-payload-sdk payload CRT). The
ps5-payload-sdk is used only as the cross-toolchain and ABI/header reference.

## Build

Port sources live in `real-tests/DoomGeneric/port/` (also copied into the doomgeneric
source tree). Build from the doomgeneric checkout:

```
cp real-tests/DoomGeneric/port/{doomgeneric_ps5.c,Makefile.ps5} ~/projects/doomgeneric/doomgeneric/
cd ~/projects/doomgeneric/doomgeneric
make -f Makefile.ps5 VPS5=/home/illia/projects/virtualps5
```

This produces `real-tests/DoomGeneric/eboot.bin`. The Doom core statically links; the
libc/sce* symbols become NID imports resolved by VirtualPS5 HLE. Software rendering
only — no SDL/OSMesa/LÖVE, no sound (M1).

## WAD

A freely redistributable WAD must be placed at `real-tests/DoomGeneric/doom1.wad`
(gitignored — no proprietary/bundled assets committed). Local testing used
Freedoom phase 1 (`freedoom1.wad`, v0.13.0). The port passes `-iwad /app0/doom1.wad`;
`/app0` maps to the eboot.bin directory.

## Platform port (`doomgeneric_ps5.c`)

- `DG_Init`: sceUserServiceInitialize/GetInitialUser, sceVideoOutOpen +
  SetBufferAttribute (A8R8G8B8_SRGB, **linear**, 640x400) + RegisterBuffers on a
  page-aligned CPU framebuffer; scePadInit + scePadOpen.
- `DG_DrawFrame`: copy `DG_ScreenBuffer` (640x400, matches A8R8G8B8 byte order) into
  the scan-out buffer with alpha forced opaque, then sceVideoOutSubmitFlip.
- `DG_SleepMs`/`DG_GetTicksMs`: sceKernelUsleep / sceKernelGetProcessTime.
- `DG_GetKey`: edge-detected scePadReadState → Doom key queue (D-pad→arrows,
  Cross→Enter, Circle/Options→Escape, Square→Fire, Triangle→Use, L1/R1→strafe).

## Baseline

Static scan: Gen5, imports 56, VPS5 7, SharpEmu 32, Missing 14, Blockers 0.

## Milestones

### M1 — reach title/menu and present a frame

- Commits: 4b63934 (target), 122d381 (strncasecmp/tolower/toupper),
  e7d0cb2 (stdio libc-heap buffers), 5efd09b (printf integer precision)
- Path to first frame required three HLE fixes (runtime-order driven), each with a
  focused managed regression:
  1. **strncasecmp / tolower / toupper** (NIDs pXvbDfchu6k / PqF+kHW-2WQ /
     TYE4irxSmko). strncasecmp was hit ~9489x on the WAD lump-name lookup; without it
     every lump lookup failed. (`KernelCaseFamilyTests`)
  2. **stdio reads/writes to libc-heap buffers** — `fread`/`fwrite`/`fgets` used the
     guest-only memory path, which cannot reach a `malloc()`/`Z_Malloc()` destination
     (host heap, outside the guest map). The WAD's 50608-byte lump directory silently
     short-read → garbage names → "PNAMES not found". Routed through the compat
     helpers (host-memory fallback). (`KernelHeapCompatMemoryTests`)
  3. **printf integer precision** — `%.3d` ignored precision for integers, so
     DoomGeneric's `"STCFN%.3d"` produced `STCFN33` instead of `STCFN033` →
     "STCFN33 not found". (`KernelSnprintfPrecisionTests`)
- Target after: Doom initializes VideoOut (`Vulkan VideoOut ready: 1920x1080`),
  materializes the 640x400 linear CPU scan-out buffer, and **presents its title/menu
  frame** (`presented guest frame: 640x400`), running its loop continuously with
  **0 fatal HLE dispatch errors, 0 dropped frames, 0 host crashes**. Pad is opened and
  DG_GetKey is wired.
- Scanner after: imports 56, VPS5 7, SharpEmu 35, Missing 11, Blockers 0.
- Managed suite: 989 passed (before) → 1000 passed (with the new regressions).
- Next observed (soft, non-fatal): `putc`/`putchar`/`puts` (console text output, NID
  tLB5+4TEOK0 / m5wN+SwZOR4 / YQ0navp+YIc) and `mkdir` (save dir) are still unresolved
  but only warn. `system` (Jc6E7N+dHz0) is fatal only inside Doom's I_Error path, which
  the clean run does not reach. These are the M2 candidates.

### M2A — startup performance (first frame ~50s → ~9s)

- Date: 2026-08-19
- Commits: 804d1c2 (gate libKernel time-poll logging), 6cc6896 (env-gated stdio
  profiler + compat access-class reporting)
- **Problem before**: first visible Doom frame took ~50s under `make real-run`.
- **Profiling method (do not assume — measure)**: added an env-gated stdio profiler
  (`SHARPEMU_PROFILE_STDIO=1`) emitting a throttled once-per-second summary
  (fopen/fread/fseek counts, bytes, read-size min/max/avg, host-I/O time, compat-copy
  time, guest-map-hit vs libc-host-heap-fallback split), plus wall-clock–timestamped
  runs. Key numbers to first frame:
  - WAD/stdio: ~14 MB across ~2260 `fread`s (avg 6.3 KB, one `fseek` per read),
    **host I/O ~0.6s, compat-copy ~33ms** — essentially all reads land in libc
    malloc/Z_Malloc host-heap buffers (copyHost≈2259, copyGuest=1), and the WAD is
    fully consumed within the **first second**. **stdio is not the bottleneck.**
  - Dominant cost: `sceKernelGetProcessTime` (DoomGeneric `I_GetTime` + tic busy-loop,
    ~12×/frame) was called **83,587×**, each emitting an unconditional
    `Console.Error.WriteLine`. Guest execution serialized on host console I/O.
- **Root cause**: per-poll console spam in the libKernel time helpers
  (`TimeExports.cs`), not stdio. Exactly the per-frame/per-poll logging AGENTS.md
  forbids.
- **Implementation**: gated `sceKernelGetProcessTime` / `GetProcessTimeCounter` /
  `ClockGettime` / `Gettimeofday` / `Usleep` traces behind `SHARPEMU_LOG_KERNEL_TIME=1`
  (default off), matching the existing `SHARPEMU_LOG_STDIO` pattern. Return values
  unchanged. No stdio behavior change (stdio already fast and correct after M1); added
  only the profiler and `TryRead/WriteCompat` out-bool access-class reporting.
- **Performance before/after** (same `make real-run` config: `--log-level=debug
  --trace-imports 16`):
  - first presented frame: **~50s → ~9.4s** (of which ~4.8s is `dotnet run` build-check
    + .NET startup harness overhead; **emulator-internal start→first-frame ~4.6s**,
    under the <5s target). WAD fopen→first present ~2.1s.
  - GetProcessTime log lines: **83587 → 1**; total run-log lines **91667 → 3756**.
  - Steady state unchanged: stable **60 fps**, present_dropped=0, no fatal dispatch,
    no host crash.
- **Regressions added**: `KernelHeapCompatMemoryTests` — mapped-guest buffer reports
  `guestHit == true`; libc malloc() host-heap buffer round-trips via host fallback with
  `guestHit == false` (both stdio memory classes).
- Managed suite: **1000 → 1002 passed**, 0 failed.
- Scanner: unchanged (imports 56, SharpEmu 35, Missing 11, Blockers 0) — no NID added.
- Next blocker: verify real gameplay input transitions (KEYDOWN/KEYUP, held keys) —
  M2B.

### M2B — verify gameplay input + analog stick

- Date: 2026-08-19
- Commit: d3c3d0a (map left analog stick to digital movement)
- **Verification (no code change needed for the core model)**: the M1 edge-detected
  queue computes `changed = buttons ^ prev` and enqueues a key **down** when a bit
  becomes set and a key **up** when it clears, only on change. So a held D-pad/button
  produces exactly one KEYDOWN (held in Doom's `gamekeydown[]`) and one KEYUP on
  release — movement/fire do not stick and are not re-pressed every frame. The mapping
  matches DoomGeneric's defaults (`m_controls.c`): `key_up/down/left/right` = arrows
  (D-pad), `key_fire = KEY_FIRE` (Square), `key_use = KEY_USE` (Triangle),
  `key_strafeleft/right` = L1/R1, menu = Enter/Escape (Cross, Circle/Options). The pad
  button bits in the port match `PadExports` exactly.
- **Change**: `scePadReadState` already reports the analog sticks (`data[4]=leftX`,
  `data[5]=leftY`, 128 at rest) but the port ignored them. Folded the **left stick**
  into the D-pad direction bits before edge detection, behind a deadzone, so a physical
  stick drives movement/menus like the D-pad (one key-down entering the zone, one key-up
  leaving it). Generic translation stays in `doomgeneric_ps5.c`; scePad semantics are
  untouched.
- Verified: eboot rebuilt (prospero-clang), boots to title/menu, stable 60 fps, no fatal
  dispatch, no host crash. Managed suite unaffected.
- Next blocker: runtime-hit libc/filesystem gaps (M2C).

### M2C — runtime-hit libc gaps: console output + mkdir

- Date: 2026-08-19
- Commits: 5f1b417 (putc/putchar/puts), 890a663 (mkdir)
- **Runtime-hit (not just static)**: a clean startup left four unresolved imports —
  `putc` (~358x), `putchar` (~39x), `puts` (~15x) for Doom's banner/printf, and `mkdir`
  (~2x) for the save/config dir.
- **putc/putchar/puts** (NID tLB5+4TEOK0 / m5wN+SwZOR4 / YQ0navp+YIc): implemented on the
  existing stdio infrastructure. `putc` shares `fputc`'s core (identical ABI) — a known
  HLE FILE handle writes to the file, an unknown stream (the bundled libc's
  stdout/stderr) forwards to the host console; `putchar` writes one byte to stdout;
  `puts` writes the string + newline to stdout and returns a non-negative count. No data
  dropped. (`KernelConsoleOutputTests`)
- **mkdir** (NID JGMio+21L4c): implemented the libc POSIX face over the existing
  `sceKernelMkdir` — guest path translated through the same mount table (no arbitrary
  host path exposed), result mapped to the libc ABI (0 / -1+errno, existing → EEXIST
  which POSIX callers treat as success), read-only mounts (e.g. `/app0`) stay denied.
  `system` is deliberately **not** implemented (host-shell-execution boundary) and is
  not runtime-hit. (`KernelMkdirPosixTests`)
- After: with the four resolved, the run has **zero unresolved imports**. Doom's own
  console output now reaches the host log, confirming full init:
  `V_Init → W_Init (Freedoom Phase 1) → M_Init → P_Init → S_Init → HU_Init → ST_Init`.
- Regressions added: 8 (`KernelConsoleOutputTests` ×4 incl. null-fault;
  `KernelMkdirPosixTests` ×4: new / existing / null-ptr / unmapped-denied).
- Managed suite: **1002 → 1010 passed**, 0 failed.
- Scanner after: imports 56, VPS5 7, **SharpEmu 35 → 39**, **Missing 11 → 7**,
  Blockers 0. Remaining misses are static-only (not runtime-hit): `atoi`, `atof`,
  `sscanf`, `strdup`, `remove`, `system`, `__swbuf`, and data symbols `__stdoutp`,
  `__stderrp`, `__isthreaded` — left unimplemented per the runtime-order rule.
- Next: reach/observe E1M1 (M2D).

### M2D — gameplay reachability (automated observation)

- Date: 2026-08-19
- No code change. With M2A–M2C in place the target: fully initializes through `ST_Init`,
  detects Freedoom Phase 1, enters the game loop, and presents continuously at a stable
  **60 fps** with **0 unresolved imports, 0 fatal HLE dispatch, 0 host crash** across
  ~50s runs. Disk reads keep trickling after the title (13.3 MB → 14.15 MB over ~30s),
  consistent with the attract loop cycling title/demo and loading level data (DEMO1 is
  recorded E1M1 gameplay, which drives the 3D render/movement/fire path).
- **Not human-validated**: this harness cannot inject controller input or see pixels, so
  interactive New Game → E1M1 → move/turn/fire/open-door has NOT been visually confirmed.
  See the manual checklist below. Per policy, "playable" is not claimed from continuous
  presentation alone.
- Next (optional, lowest priority): audio (M2E).

## Manual validation checklist (human, with a display + controller/keyboard)

Run: `make real-run ELF=real-tests/DoomGeneric/eboot.bin` (a redistributable
`doom1.wad`/Freedoom must be at `real-tests/DoomGeneric/doom1.wad`). Controller or the
keyboard fallback (arrows = move/turn & menu; Z or Enter = select/OK; X or Esc = back;
C = fire; V = use/open; Q/E = strafe; left stick = move). Verify:

1. Title/menu renders (Freedoom title, 640×400 scaled to the window).
2. Esc/Options opens the menu; Up/Down move the highlight; Enter selects.
3. New Game → Episode (if prompted) → Skill → level starts (E1M1 / Freedoom C1M1).
4. Movement: forward/back (Up/Down), turn (Left/Right); motion is smooth, keys do not
   stick when released, held direction keeps moving.
5. Left analog stick moves/turns like the D-pad (deadzone feels reasonable).
6. Fire (Square/C) fires the weapon repeatedly while held.
7. Use (Triangle/V) opens a door / operates a switch.
8. Strafe (L1/R1 or Q/E) side-steps.
9. Esc returns to the menu; the game keeps presenting at ~60 fps throughout.
10. AUDIO (M3B): menu move/select SFX; pistol/weapon fire; a door open/close; several
    overlapping sounds at once — all audible, no crackling/stutter, no growing latency.
11. PERSISTENCE (M3D): Save Game to a slot, quit the app, relaunch, Load Game — the save
    is present and loads (savegames go to the writable /download0 sandbox mount).

Manual validation 19-08-2026 21-58:
- Doom/Freedoom boots correctly
- menu navigation works
- gameplay input works
- no stuck controls observed
- rendering stable
- stable 60 FPS during gameplay

## M3 — playable game: audio, stability, persistence

### M3A — interactive gameplay (verified)

- Date: 2026-08-19. No new code (input model verified; analog stick already added in M2B).
- The M2B edge-detected model (`buttons ^ prev`, enqueue on change only) was
  re-audited for all required transitions and is correct: key-down on press, key-up on
  release, held keys stay held (no per-frame re-press), simultaneous buttons each edge
  independently, analog stick crossing the deadzone raises the matching D-pad key-down
  and returns-to-neutral raises key-up, and D-pad+stick OR into the same direction bit so
  they never emit conflicting duplicate events.
- **Human-validated 2026-08-19** (see checklist note above): boots, menu navigation,
  gameplay input, no stuck controls, stable 60 fps.

### M3B — audio (SFX through sceAudioOut)

- Date: 2026-08-19
- Commits: ab141b3 (port SFX backend), a62b41c (env-gated sceAudioOut submission profiler)
- **Architecture** (as required): Doom mixer → guest PCM → `sceAudioOut*` → VirtualPS5
  AudioOut HLE → host backend. No SDL/OpenAL/PortAudio; no direct host audio from the
  guest; nothing bypasses sceAudioOut.
- **Port backend** (`i_ps5sound.c`, new): provides `DG_sound_module`. Decodes each DMX
  "DS" lump once to signed-16 mono (cached on `sfxinfo->driver_data`, no per-play alloc);
  a dedicated guest audio thread (`pthread_create`) mixes the 16 channels (per-channel
  16.16 fixed-point step resample to 48 kHz, Doom stereo separation/volume) into a static
  interleaved S16 stereo buffer and submits it via `sceAudioOutOutput`, which blocks for
  pacing (host-queue back-pressure) so the thread self-paces with no busy wait and never
  stalls rendering. Music is a safe stub (Init returns false).
- **Build**: `-DFEATURE_SOUND` makes `i_sound.c` pull in the module; its
  `#include <SDL_mixer.h>` (i_sound.c calls no SDL itself) is satisfied by a stub header
  in the port include dir; links `-lSceAudioOut`. No doomgeneric core changes.
- **HLE**: no semantic change. Added an env-gated (`SHARPEMU_PROFILE_AUDIO=1`, default
  off) submission profiler to `sceAudioOutOutput` (call count / silent count / frames /
  peak amplitude, throttled 1/s) to prove the pipeline is live and non-silent.
- **Verified**: port opens one AudioOut port (48 kHz S16 stereo, 256-frame grain,
  backend=sdl3); the audio thread is scheduled and submits at exactly **187.5 grains/s
  (48000/256)** — real-time cadence held for 160+ s with per-second deltas 187–188 (no
  underrun/drift); after startup the attract demo produces continuous **non-silent** mixed
  SFX (peak ~10k–20k of 32767). `hle-audio-out` guest regression still green; managed
  suite **1010 passed**. SFX status: **working**. Music: **not implemented** (stub).
  Audibility itself is a human step (the harness has no ears) — the profiler proves real
  PCM is submitted.

### M3C — long-run stability

- Date: 2026-08-19. No code change (measurement).
- Ran the target continuously for ~4 min (plus repeated multi-minute runs) with audio on:
  - **present_dropped = 0, present_not_taken = 0**; FPS steady **59–61** (only the 1–2 s
    startup ramp dips).
  - Audio real-time cadence held the whole run (187–188 grains/s); no stall.
  - Main emulator process: working set ramps 313 → 350 MB over the first ~2 min then
    **plateaus** (349.9 → 350.0 MB flat), private memory plateaus ~280 MB; **handles
    stable/decreasing** (742 → 716) and **threads stable** (42 → 35). Bounded growth to a
    steady state — **no leak**, no handle/thread leak, no audio-queue growth.
  - **0 fatal dispatch, 0 host crash, 0 OOM**; only bounded lifecycle warnings (8 stdio
    host-fallback notices, capped).
- Resource lifetime checked: SFX decode cache is bounded (decoded once per lump), fixed
  16-channel array, static mix buffer, HLE submit uses a pooled buffer — no per-submission
  allocation, no per-effect device.

### M3D — save/config persistence

- Date: 2026-08-19. Commit: 0c35b6e (redirect savegames to the writable sandbox mount)
- Runtime-hit: Doom calls `mkdir("./.savegame")` and uses `.`/`GetDefaultConfigDir()`==
  "." for its save dir; our runtime maps a relative path under the **read-only** `/app0`,
  so the mkdir returned -1 and savegames could not be written.
- Fix: point `savegamedir` at `/download0/` — the runtime's writable, per-title sandbox
  mount (auto-created), set via the global after `doomgeneric_Create()`. Stays inside the
  guest filesystem sandbox (no host absolute path exposed); no doomgeneric core change.
  Config (`default.cfg`) is loaded during init and only rewritten at process exit (never
  reached), so config does not round-trip; **savegames** now target a writable location.
  `system()` remains intentionally unimplemented (host-shell boundary) and is not hit.
- Interactive save/load is a human validation step (harness cannot inject input).

### M3E — hot-path logging audit

- Audited the per-frame / per-poll / per-submission / per-clock export paths for
  unconditional logging. After the M2A time-log fix, the hot paths are clean:
  `scePadReadState` (per-poll) and `sceAudioOutOutput` (per-submission) have no per-call
  log; `sceVideoOutSubmitFlip`'s per-flip trace is `_dumpVideoOut`-gated; the compat
  memory host-fallback notice is capped at 8. Both new diagnostics
  (`SHARPEMU_PROFILE_STDIO`, `SHARPEMU_PROFILE_AUDIO`) are env-gated (default off) and
  emit only throttled once-per-second summaries. No new hot-log offender found.

### M3 scanner / performance

- Scanner after M3: imports **64**, VPS5 **11**, **SharpEmu 43**, Missing **7**,
  Data miss 3, Blockers 0; **0 runtime-unresolved imports**. The import/SharpEmu growth
  vs M2 is the audio + pthread symbols now linked and resolved. Remaining 7 misses are
  static-only and not runtime-hit (`atoi`, `atof`, `sscanf`, `strdup`, `remove`,
  `system`, `__swbuf`) plus data symbols (`__stdoutp`, `__stderrp`, `__isthreaded`).
- Performance held: emulator-internal startup ~4.6 s (unchanged); steady state 60 fps,
  present_dropped=0; audio submission cadence 187.5 grains/s.
- Remaining: music (deferred — needs MIDI/OPL, a large subsystem); interactive
  audio/save validation is a human step.
