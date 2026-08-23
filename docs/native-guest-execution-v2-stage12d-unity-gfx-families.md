# Stage 12D — at 17687b7 the stall and the AV are DIFFERENT root families; the AV is a `sceAgcCreateShader` NULL-descriptor deref

Status: **forensic analysis only, no production runtime change** (branch `gpt-dlsym`, not pushed).
17687b7 removes the libresonanceaudio `0x8096BF27E` crash (all six runs boot-init 12 modules). The
remaining failures are **not one bug** — they are ≥3 distinct intermittent families.

## Run classification (17687b7, flags; guard on = `_1/2/3`, guard off = `_guardoff_1/2/3`)

| run | outcome | detail |
|---|---|---|
| _1,_2,_3 | guard-kill | import-loop guard fires on `scePthreadYield` during the slow Unity phase |
| guardoff_1 | **stall** | 26 suspendPoints, 1 cache save, **Baselib 0x86** timeouts=46, **no shader failures** |
| guardoff_2 | **AV** | `0xC0000005 @ 0x8000569B6`, thread `UnityGfxDeviceWorker`, preceded by `sceAgcCreateShader`→MEMORY_FAULT flood |
| guardoff_3 | **stall** | 12 suspendPoints, 1 cache save, **Baselib 0x86** timeouts=2160, **no shader failures** |

None reached sustained gameplay.

## 1. Stall vs AV — DIFFERENT root families (not the same bug)

- **Stall (guardoff_1/3):** the **Baselib `Baselib_SystemSemaphore` 0x86** render/scene handshake
  (caller `0x800D18129`) times out repeatedly — the same persistent, nondeterministic Unity job/scene
  stall characterized in Stage 11 (shown there NOT to be an emulator sema defect). These runs make
  **zero** `sceAgcCreateShader` calls that fail — no shader-creation failure is involved.
- **AV (guardoff_2):** an early crash in `UnityGfxDeviceWorker` after a **flood of
  `sceAgcCreateShader`→`ORBIS_GEN2_ERROR_MEMORY_FAULT`** for shader "Unlit/Fog Volume".

They share no last-common divergence in shader/GPU work because the stall runs never take the failing
shader path. **The milestone's "treat as the same bug" hypothesis is disproven for this data.**

## 2. f3dg2CSgRKY identity

**`f3dg2CSgRKY` = `sceAgcCreateShader`** (`libSceAgc`, Gen5, `AgcExports.cs:1753`). It builds a PS5
shader from a header (`Rsi`) + code (`Rdx`) into a destination descriptor (`Rdi`). It returns
`ORBIS_GEN2_ERROR_MEMORY_FAULT` when it cannot read the header words or when
`RelocatePointerField` (`AgcExports.cs:14967`) fails to read/write one of the header's pointer fields
(Cx/Sh registers, UserData, Specials, Input/Output semantics — `:1779-1800`).

## 3. Are its MEMORY_FAULTs causal? YES for the AV, NO for the stall

