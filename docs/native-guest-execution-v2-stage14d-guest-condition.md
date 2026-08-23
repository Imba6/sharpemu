<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14d: the guest 0x86-acquire loop condition

Branch `gpt-dlsym`. Static analysis of the guest binary
`real-tests/Cocoon/PPSA08766-app0/eboot.bin` (PS5 SELF; ELF at file 0x1a0;
executable segment ph0 mapped from SELF-segment file offset `0xc6d0`, module base
`0x800000000` — so `file_off(va) = 0xc6d0 + (va - 0x800000000)`), plus a new
gated diagnostic. **No runtime semantics changed.**

## Phase 1 — disassembly / CFG at RIP 0x800D18129

`0x800D18129` is the return site of the timed `WaitSema` call inside a
**Baselib `Baselib_SystemSemaphore` acquire** routine (prologue at
`0x800D17F60`, `r13 = rdi = the semaphore object`). Decoded operands:

- **`[r13+0x118]`** — the semaphore's **user-space atomic count** (signed i32).
- **`[r13+0x120]`** — the **OS semaphore handle = 0x86**.
- `[r13+0x34]` — a state/type field (set to 4).
- `[r13+0x170]`, `[r13+0x190]` — additional state consulted on the acquired path.

Core loop (validated: a `call` ends exactly at `0x800D18129`):

```
;--- fast path ---
800D180FC  lock xadd [r13+0x118], eax     ; eax=-1: atomically count--, eax=OLD count
800D18105  test eax, eax
800D18107  jg   0x800D17FE0               ; OLD>0 -> token acquired (exit loop)
;--- slow path: block on the OS semaphore ---
800D1810D  mov  rdi, [r13+0x120]          ; handle = 0x86
800D18114  mov  esi, 1                    ; need = 1
800D1811D  mov  dword [rbp-0x34], 0x3E8   ; timeout = 1000 us
800D18124  call sceKernelWaitSema         ; -> ret 0x800D18129
800D18129  test eax, eax
800D1812E  je   0x800D17FE0               ; WaitSema OK (signalled) -> acquired (exit loop)
800D18134  mov  eax, [r13+0x118]          ; timed out: reload count
800D1813D  jns  0x800D18152               ;   count>=0 -> go re-wait
800D1813F  (cmpxchg [r13+0x118], count+1) ;   count<0  -> undo our decrement, then give up
800D1814D  jmp  0x800D17FD3
800D18152  call 0x801789150              ; (helper) then
800D18163  call 0x8017896E0             ; (re-wait on 0x86)
800D18168  jmp  0x800D18129              ; loop
```

CFG:
```
enter acquire(r13)
  └─ lock xadd count-- ──> OLD>0 ─────────────► ACQUIRED (0x800D17FE0) ─► return to caller
        └─ OLD<=0 ─► WaitSema(0x86,1000us)
                       ├─ OK ──────────────────► ACQUIRED ─► return to caller
                       └─ TIMED_OUT ─► count>=0 ─► re-wait ──┐ (loops)
                                     └ count<0  ─► give up ──► return "not acquired"
```

**The loop-exit is "acquire a token"** (OLD count>0, or WaitSema returns OK).
The consumer got OK 13× (one per production) — it is not stuck *inside* one
acquire; it **returns to its caller after each token and the caller calls
acquire again**. So the true re-acquire-vs-ack decision is in the **caller**
(Unity job/loading logic) above this Baselib routine — it decides *how many*
tokens to drain before proceeding to `signal 0x84 @ 0x800D1379F`. That count/flag
lives in the caller's callee-saved registers (rbx/r12/r14/r15, preserved across
the acquire call) and/or its stack frame.

**Round 13 in these terms:** PM produced 13 tokens; the consumer's caller
drained all 13 and then called acquire a **14th** time (which blocks forever,
because PM is now parked on `wait 0x84` and produces no more). The caller
expected one more item than was produced — an off-by-one in *guest* state.

This is guest application logic; the exact expected-count value cannot be read
statically. It is captured at runtime by the diagnostic below.

## Phase 2–3 — generic guest-RIP watch (implemented, gated, read-only)

```
SHARPEMU_DIAG_GUEST_RIP_WATCH=0x800D18129
SHARPEMU_DIAG_GUEST_RIP_WATCH_COUNT=400
SHARPEMU_DIAG_GUEST_RIP_WATCH_MEMORY=r13+0x118:4,r13+0x120:8,r13+0x34:4,r13+0x170:8,r13+0x190:8
```

The direct-execution backend has no per-instruction hook, but it exposes the
**full guest register file + memory at every import boundary**, and
`0x800D18129` *is* an import-return site. `MaybeEmitGuestRipWatch` fires there,
logging every GPR (rdi..r15, rbp, rsp — which includes the caller's preserved
rbx/r12/r14/r15) and the requested operands as `ea=…=value`. Inert when unset;
never throws into guest execution; no title-specific values. Emits:

