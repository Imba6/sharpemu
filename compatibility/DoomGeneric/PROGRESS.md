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