Causal chain for guardoff_2: `sceAgcCreateShader` MEMORY_FAULTs on "Unlit/Fog Volume" → Unity stores a
**NULL** shader/material descriptor → `UnityGfxDeviceWorker` later indexes into it → **NULL-pointer
deref → AV**. On real hardware `sceAgcCreateShader` would succeed, so the emulator's fault is the
upstream cause. It is **not** causal for the stall (stall runs don't call it).

## 4. AV effective-address reconstruction

Instruction at `0x8000569B6`: `0F B7 74 4F 2E` = **`movzx esi, word ptr [rdi + rcx*2 + 0x2E]`**
(ModRM `74`=disp8+SIB reg=ESI; SIB `4F`=scale×2, index=RCX, base=RDI; disp8 `0x2E`). With **RDI=0,
RCX=0** ⟹ EA = `0 + 0 + 0x2E` = **0x2E** (matches `target=0x2E`, read/`type=0`). The follow-on
`48 8B 44 CF 08` = `mov rax,[rdi+rcx*8+8]` is a second RDI-based access. So it reads a `u16` count at
offset 0x2E of a descriptor **whose base pointer (RDI) is NULL**.

## 5. Pointer provenance

`RDI` should hold the shader/material descriptor pointer Unity got back from `sceAgcCreateShader`.
Because that call returned MEMORY_FAULT, the descriptor is **NULL** and Unity's error handling did not
stop the render path — the NULL propagates into `UnityGfxDeviceWorker`'s descriptor-indexing routine.
`RCX=RSI=RBX=RDX=0` and `RAX=0x602467FFF`/`R8=0x606840C18`/`R15=0x602462CD8` (guest GPU-heap
`0x6_00000000` region) are the surrounding graphics-buffer pointers — consistent with a render-command
build over a shader table.

## 6. Caller chain

RIP `0x8000569B6` and the frame chain (`0x800F1444A`, `0x800ECF574`, `0x800ED9292`, …, `0x8015xxxxx`)
are all inside **eboot / Unity il2cpp** (module hash `ayuoL6Vjz2k`, eboot range
`0x800000000..0x801DAE418`). The site is a Unity **GfxDevice** shader/pipeline descriptor-indexing
routine on the render worker; last HLE before the fault was `d-6uF9sZDIU`. Full symbolication needs the
guest binary (not done); subsystem = Unity renderer / GfxDevice.

## 7. Stalled-thread snapshot

Existing logs suffice — no new diagnostic needed for the stall. guardoff_1/3 stall with `UnityEOPThread`
/ `GfxFlipThread` / job workers parked on the Baselib 0x86 handshake (caller `0x800D18129`, host-park
1000µs poll), the render loop emitting suspendPoints then wedging. This is the Stage-11 stall, unchanged.

## 8. Comparison to HEAD's `0x8015A0xxx` AV — DIFFERENT bug

| | 17687b7 guardoff_2 AV | HEAD default AV |
|---|---|---|
| RIP | `0x8000569B6` | `0x8015A027C` |
| thread | `UnityGfxDeviceWorker` (managed=65) | main `<unnamed>` (managed=2) |
| access | read (`type=0`), target 0x2E | **write** (`type=1`), target 0x19/0x3A |
| precursor | `sceAgcCreateShader`→MEMORY_FAULT + shader errors | **asset loading** (globalgamemanagers/sharedassets), **no shader/f3dg2** |

Different RIP, thread, access type, and precursor ⟹ **not the same fault**. HEAD's `0x8015A0xxx` (also
seen once at 28cab08) is a distinct, long-lived main-thread AV during asset loading; the guardoff_2 AV
is a render-worker NULL-deref caused by shader-creation failure. Both are real but separate.

## 9. Root-cause confidence

- Stall ≠ AV (different families): **high** (stall runs make no failing shader call).
- f3dg2CSgRKY = sceAgcCreateShader, MEMORY_FAULT → NULL desc → AV: **high**.
- AV effective address = NULL deref at 0x2E: **high** (decoded).
- Which `RelocatePointerField` field faults for "Unlit/Fog Volume": **unknown** — needs the
  `SHARPEMU_LOG_AGC_SHADER` trace (branch not distinguishable in the current WARN).
- HEAD AV ≠ this AV: **high**.

## 10. Smallest generic fix

**None proven yet.** The AV's true root is *why* `sceAgcCreateShader` MEMORY_FAULTs for this shader
header — one of six `RelocatePointerField` calls (or the header read) fails. Faking success or
returning OK would be wrong (fake progress). The correct fix is to make `sceAgcCreateShader` handle
that header layout, which requires knowing the failing field first. So the next step is a **diagnostic**,
not a fix.

## 11. Required Windows verification / next experiment

**One diagnostic rerun** of the guardoff config **plus `SHARPEMU_LOG_AGC_SHADER=1`** (existing trace
flag, `AgcExports.cs:1236`) — 2-3 runs → `run_stage12_agcshader_N.log`. It emits `TraceCreateShader`
with the header/dest/code addresses and the failure reason, pinpointing which relocation field (or the
header-version check) faults for "Unlit/Fog Volume". Then the fix is scoped to that field/offset.

Note also: the **primary gameplay blocker is the Baselib 0x86 stall** (the furthest-progressing runs hit
it, not the shader AV). The shader AV is an earlier, shader-specific crash. Both should be tracked, but
fixing `sceAgcCreateShader` will not by itself resolve the stall.

## 12. Commits

Branch `gpt-dlsym`. Commit: this doc (no analyzer/runtime change).

## 13. git status

Clean tree; captures git-ignored (not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
