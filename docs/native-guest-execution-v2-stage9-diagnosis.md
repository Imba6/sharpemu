# Stage 9 diagnosis — the post-first-frame stall is NOT the unwind spin

Status: **diagnostics only, no production change** (branch `gpt-dlsym`, not pushed by this
session). The milestone framed the current Cocoon blocker as a libunwind /
`sceSysmoduleGetModuleInfoForUnwind` (`4fU5yvOkVG4`) spin. The evidence contradicts that: the
stall is a **module-load / event-queue wait**, and the unwind NIDs are called only a handful
of times. Per the milestone's own STOP conditions ("STOP without production change if … the
blocker isn't unwind … do NOT fake success"), no unwind change was made.

## What the NIDs actually are (resolved)

| NID | Symbol | In current stall | Calls (un-throttled WARN) |
|---|---|---|---|
| `4fU5yvOkVG4` | `sceSysmoduleGetModuleInfoForUnwind` | implemented, returns `NOT_FOUND` | **8** (0 in some runs) |
| `crb5j7mkk1c` | `_is_signal_return` (libunwind helper) | unresolved | **8** |
| `VkqLPArfFdc` | **`sceImeKeyboardGetInfo`** (IME keyboard — NOT unwind) | unresolved | **4** |
| `bxGoVxpdSPQ` | `sceAgcCbSetShRegisterRangeDirectGetSize` (AGC/GPU) | unresolved | **8** |

The unresolved WARN at `DirectExecutionBackend.Imports.cs:637` fires on **every** unresolved
import (no per-NID throttle), so these counts are exact. **None of them spins** — 4–8 calls
total across a ~2.9 M-import run. `VkqLPArfFdc`, which Stage 7/8 called the spin NID, is
`sceImeKeyboardGetInfo`, unrelated to unwinding.

## What the stall actually is (guest RIP + wait profiler)

150 s run, `SHARPEMU_PROFILE_GUEST_RIP=1`, V2 + Stage-7 rented-worker experiment + the
dedicated-primary patch (applied for testing) + `NATIVE_WORKER_MAX=128`:

- **`waiting = 99.7% of guest thread-time`** — the guest is **blocked, not spinning**.
- `top_wait: <idle-or-scheduler> 33.7% | sceKernelWaitEqueue 32.4% | sceKernelLoadStartModule
  31.4% | read 0.3%`.
- `top_rip` is ~96% at two **host** import-trampoline addresses (`0x7FFEB8700ED4` +
  `0x7FFEB8700A04`) — i.e. threads parked *inside* HLE imports, not executing guest code.
- All **12 boot module initializers (Path A, incl. `libresonanceaudio.prx`) return cleanly**
  (`Guest returned: 0` ×12) — Stage-8 module-init routing is fine; no Path-A hang.
- `videoout draws = 0` after the first frame; `0x1E` GC suspends, when they occur, take
  **2–5 s each** (`elapsed_ms`).

So after first frame the game **deadlocks/waits**: one thread inside a guest-driven
`sceKernelLoadStartModule` (Path B, on-demand — no completion log), another on
`sceKernelWaitEqueue`, the rest idle. This is a **module-load / event-queue hang**, most
likely a startup race (Stage-5C runs d/e/i reached sustained ~30 fps; B/g/j hung the same
way — non-deterministic). The "unresolved-import spin" / "libunwind spin" label from Stage 7/8
was an **imprecise characterization of this same waiting stall** (the unwind NIDs appeared in
the tail but are not the cause).

## Verdict

- **The unwind path is not the blocker.** `sceSysmoduleGetModuleInfoForUnwind` returns
  `NOT_FOUND` for some address and is called ≤8 times without looping; the C++ unwinder is not
  in a spin. Whether its `NOT_FOUND` is even wrong for that address is moot for the stall
  (it does not gate progress), and the queried address was not captured — so there is **no
  proven unwind bug to fix** here. Forcing an unwind change would be fake success.
- **The real next blocker** is a non-deterministic **post-first-frame module-load /
  event-queue hang** (`sceKernelLoadStartModule` Path B + `sceKernelWaitEqueue`), observable
  once the Stage-7 gated experiment lets Cocoon past the 0x1E storm. This is a different
  milestone (module-load / scheduler / equeue), explicitly out of Stage 9's "unwind only"
  scope.

## Recommendation

Re-scope the next milestone to the **`sceKernelLoadStartModule` (Path B) + `sceKernelWaitEqueue`
post-first-frame hang** (identify the on-demand module that never completes and the equeue it
waits on; determine whether it is a blocking Path-B module-init resume issue under the gated
experiment or a pre-existing startup race). Keep the unwind NIDs (`4fU5yvOkVG4`,
`crb5j7mkk1c`) and the unimplemented `sceImeKeyboardGetInfo` / `sceAgcCb…` as low-priority
compatibility follow-ups — none of them is on the critical path today.
