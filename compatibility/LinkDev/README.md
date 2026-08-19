# LinkDev compatibility status

**Target:** `real-tests/LinkDev/LinkDev.elf` (Gen4/PS4-ABI DYN image)
**Status:** Reached the compatibility boundary for this target. Execution
progresses cleanly through the payload CRT bootstrap and then stops at a
**ps5-payload-sdk payload/jailbreak CRT dependency**, not a normal PS5
userspace HLE gap. `SDL_main` is intentionally not reached.

## What LinkDev successfully exercises

Driven by the loader/HLE bootstrap milestones on this branch, LinkDev runs its
entire payload CRT init prologue against the VirtualPS5 runtime:

- **`sceKernelDlsym` bootstrap self-resolution** — the injected
  `payload_args->sys_dynlib_dlsym` bridge resolves `sceKernelDlsym`, and the CRT
  observes that the resolved address equals the bridge callback.
- **Dynamic HLE thunks** — exports the guest never statically imported are
  materialized as guest-callable addresses (`getpid`).
- **Raw syscall gateway + libkernel veneer** — `__crt_syscall_init` resolves
  `getpid`, then takes `getpid + 0x0A` as the shared raw `syscall` entry
  (`ptr_syscall`). `__crt_syscall` dispatches through it.
- **`__sysctl` (syscall 202)** — `__kernel_init` queries `{CTL_KERN, 46}`
  (PS5 firmware version); with a configured/derived firmware profile this
  returns the presented version, otherwise ENOENT (see the firmware-profile
  milestone).
- **`getpid` (syscall 20)** — issued through the raw gateway.

All current synthetic regressions covering the above remain green
(`hle-kernel-dlsym`, `hle-raw-syscall`, `hle-sysctl`, and the managed
`KernelFirmwareProfileTests`).

## Final control-flow boundary

`_start` (payload CRT, `0x1de400`) runs its init chain:

```
_start
  -> __crt_syscall_init   (0x1de760)  OK  (sceKernelDlsym + getpid+0x0A gateway)
  -> __kernel_init        (0x1e0530)  <-- payload kernel-exploit setup
  -> __klog_init          (0x1deb70)  <-- returns -1 here
  -> (never reached) resolve __isthreaded, __patch_init, __rtld_init,
     __rtld_lib_open/init, main -> SDL_main
```

Observed: `__kernel_init` reaches and passes the firmware `sysctl`, then the
init result written to `payload_args->payloadout` is `0xFFFFFFFF` (-1), so
`_start` takes its failure exit (`0x1de4d6 jne 0x1de68c`), resolves
`sceKernelDlsym` one last time, and returns to the host with `rax = 0`.
Execution finishes near-instantly; no SDL/libc/application imports or syscalls
occur.

The `-1` originates in **`__klog_init`** (`0x1deb70`). It resolves, via
`kernel_dynlib_dlsym(-1, handle, symbol)`:

| symbol      | handle          |
|-------------|-----------------|
| `snprintf`  | `0x2`           |
| `strerror`  | `0x2`           |
| `vsnprintf` | `0x2`           |
| `__error`   | `0x1` / `0x2001`|

If any lookup fails, `__klog_init` returns `-1` (`ebx` stays `0xFFFFFFFF`).

## Why SDL_main is not reached — payload-only, not userspace

`KERNEL_DLSYM` here is `kernel_dynlib_dlsym(-1, handle, symbol)` — the
ps5-payload-sdk kernel-assisted resolver built on `SYS_dynlib_get_obj_member`
and the kernel R/W primitive, **not** the normal userspace `sceKernelDlsym`.
It, and the whole CRT beyond this point, depend on payload/jailbreak state that
the WebKit+kernel exploit chain establishes before jumping to the payload:

- `payload_args->rwpair[0..1]`  — kernel-R/W socket pair
- `payload_args->rwpipe[0..1]`  — kernel-R/W pipe pair
- `payload_args->kpipe_addr`    — kernel pipe address
- `payload_args->kdata_base_addr` — leaked kernel `.data` base

`__kernel_init` (which runs immediately before `__klog_init`) consumes exactly
these fields and, keyed on the detected firmware version, computes
firmware-specific **kernel offsets** relative to `kdata_base_addr`
(e.g. `KERNEL_ADDRESS_TEXT_BASE = kdata_base - 0xca0000`,
`KERNEL_ADDRESS_ALLPROC = kdata_base + 0x2755d50`) for arbitrary kernel
read/write via `kernel_copyin` / `kernel_copyout`.

This is jailbreak/payload plumbing, not PS5 userspace application ABI. LinkDev
is a ps5-payload-sdk **payload**, and its CRT expects to run inside a hijacked,
kernel-exploited process — not as an ordinary PS5 title.

## Out of scope (intentional)

Per the project architecture rules, VirtualPS5 must **not** emulate
jailbreak/payload kernel-exploit plumbing merely to satisfy the ps5-payload-sdk
payload CRT. The following are therefore deliberately **not** implemented and
are not planned for this target:

- fabricated `rwpair` / `rwpipe` / `kpipe_addr` / `kdata_base_addr` state;
- payload `kernel_copyin` / `kernel_copyout` kernel R/W;
- invented kernel addresses or firmware-specific kernel offset tables;
- changes to the VirtualPS5 CRT/runtime to mimic the ps5-payload-sdk CRT;
- special cases whose only purpose is to reach LinkDev's `SDL_main`.

Reaching LinkDev's application entry would require emulating that payload/
jailbreak CRT environment, which is outside this compatibility target. The
useful, portable outcome of this target — the dlsym bootstrap, dynamic HLE
thunks, the raw syscall gateway/veneer, generic `sysctl`, and the firmware
profile — is already implemented and regression-covered.

## Reproduce

```
make real-run ELF=real-tests/LinkDev/LinkDev.elf TRACE=512
# optional: present a firmware version so __kernel_init's sysctl path resolves
SHARPEMU_FW_VERSION=0x07610000 make real-run ELF=real-tests/LinkDev/LinkDev.elf TRACE=512
```

Either way LinkDev stops at the `__klog_init` boundary described above; the
firmware value only changes whether `{CTL_KERN,46}` returns a version or ENOENT,
not whether the payload CRT can proceed.
