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

- Commit: (this milestone)
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
