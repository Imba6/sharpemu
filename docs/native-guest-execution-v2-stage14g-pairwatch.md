<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14g result: pair-watch of the 0x86/0x84 queue

Branch `gpt-dlsym`. Evidence: `run_stage14g_pairwatch.log` (multi-RIP watch armed
on all four handshake sites, 2000 records, + sync snapshot). Analysis via the
Stage-14 tooling. **No runtime semantics changed.**

## Roles on the stalled queue (object `0x61FA39E60`)

The four watched RIPs are **generic Baselib functions** shared by many
queues/threads; filtering to the stalled object gives:

| site | RIP | thread |
|---|---|---|
| consumer `wait 0x86` (take item) | `0x800D18129` | `0x0` (external/primary = main "Thread-3"), 1966× |
| producer `signal 0x86` (put) | `0x800D17D53` | `0x1AE02A958C0` (Loading.PreloadManager) |
| producer `wait 0x84` (free slot) | `0x800D17CFB` | `0x1AE02A958C0` |
| consumer `signal 0x84` (free slot) | `0x800D1379F` | `0x0` (main, 10×) **and** `0x1AE02A29F20` (1×) |

So the producer is `Loading.PreloadManager` (handle `0x1AE02A958C0`), the consumer
is the host-parked main thread, and a *third* thread (`0x1AE02A29F20`) posts a
free slot once.

## Terminal state (authoritative — sync snapshot)

Cycle `['Thread-3','Loading.PreloadManager']`: `Thread-3 → 0x86 (item)` parked,
`Loading.PreloadManager → 0x84 (free slot)` parked. Terminal RIP-watch records
(seq 1998-2000) on the queue: item `[+0x118] = -1`, free `[+0x68] = -1`,
gen `[+0x170]=0 [+0x190]=1`, awaited item node `[rbp-0x18] = 0x679722120`.

This is a **mutual bounded-queue deadlock**: the consumer waits for an item, the
producer waits for a free slot, neither yields.

## What the counts do and do NOT show

- The RIP-watch fires on the **slow path** (the fast-path atomic already ran), so
  the consumer's `item[+0x118]` is **always -1** across all 1966 records — this is
  the consumer's own reservation, **not** proof the producer never produced.
  Successful fast-path takes leave no record.
- The free count `[+0x68]` fluctuates 0→8→…→-1 across the run — normal operation
  (the consumer took ~8 items via the fast path, posting free slots), ending
  blocked. **No invariant violation is provable** from these slow-path snapshots;
  the earlier "sum drift" idea is withdrawn (the `0xffff` CAS at `[+0x68]` shows
  this is not a symmetric items+free=N ring, and the item side is always the
  reserved -1).
- OS-level `0x86` signal==wake (Stage-14b) — no dropped kernel signal.

## Answers

1. **Same logical item?** Producer and consumer operate on the **same queue
   object** `0x61FA39E60`; items flow through it. Node-level identity of a given
   item across producer/consumer is **not** captured (the producer's item
   register was not read) — unconfirmed.
2. **Final successful round item/context:** not cleanly isolable — successful
   takes are fast-path and unrecorded; the free count shows ≥8 items flowed.
3. **Failed round item/context:** awaited item node `0x679722120`, gen `(0x170=0,
   0x190=1)`; consumer parked on `0x86`.
4. **Branch preventing `0x800D1379F` (free post) after the last take:** **not
   determinable from this capture** — it is in the consumer's higher loop and is
   gated by the item-node state, which was not read (the node is only on the stack
   at `[rbp-0x18]`, not in a GPR at the import boundary).
5. **Controlling register/memory:** the item-node fields at `*(rbp-0x18)` — not
   yet sampled.
6-9. **Values / writer / timing:** unknown pending the node-field capture.
10. **Logical request:** **not established** — no evidence yet ties node
    `0x679722120` to a concrete asset/file/scene/PSN operation (do not name
    without evidence; a third thread `0x1AE02A29F20` participating hints at an
    async pipeline, but that is not proof).
11. **Earliest upstream HLE:** unknown pending the node's type/payload.
12. **Generic emulator defect proven? NO.** Snapshot shows a clean mutual
    bounded-queue deadlock; OS signals balanced; native execution preserves
    `lock` atomicity; the count snapshots do not prove an invariant violation.

## Verdict / STOP

Per the Stage-14g STOP conditions — the count "mismatch" is a higher-level
producer/consumer wedge (guest logic), several sources remain plausible, and **no
generic emulator-visible semantic defect is proven** — **STOP: no semaphore/
scheduler/HLE change, no synthetic regression** (its precondition is unmet).

## Tooling added

The guest-RIP watch memory operands now support **one pointer indirection**
(`[reg+disp]+disp2:size`), so the next capture can chase `[rbp-0x18]` to the item
node's fields. Gated, read-only, +3 tests (21 total).

## Next blocker (next capture)

Read node `0x679722120`'s layout and trace its creation/insertion + the consumer
branch:

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH="0x800D18129,0x800D1379F,0x800D17D53"
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH_COUNT="4000"
# item node fields via one indirection off the stack slot holding the node:
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH_MEMORY="[rbp-0x18]+0x0:8,[rbp-0x18]+0x8:8,[rbp-0x18]+0x10:8,[rbp-0x18]+0x18:8,[rbp-0x18]+0x20:8,[rbp-0x18]+0x28:4,[rbp-0x18]+0x2C:4,r13+0x68:4,r13+0x118:4"
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1
$env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage14h_nodewatch.log
```
Then: identify the node's type/state/next/payload fields; find the field whose
value diverges on the terminal (failed) item vs the prior successful items; that
field is the consumer's branch condition. If it is an asset/file/job descriptor,
trace the immediately-preceding HLE call that produced its value — the **first
incorrect HLE-visible result** is the next blocker. Only if that proves a generic
emulator-visible defect: build a failing synthetic regression, then the smallest
generic fix.

Caveat: the node is reliably at `[rbp-0x18]` only in the terminal acquire frame
(`rbp=0x7FFFF01FACB0`); other frames put a stack/return address there, so filter
node reads to records whose `ptr` is a `0x6xxxxxxxx` heap value.

## Confidence ledger (Stage 14g)

| Claim | Level |
|---|---|
| Two-semaphore Baselib queue; roles/threads as tabled | **PROVEN** |
| Terminal mutual deadlock (consumer↔producer) on obj 0x61FA39E60 | **PROVEN** (snapshot) |
| Awaited item node = 0x679722120 | **PROVEN** |
| Count snapshots prove an invariant violation | **DISPROVEN** (withdrawn) |
| Branch/condition that skips the free post | **UNKNOWN** — needs node-field capture |
| Generic emulator defect | **NOT PROVEN** → STOP |
