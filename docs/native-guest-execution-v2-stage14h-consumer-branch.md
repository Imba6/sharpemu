<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14h: the consumer branch is deep Unity job-system logic

Branch `gpt-dlsym`. Static analysis of `eboot.bin`
(`file_off(va)=0xc6d0+(va-0x800000000)`). No Stage-14h Windows node-field capture
exists, so Phases 1/2/5/7/8 (runtime) could not be run here; this stage is the
static consumer-branch decode (Phase 3) plus the resulting STOP assessment.
**No runtime semantics changed; no new tooling added.**

## Phase 3 — static consumer decode

The consumer that takes item `0x86` and posts free-slot `0x84` is a large Unity
**job-system dispatcher** at `~0x8007EE000`. It handles **one item per call**;
its caller loops. Structure (per call):

```
0x8007EEB84  obj = call 0x800D13420()          ; resolve the bounded-queue object
0x8007EEB8C  call 0x800D17F60                   ; ACQUIRE (wait item 0x86)  <-- blocks here at terminal
             ... process the taken item ...
0x8007EEB0A  eax = call 0x8007E3590(global)     ; a job/state query (own frame)
0x8007EEB0F  cmp eax, 2 ; jne 0x8007EECE6        ; STATE BRANCH
             ... FNV-hash lookups into a job/handle registry (0x8007EE656.., 0x8007EE819..) ...
             ... descriptor/string construction; call 0x8007F6BD0 (dispatch) ...
0x8007EE930  jmp 0x8007EE617                    ; -> cleanup path
0x8007EE617  obj = call 0x800D13420()
0x8007EE622  call 0x800D136A0                   ; ACK (release free-slot 0x84)  <-- the "post"
0x8007EE627  [stack-canary]                     ; then epilogue + RET
```

The ack helper `0x800D136A0` (which contains `signal 0x84 @0x800D1379F`) has **9
call sites** — it is a generic Baselib release used across the codebase; the
consumer reaches it only on its normal completion/return path.

### What this means for the "skipped ack"

The consumer does **not** contain a simple "if node.field then skip ack" test.
The observed accounting (items taken 13, free-slot posts 12) is explained by:
**item 13 was taken (ACQUIRE returned), but its *processing* took a branch that
never reached the ack site `0x8007EE622`**, and the loop then re-entered ACQUIRE
(`0x8007EEB8C`) for the next item and blocked (the producer, lacking a free slot,
never produces it). The controlling branch is the job/state logic during
processing — e.g. the `call 0x8007E3590; cmp eax,2` state query and the
job-registry hash lookups — **not** a single decodable node field, and **not** a
semaphore/scheduler operation.

`0x8007E3590`, `0x8007F6BD0`, `0x800D13420` are each their own substantial guest
functions: this is Unity's job/preload dispatcher, several frames deep.

## Answers (report items)

1-3. **Terminal / preceding node fields, first differing field:** not obtained —
   no Stage-14h node-field capture exists (Windows-only); could not be produced
   here.
4. **Consumer branch instruction:** the processing-phase state branch
   `0x8007EEB0F: cmp eax,2 ; jne 0x8007EECE6` (eax from `0x8007E3590`), plus the
   job-registry hash lookups — not a single node-field compare.
5. **Branch condition:** a job/state value returned by guest logic
   (`0x8007E3590`) and registry state, not an emulator-visible field.
6. **Free-slot post path:** `…→0x8007EE617→ACK(0x800D136A0)→0x8007EE622→RET`.
7. **Bypass path:** there is no explicit "skip ack" branch; item 13's processing
   simply does not reach `0x8007EE622` before the loop re-enters ACQUIRE and
   blocks.
8-11. **Writer / thread / timing / lifecycle divergence:** not reached — the
   deciding state is produced by nested guest job-system code, not a captured
   emulator-visible write.
12. **Logical request semantics:** **not proven** (do not name).
13-14. **Upstream HLE / mismatch:** none identified; the branch is gated by guest
   job/registry state, not a direct HLE result.
15. **Generic emulator defect proven? NO.**
16-17. **No regression, no fix.**
18. Tests: unchanged (RIP-watch parser suite remains at 21).

## STOP — conditions met

Per the Stage-14h STOP conditions, several now hold:
- **No single branch-controlling node field** — the ack is gated by multi-frame
  Unity job/state logic (hash-registry + `0x8007E3590` state query).
- **Writer is guest logic with no emulator-visible input** identified.
- **Only title-specific behavior would "fix" the branch** — there is no generic
  emulator-visible defect to correct.

Therefore: **STOP. No behavioural change.**

## Strategic assessment (why deeper guest chasing is the wrong direction)

Across Stages 14a–14h every candidate emulator defect was cleared:
semaphore primitive (14f), lost wake / stale read (14f, ruled out), host-park /
0x1E / GC suspend (13/14, sound), object corruption (14f), count invariant
(14g, withdrawn), and now the consumer branch (14h, guest job-system logic).
The wedge is a **cross-substrate bounded-queue/job deadlock**: the producer
(`Loading.PreloadManager`) and job workers run on **native workers** while the
consumer is the **host-parked primary/main thread** — an asymmetric relative
scheduling that real PS5 hardware does not impose. This is most consistent with a
**scheduling/timing-fidelity** root (matching: V2-OFF never starts the handshake;
V2-ON runs, then wedges nondeterministically), **not** a primitive/HLE
correctness bug.

Continuing to decode Unity's job dispatcher will keep bottoming out in guest
logic. The only remaining *generic* lead is scheduling fidelity, which the
milestone rules (rightly) forbid changing without proof — and proving it needs a
controlled scheduling experiment, not more node decoding.

## Next blocker / recommendation

- Mechanically, the node-field capture (Stage-14g command, one-level indirection)
  can still be run, but on this evidence it will characterise **guest job/registry
  state**, not an emulator defect.
- The higher-value next step is a **controlled scheduling-fidelity experiment**
  (e.g. compare consumer-on-native-worker vs host-parked, or producer inline vs
  worker) to test whether the wedge is scheduling-induced — done as a *gated
  experiment* that does not change default semantics, with the wait-for snapshot
  as the pass/fail oracle. That is a design decision for the maintainer, not an
  autonomous fix.

## Confidence ledger (Stage 14h)

| Claim | Level |
|---|---|
| Consumer is a multi-frame Unity job dispatcher; ack at `0x8007EE622`, acquire at `0x8007EEB8C` | **PROVEN** (disasm) |
| The "skipped ack" is item-13 processing not reaching the ack, then re-blocking in acquire | **PROVEN** (structure) |
| A single node field gates the ack | **DISPROVEN** — it is multi-frame job/state logic |
| Generic emulator-visible defect | **NOT PROVEN** → STOP |
| Root is scheduling/timing fidelity (cross-substrate) | **LIKELY** (consistent across 14a–14h; unproven) |
