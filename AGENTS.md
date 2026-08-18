# VirtualPS5 Agent Instructions

This fork contains the VirtualPS5 compatibility runtime built on top of SharpEmu.
The goal is to implement PS5 userspace/system APIs incrementally, driven by real
application imports and runtime blockers.

## Branch safety

- Work only on the branch explicitly assigned for the current task.
- Approved agent/work branches include `fable`, `codex`, `gpt`, and task-specific branches such as `gpt-dlsym`.
- Never switch to or push directly to `virtual-os`, `main`, or `upstream`.
- If the current branch is not clearly an agent/work branch, stop and ask before modifying files.
- Never rewrite history or force-push unless the human explicitly requests it.
- Keep each commit limited to one NID or one tightly related API group.

## Architecture boundaries

- VirtualPS5 owns PS5-visible userspace/runtime behavior.
- Prefer implementing PS5 APIs under `src/VirtualPS5.HLE`.
- Reuse existing SharpEmu host abstractions for CPU, memory, input, audio, video,
  filesystem, synchronization, and other host facilities when appropriate.
- Only modify SharpEmu internals when a missing lower-level capability cannot be
  expressed cleanly from VirtualPS5.HLE. Explain that need in the commit message.
- Keep the VirtualPS5 CRT/runtime in `guest-tests/runtime`.
- NEVER use the ps5-payload-sdk jailbreak CRT/runtime as the VirtualPS5
  application CRT.
- ps5-payload-sdk may be used as a cross-toolchain and as an API/signature,
  structure, constant, and behavior reference.

## Compatibility backlog

The compatibility scanner is:

```bash
python3 scripts/vps5_scan.py \
  --elf <target-elf> \
  --log <real-run-log>
```

It writes `missing-nids.json` next to the target ELF by default.

When asked to implement compatibility work:

1. Read `missing-nids.json`.
2. Always service entries in `blockers` first.
3. Otherwise choose the lowest-priority-number unresolved entry.
4. Prefer a single function. A small same-library group is acceptable only when
   the functions share one state model and testing them separately would be
   artificial (for example create/wait/delete for one event primitive).
5. Never attempt to implement the entire backlog in one change.

## NID implementation workflow

For every NID/API group:

1. Identify the canonical symbol name and library.
2. Confirm the signature, argument ABI, structures, constants, and return/error
   semantics from available open references.
3. Search existing SharpEmu code before adding a new host mechanism.
4. Design the smallest correct VirtualPS5-facing implementation.
5. Implement the HLE export with `SysAbiExport` and the correct library/target.
6. If guest-side declarations are needed for a synthetic test, add only the
   minimal declarations required by that test. Do not replace our CRT/runtime.
7. Add a minimal regression guest under `guest-tests/src/<test-name>`.
8. Build the host.
9. Run the synthetic regression test.
10. Run the real target that requested the API.
11. Re-run `scripts/vps5_scan.py` and verify the blocker/import changed as
    expected.
12. Commit only after the regression test and target check have completed.

## Correctness rules

- Never fake an implemented API by returning success without implementing
  behavior that callers rely on.
- A no-op success is acceptable only when the real API is semantically a safe
  no-op for our environment, and that conclusion is supported by references or
  caller behavior. Document why.
- Preserve guest-visible error behavior where known.
- Validate guest pointers before reading/writing guest memory.
- Avoid fixed host addresses or assumptions tied to one test binary.
- Avoid per-frame/per-poll console spam. Diagnostic logging must be gated,
  change-driven, or intentionally temporary.
- Do not add proprietary Sony binaries, firmware, PKGs, game assets, keys, or
  other copyrighted runtime material to the repository.

## Stop and report instead of guessing

Stop the implementation and report the architectural blocker when any of these
apply:

- The function requires semantics that are not sufficiently understood.
- The change would require a new memory-management model or unsafe aliasing
  behavior.
- The change affects AGC/GPU synchronization, command processing, shader
  translation, or render-target ownership beyond a small isolated fix.
- Save-data, NP/account/authentication, DRM, entitlement, or security semantics
  would need to be invented.
- Correct behavior requires broad changes across unrelated SharpEmu systems.
- Available references disagree materially about ABI or structure layout.

A blocker report should state:

- symbol/NID/library;
- what the target is doing when it calls it;
- what is known about the real API semantics;
- what SharpEmu capabilities already exist;
- the smallest proposed architecture;
- what decision or information is required from the human.

## Tests

At minimum for a normal HLE implementation run:

```bash
make host-build
make run TEST=<new-or-existing-regression-test>
```

Then run the requesting target and rescan it:

```bash
make real-run ELF=<target> TRACE=256 2>&1 | tee <target-dir>/run.log
python3 scripts/vps5_scan.py \
  --elf <target> \
  --log <target-dir>/run.log
```

If the real target does not terminate normally, that alone is not failure. The
important check is that the implemented blocker is resolved and execution makes
measurable forward progress to the next blocker.

## Commit format

Use a focused message such as:

```text
hle(kernel): implement sceKernelDlsym
```

For non-trivial HLE commits, include in the body:

```text
Library: libKernel
NID: <nid if known>
Regression: <guest test>
Target: <real target>
Result: <what changed / next blocker>
```

Do not bundle formatting cleanup, unrelated refactors, or multiple independent
libraries into the same compatibility commit.
