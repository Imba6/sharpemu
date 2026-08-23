<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14g: the 0x86/0x84 pair is a two-semaphore bounded queue

Branch `gpt-dlsym`. Static analysis of `eboot.bin`
(`file_off(va)=0xc6d0+(va-0x800000000)`) plus the Stage-14f capture. **No runtime
semantics changed.** Diagnostic tooling extended only.

## Structural reframe (static, exact)

`0x86` and `0x84` are **not** independent signals — they are the two semaphores of
one **bounded-queue / rendezvous object** (the constant `r13/r14 = 0x61F8390E0`).
Object layout, confirmed from the acquire/ack/producer code:

| offset | meaning |
|---|---|
| `+0x34` | type = 4 |
| `+0x68` | `0x84` "free-slot" user-space atomic count |
| `+0x70` | `0x84` OS handle |
| `+0x118` | `0x86` "item-available" user-space atomic count |
| `+0x120` | `0x86` OS handle |
| `+0x170`,`+0x190` | generation/state (toggle per item) |

Exact import-return sites (validated by disassembly; `WaitSema=0x801788a70`,
`SignalSema=0x801788b00`):

| role | operation | RIP | call |
|---|---|---|---|
| Producer `Loading.PreloadManager` | `wait 0x84` (free slot) | `0x800D17CFB` | `call 0x801788a70`, rdi=`[r14+0x70]` |
| Producer | `signal 0x86` (item ready) | `0x800D17D53` | `call 0x801788b00`, rdi=`[r14+0x120]`, **esi=r13d (count)** |
| Consumer main/`Thread-3` | `wait 0x86` (take item) | `0x800D18129` | `call 0x801788a70`, rdi=`[r13+0x120]` |
| Consumer | `signal 0x84` (free slot) | `0x800D1379F` | `call 0x801788b00`, rdi=`[r13+0x70]`, esi=1 |

So the protocol is the classic bounded queue:
`producer: wait 0x84 → put → signal 0x86` ; `consumer: wait 0x86 → take → signal 0x84`.

## What the Stage-14f + 14b counts now mean

- `0x86` items produced = 13, taken = 13.
- `0x84` free-slots posted by the consumer = **12**.

With a capacity-1 rendezvous, the producer needs a free slot (`0x84`) before each
put. After 13 puts it has consumed 13 free slots but the consumer posted only 12,
so the producer blocks on `wait 0x84` for the 14th, while the consumer — having
taken item 13 — **did not post `0x84`** and instead went back to `wait 0x86` for
item 14. **Mutual wait.** (Matches the Stage-14f terminal freeze on item node
`0x67A644E90`.)

So the precise defect is **not** "an extra consumer node" but: **after taking
item 13, the consumer skipped its `signal 0x84` (free-slot) post and re-waited on
`0x86`.** The producer is then correctly, permanently blocked.

## Verdict (Phase 9 bar)

- The two semaphores behave exactly as their guest code drives them; every signal
  was delivered (14b: `signal==wake`), the object is stable (14f), counts are
  consistent. **No generic emulator defect is proven.**
- The divergence is the consumer's higher-loop branch choosing *re-wait* over
  *post-0x84* after item 13 — guest logic gated by the item-node/queue state set
  earlier. Per the milestone STOP conditions: **STOP — no semaphore/scheduler
  change, no synthetic regression** (its precondition, a proven emulator-visible
  semantic violation, is not met).

## Tooling added (this stage)

`SHARPEMU_DIAG_GUEST_RIP_WATCH` now accepts a **comma-separated set of RIPs**, so
one capture correlates producer + consumer + ack. Gated, read-only, +4 tests.

## Next capture — map items + find the skipped post (Windows)

One run, all four handshake sites, reading both semaphore counts (anchored on the
object in `r13` at consumer sites and `r14` at the producer site) plus the
caller-saved item pointer:

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH="0x800D17D53,0x800D17CFB,0x800D18129,0x800D1379F"
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH_COUNT="2000"
# object counts (free-slot @0x68, item @0x118, gen @0x170/0x190) on both anchors + item ptr
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH_MEMORY="r13+0x68:4,r13+0x118:4,r13+0x170:8,r13+0x190:8,r14+0x68:4,r14+0x118:4,rbp-0x18:8,rbx:8,r14:8,r13:8"
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1
$env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage14h_pairwatch.log
```
Confirm `guest_rip_watch armed rip=[0x800D17D53,0x800D17CFB,0x800D18129,0x800D1379F]`.

Analysis targets (offline, from the interleaved records):
1. **Per-round item mapping**: at `0x800D17D53` (producer put) and `0x800D18129`
   (consumer take), the register file gives the current item pointer — build the
   `round | producer item | consumer item` table (Phase 3).
2. **First count divergence** (Phase 6): watch `[obj+0x68]` (free slots) and
   `[obj+0x118]` (items) at each site; find the first round where the consumer
   takes an item but the subsequent `0x800D1379F` (post 0x84) does **not** occur —
   i.e. the free-slot count fails to increment. That is the exact skipped post.
3. **Item node fields** (Phase 4): once the item register at `0x800D18129`/
   `0x800D1379F` is known from the register file, a follow-up run adds operands
   `<itemreg>+0x0:8,+0x8:8,+0x10:8,...` to read the node's next/type/payload and
   trace back to its insertion RIP and the upstream HLE that created it (Phase 7).

## STOP / next blocker

The count mismatch is, on all evidence so far, **guest logic**: the consumer's
higher loop skips the `0x84` free-slot post after item 13. The next blocker is to
capture *why that branch is taken* (the item-node/queue-state condition at the
ack site) and only then trace it to any upstream HLE result. No emulator defect
is proven; no behavioural change is warranted yet.

## Confidence ledger (Stage 14g)

| Claim | Level |
|---|---|
| 0x86/0x84 are one two-semaphore bounded-queue object (layout above) | **PROVEN** (disasm) |
| Exact producer/consumer/ack RIPs and calls | **PROVEN** (disasm) |
| Defect = consumer skips `signal 0x84` after item 13 (not an extra node) | **PROVEN** (counts: items 13/13, free-slots 12) |
| Root branch condition (item-node state) | **UNKNOWN** — needs the pair-watch capture |
| Generic emulator defect | **NOT PROVEN** → STOP |
