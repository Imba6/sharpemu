<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14f: the 0x86 consumer waits for an item never produced

Branch `gpt-dlsym`. Evidence: `run_stage14f_ripwatch.log` (armed guest-RIP watch
at `0x800D18129`, 512 records; V2-ON; sync snapshot). Analysis via the Stage-14d
RIP-watch tooling. **No runtime semantics changed.**

## Watch summary (512 records at the 0x86 acquire's WaitSema)

- Self-verification present: `guest_rip_watch armed rip=0x800D18129 limit=512`.
- `r13 = 0x61F8390E0` **constant** — the same `Baselib_SystemSemaphore` object throughout (rules out object corruption / E).
- `[r13+0x120] = 0x86` (OS handle), `[r13+0x34] = 4` (type) — constant.
- `[r13+0x118] = 0xFFFFFFFF (-1)` on **every** record — expected: the watch fires only when the user-space fast-path found no token, so the count is always the post-decrement -1 here.
- `[r13+0x170]/[r13+0x190]` toggle `(1,0)↔(0,1)` per processed item — a two-state generation flag.

## Last successful vs first extra acquire (the transition)

The caller's item pointer (saved caller `r13` at `[rbp-0x18]`) advances through a
list of work items, then freezes:

```
item progression (caller r13): … 610471F70 → 620E2AE00 → 60F8CAB80 → 67A4ADF70 → 67A644E90
terminal frozen run: seq 113 → 512 (400 identical records)
   rbp=0x7FFFF01FACB0  caller_item(r13)=0x67A644E90  [r13+0x170]=0  [r13+0x190]=1
```

- **Last successful acquire:** item node **`0x67A4ADF70`** (processed; consumer advanced).
- **First extra acquire (permanent poll):** item node **`0x67A644E90`**, seq 113 — repeats unchanged 400×.

So the consumer's drain loop reached node `0x67A644E90` and is waiting for the
producer to hand off (`signal 0x86`) that item. It never comes.

## Semaphore / producer accounting

The 14f sync snapshot shows the same cycle: `Loading.PreloadManager -> 0x84`
(parked, not producing) and `Thread-3 -> 0x86` (the host-parked main thread,
waiting). Combined with Stage-14b (`SHARPEMU_LOG_SEMA`: 0x86 **signal==wake==13**,
every signal delivered): the producer called `SignalSema(0x86)` **exactly 13
times** then parked on `wait 0x84`. There is **no 14th production**, and `[r13+0x118]`
correctly reflects no token — no dropped signal, no stale read.

## Why the consumer expects a 14th item

The producer/consumer form a per-item rendezvous: PM `signal 0x86 (produce) →
wait 0x84 (await ack)`; consumer `wait 0x86 (take) → signal 0x84 (ack)`. The
counts are `0x86: 13 produced/13 taken` but `0x84: 12 acked`. So after taking
item 13, **the consumer did not ack (signal 0x84); it advanced to wait for item
14 (`0x67A644E90`)**, while PM is blocked on `wait 0x84` awaiting the ack of item
13 and therefore cannot produce a 14th. Mutual wait.

The consumer's decision to expect a 14th item is driven by its **work-list
containing node `0x67A644E90`** — guest state populated **earlier**, before this
rendezvous. Nothing in the semaphore layer creates that node.

## Classification (Stage-14d A–E)

| Case | Verdict |
|---|---|
| **A** — writer never produces the token | **CONFIRMED**: PM signals 0x86 exactly 13×, never a 14th (it is deadlocked on 0x84). The item `0x67A644E90` the consumer waits for is never produced. |
| B — writer writes a different value | No. |
| C — consumer reads stale semaphore memory | **Ruled out**: `[r13+0x118]` is consistently -1 (correct: no token), object stable, signals==wakes (14b). No stale read. |
| D — different operand/register drives the branch | No — the branch is driven by the consumer's legitimate item-list node. |
| E — object corrupted / different | **Ruled out**: `r13=0x61F8390E0` constant and valid throughout. |

## Generic emulator defect?

**NOT PROVEN.** The semaphore primitive delivered every signal, the object is
stable, and there is no stale read. The off-by-one is **guest-side item
accounting**: the consumer's work-list holds node `0x67A644E90` (a 14th item)
that the producer never produces, and the producer is mutually blocked on `0x84`.
Per the milestone rules — **STOP; no fix, no synthetic regression, no semantics
change** (the precondition, a proven generic emulator violation such as case C,
is not met).

## Next blocker (earlier guest/HLE-visible state)

Per the decision tree, this is guest logic driven by an **earlier** state: what
populated the consumer's work-list with node `0x67A644E90` — i.e. why the
consumer expects 14 items when the producer's pipeline yields 13. Candidates to
investigate on the next capture:
- an earlier HLE return that seeds the item/job count (asset/file enumeration,
  job-queue length, a load-request count),
- or the mutual-wait itself: the consumer enqueues N+1 requests while the
  producer fulfils N and both deadlock on the `0x86/0x84` rendezvous.

Recommended next diagnostic (still no semantics change): a second armed RIP-watch
on the **producer's `SignalSema(0x86)` return site** (near `0x800D17D53`) plus the
**consumer's ack site** context, to capture, per item, the producer's production
count vs the consumer's list length, and to find the earlier HLE call that set
the mismatched count. Then, only if that reveals a generic emulator-visible state
error, produce a failing synthetic regression before any fix.

## Confidence ledger (Stage 14f)

| Claim | Level |
|---|---|
| Same semaphore object throughout (no corruption) | **PROVEN** |
| Consumer permanently waits on item node 0x67A644E90 (last good 0x67A4ADF70) | **PROVEN** |
| Producer signals 0x86 exactly 13×, never a 14th; parked on 0x84 | **PROVEN** (14f snapshot + 14b counts) |
| No dropped signal / no stale semaphore read | **PROVEN** (case C ruled out) |
| Classification = A (writer never produces the awaited item) | **PROVEN** |
| Generic emulator defect | **NOT PROVEN** → STOP |
| Root = earlier guest work-list/count populated with one extra item | **LIKELY** (next blocker) |
