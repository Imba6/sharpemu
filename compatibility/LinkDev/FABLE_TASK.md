# Fable task — LinkDev priority-0 blocker

Read `/AGENTS.md` first and follow it strictly.

## Target

- Target: `real-tests/LinkDev/LinkDev.elf`
- Runtime log: `real-tests/LinkDev/run.log`
- Compatibility backlog: `compatibility/LinkDev/missing-nids.json`
- Current blocker: `libKernel:sceKernelDlsym`
- NID: `LwG8g3niqwA`
- Kind: dynamic symbol resolution
- Current stall: `__internal_bootstrap_bridge`

The current runtime reaches the guest entry point, then the bootstrap bridge tries to resolve `sceKernelDlsym` through module handles `0x1` and `0x2001`. Resolution fails and execution stalls before normal target progress.

## Task

Implement only the smallest correct support needed to resolve the current `sceKernelDlsym` blocker and allow LinkDev to advance to its next real blocker.

Do not start implementing the rest of the LinkDev backlog in the same change.

## Required investigation

1. Inspect the existing SharpEmu dynamic-loader/module/symbol-resolution implementation before adding new mechanisms.
2. Trace the existing `__internal_bootstrap_bridge` and `sceKernelDlsym` resolution path.
3. Determine whether this should be:
   - a VirtualPS5 HLE export,
   - a fix/reuse of an existing SharpEmu loader capability,
   - or a very small bridge between the two.
4. Confirm ABI, return value and output-pointer semantics from available open references.
5. Do not fake success. The returned guest address must actually resolve the requested symbol correctly enough for the caller to invoke it.

## Regression requirement

Add the smallest deterministic regression that proves dynamic symbol lookup works. Prefer a synthetic guest that obtains a known symbol via `sceKernelDlsym`, validates the result, and invokes it when safe.

## Validation

Run at minimum:

```bash
make host-build
make run TEST=<regression-test>
```

Then run LinkDev and capture a fresh log:

```bash
make real-run ELF=real-tests/LinkDev/LinkDev.elf TRACE=512 2>&1 | tee real-tests/LinkDev/run.log
```

Rescan:

```bash
python3 scripts/vps5_scan.py \
  --elf real-tests/LinkDev/LinkDev.elf \
  --log real-tests/LinkDev/run.log \
  --output compatibility/LinkDev/missing-nids.json
```

Success for this task means:

- `sceKernelDlsym` is no longer the priority-0 blocker;
- LinkDev makes measurable forward progress to a new blocker or executes further;
- regression test passes;
- unrelated HLE APIs are not implemented in this commit.

## Commit

Use:

```text
hle(kernel): implement sceKernelDlsym
```

Include the NID, regression test name, LinkDev result, and next blocker in the commit body.
Push only to `origin/fable`.