```
[LOADER][DIAG] guest_rip_watch seq=N rip=0x800D18129 nid=Zxa0VhQVTsk thread=0x..
   rdi=.. rsi=.. ... r13=0x<obj> r14=.. r15=.. [r13+0x118:4]ea=0x..=0x<count> [r13+0x170:8]ea=..=0x..
```

Because the same facility keys on *any* import-return RIP, pointing it at the
producer's `SignalSema(0x86)` return site (PM's release, near `0x800D17D53`)
captures the producer side symmetrically — no write-watch needed to see both
ends of `[r13+0x118]`.

Covered by 14 unit tests (`GuestRipWatchTests`) over the spec parser
(RIP literal, `reg±disp:size`, defaults, bad-size clamp, non-GPR skip).

## Phase 4 — writer identification (design; not implemented)

`[r13+0x118]` is written by the acquire (consumer `lock xadd -1`) and by the
release (producer, at the `SignalSema(0x86)` path). Both sites are import-return
boundaries, so **the Phase-2 RIP-watch on the producer release RIP already
reveals the producer's writes/values** — the lighter tool. The existing
`GuestWriteWatch` (`src/SharpEmu.HLE/GuestWriteWatch.cs`) is a bulk-HLE-write
*value* checker and does **not** capture a writer RIP; a true per-write trap
would need page-protection + fault-RIP capture (a heavyweight tracer the
milestone says to avoid). Recommendation: use dual RIP-watch (acquire + release)
first; only add a page-fault write-watch if the caller writes the exit flag
outside any import path.

## Phases 5–9 — pending Windows capture (prepared)

Not runnable on this Linux host. Capture round 12 vs round 13 with:

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1
$env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
$env:SHARPEMU_LOG_SEMA=1
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH=0x800D18129
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH_COUNT=400
$env:SHARPEMU_DIAG_GUEST_RIP_WATCH_MEMORY="r13+0x118:4,r13+0x120:8,r13+0x34:4,r13+0x170:8,r13+0x190:8"
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage14d_ripwatch.log
```

Analyse: correlate `guest_rip_watch` lines around the last successful 0x86
consume (round 12) and the first failed one (round 13). Compare across rounds:
`r13` (same object?), `[r13+0x118]` (count seen), the caller's preserved
`rbx/r12/r14/r15` (the drain-count/limit), and `[r13+0x170]/[r13+0x190]`. The
**first field that differs** between the good and failed acquire is the earlier
guest-visible condition.

Then classify (Phase 6): **A** writer never runs / produces no 14th token
[matches current evidence: PM parks on 0x84 after 13 productions]; **B** writer
writes a different value; **C** stale read (only if the RIP-watch shows the
consumer reading a value the producer already updated — *do not* claim without
this); **D** a different operand/register drives the branch; **E** the object
pointer differs.

## Verdict and STOP

- Phase 1 decoded the acquire primitive and its operands precisely; the
  higher-level exit condition is **guest (Unity/Baselib caller) state**, not an
  emulator primitive.
- Consistent with Stage 14b/14c, **no emulator execution/routing/memory defect is
  proven**: the token is delivered/consumed, the acquire routine behaves exactly
  as its own code specifies, and the divergence is a caller-level count/flag.
- Per the milestone STOP conditions ("the watched state is guest logic and the
  writer simply does not execute", "no generic emulator semantics violation is
  proven"): **STOP — no behaviour change, no synthetic regression** (its
  precondition, proven stale visibility (case C), is not established). The
  worker→signal→wait→stale-read regression would be the *wrong* test if, as the
  evidence indicates, the producer simply never emits a 14th token (case A).
- The RIP-watch diagnostic is the tool to convert "guest branch" into the exact
  differing field on the next Windows capture, and to positively confirm or rule
  out case C (stale visibility) before any behavioural work.

## Confidence ledger (Stage 14d)

| Claim | Level |
|---|---|
| RIP 0x800D18129 is inside a Baselib 0x86 acquire; operands [r13+0x118]/[r13+0x120] identified | **PROVEN** (disasm, call-boundary validated) |
| Loop-exit = acquire a token (count>0 or WaitSema OK) | **PROVEN** |
| Re-acquire-vs-ack decision is in the caller (guest app logic) | **PROVEN** |
| Round 13 = caller expects a 14th token never produced | **LIKELY** (matches all timing evidence; exact caller field pending RIP-watch) |
| Stale cross-substrate visibility (case C) | **NOT ESTABLISHED** (needs RIP-watch values) |
| Any generic emulator defect | **NOT PROVEN** → STOP |
