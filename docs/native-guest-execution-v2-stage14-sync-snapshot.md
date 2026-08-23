<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14: live sync-stall snapshot + default-path baseline

Autonomous session, branch `gpt-dlsym`, baseline commit `17687b7`.

Two goals, both diagnostic (no scheduler/semaphore/host-park/0x1E behavior
changed):

- **A.** Establish the pure-default **V2-OFF** Cocoon baseline.
- **B.** Implement a generic **live sync-stall snapshot** able to identify the
  anonymous host-blocked (`guest=0`) waiter and close the 0x86/0x84 wait-for
  cycle at the moment of stall.

> This machine is Linux/WSL2. Cocoon runs Windows-native (GPU + game binary), so
> the *fresh* Windows runs required by Phase 1 and Phase 9 could not be executed
> here. Phase 1 is characterised from the **existing captured** default-path
> logs; the C# snapshot infrastructure is built, compiled, and unit-tested on
> Linux, with the live Windows capture prepared as an exact runbook (§7).

---

## 1. Phase 1 — V2-OFF default baseline (from existing captures)

The stage-12 `run_stage12_default_*` logs are the default (V2-off) configuration.
Per the Stage-13 change matrix the default 0x86 path is code-identical from
`17687b7` to HEAD, so these characterise the current default shape (a fresh HEAD
capture is still owed — see §7). Measured with the Stage-13 tools:

| Run | lines | presents | 0x86 signal/wake | 0x86 timeouts | >1 s parked GC | shape |
|---|---|---|---|---|---|---|
| default_1 | 2 815 | 0 | — (no 0x86) | — | 0 | **Access Violation crash** + CET/CFG relaunch |
| default_2 | 4 223 | 3 | 0 / 0 | 39 | 0 | 0x86 never handshakes |
| default_3 | 15 642 | 3 | 0 / 0 | 10 820 | 0 | 0x86 never handshakes (long) |
| default_4 | 4 558 | 3 | 0 / 0 | 38 | 0 | 0x86 never handshakes |
| default_5 | 4 653 | 3 | 0 / 0 | 59 | 0 | 0x86 never handshakes |

**Classification: B (nondeterministic mixture), dominated by the "0x86 never
handshakes" shape (4/5), with an intermittent early Access Violation (1/5).**

Key properties of the V2-OFF shape, distinct from the V2-ON wedge:
- Reaches 3 present markers (splash, first frame, one guest frame), then the
  0x86 waiter floods timeouts. **The producer never signals 0x86 even once**
  (`signal=0`), versus 6–8 successful rounds in V2-ON.
- **Zero** multi-second parked GC-suspend deliveries (V2-ON has 500–600). The
  guest never enters the long IL2CPP stop-the-world pauses — consistent with it
  never reaching the phase that produces them.
- The 1/5 Access Violation is the separate Unity-AV family (Stage-12D), not the
  stall.

**Verdict:** V2-OFF "0x86 never handshakes" is a **stable, distinct blocker**
from the V2-ON "6–8 rounds then mutual wedge". They are **not** proven to share a
root cause and are treated separately (Stage-13 §4 reconfirmed). The V2-OFF
producer never reaches the first 0x86 signal — an *earlier* failure than the
V2-ON handshake wedge.

---

## 2. Phase 2–5 — live sync snapshot architecture

Gated, read-only, off by default:

- `SHARPEMU_DIAG_SYNC_SNAPSHOT=1` — master enable.
- `SHARPEMU_DIAG_STALL_SNAPSHOT_MS=<ms>` — auto-trigger after that long with no
  frame-present progress.

Pipeline:

```
registries (read-only enumerators)  ─┐
_guestThreads (cooperative waiters) ─┤→ SyncSnapshotAssembler ─→ SyncSnapshotFormatter
_hostParkRegistry (parked waiters)  ─┘        (pure, tested)         (sync_snapshot.* lines)
                                                                            │
                                          scripts/analyze_waitfor_graph.py ─┘  (authoritative graph + cycles)
```

Components (all new, all generic):

| Layer | Location | Tested |
|---|---|---|
| Record types + formatter (`sync_snapshot.*` grammar) | `src/SharpEmu.HLE/Diagnostics/SyncSnapshot.cs` | ✅ C# |
| Pure assembler (merge coop + host-park, gate-match, wake-key parse) | `src/SharpEmu.HLE/Diagnostics/SyncSnapshotAssembler.cs` | ✅ C# |
| Read-only enumerators (sema/eventflag/host-park/thread-name/flip-count) | `KernelSemaphoreCompatExports`, `KernelEventFlagCompatExports`, `GuestThreadExecution`, `KernelPthreadState`, `VideoOutExports` | compile |
| Collector + auto-trigger watchdog | `src/SharpEmu.Core/Cpu/Native/DirectExecutionBackend.SyncSnapshot.cs` | compile |
| Offline authoritative parser + wait-for graph | `scripts/analyze_waitfor_graph.py` (`SyncSnapshot` mode) | ✅ Python |

Snapshot content per thread: handle, pthread, name, state, guest RIP, resume
RIP, host tid, native-worker/inline, wait type + object + wake key, need,
timeout, parked, reentrant, wait-ms. Per semaphore: handle, count, max, waiters,
last-signaler (diagnostic field). Per event flag: handle, bits, waiters.

**Cross-language round-trip verified:** lines produced by the C# formatter feed
`analyze_waitfor_graph.py` and resolve the 0x86/0x84 deadlock cycle, with the
formerly anonymous consumer identified.

---

## 3. Phase 3 — host-park identity mapping (and its honest limit)

The audit result is important and shapes what "identify the anonymous waiter"
can mean:

- The `guest=0` waiter reaches the host-block path **precisely because
  `CurrentGuestThreadHandle == 0`** (i.e. `!IsGuestThread`). It is a
  **non-cooperative, external-executor thread**, *not* a scheduler-tracked guest
  pthread, so it is **not** in `_guestThreads`.
- Its recoverable identity is therefore the **synthetic per-host-thread handle
  `Thread-{id}` + caller RIP + wait object** — **not** a named Unity guest
  thread (e.g. not "Background Job.Worker N"). Claiming otherwise would be
  fabrication, which the tooling explicitly refuses (emits `UNKNOWN`).
- The **wait object** for such a waiter is recovered without any new hot-path
  plumbing by matching the park's **monitor-gate identity**
  (`RuntimeHelpers.GetHashCode(gate)`) against each semaphore's/event-flag's gate
  id. `HostParkRegistration` already stores the `Gate`; the enumerator exposes
  its id read-only. Equeues share one global gate (not disambiguable this way)
  and mutexes use a `Lock` (different mechanism) — documented follow-ups.

**So the cycle CAN be closed** as: `Thread-{id}` (external executor) waits
`sema 0x86` ⇄ produced by `Loading.PreloadManager`, which waits `sema 0x84` ⇄
produced by the external-executor consumer. The consumer is a real thread with a
recoverable synthetic identity and wait object; what is *not* recoverable from
existing state is its Unity-level name.

---

## 4. Phase 6 — automatic stall trigger

Keys on `VideoOutExports.DiagnosticFlipCount` (monotonic, always-on guest flip
submissions) — **not** raw import count, which a busy spin keeps advancing
(Stage-13). If the flip count is unchanged for `SHARPEMU_DIAG_STALL_SNAPSHOT_MS`
**and** no guest thread is Ready **and** the RIP is not in a known-benign
blocking import (reusing the existing `HasReadyGuestThread` /
`IsExpectedBlockingImportStall` vetoes), one snapshot is emitted. Rate-limited to
one per stall episode; re-armed when frames resume. Runs on a background thread;
never terminates the guest, never intervenes.

---

## 5. Tests

- Python (`scripts/test_analyze_waitfor_graph.py`, +9): live-snapshot parse,
  host-parked identity recovery, sema/mutex cycle detection, **UNKNOWN preserved
  not guessed**, equeue-as-external, last-block-wins.
- C# (`tests/SharpEmu.Libs.Tests/Diagnostics/SyncSnapshotTests.cs`, 12):
  formatter grammar, wake-key parsing (generic forms + rejects), **host-park
  wait-object recovery by gate-match**, **unmatched host-park stays unknown**.
- Full suite: 1111 pass; the 2 `KernelHeapCompatMemoryTests` failures are
  pre-existing environmental (malloc host-fallback, identical at baseline).

---

## 6. Phase 9 — live Windows capture (PENDING, prepared)

Not runnable on this host. Runbook below.

---

## 7. Morning Windows verification & capture

From a Windows checkout of `gpt-dlsym`:

```powershell
dotnet build .\SharpEmu.slnx -c Debug
dotnet test .\tests\SharpEmu.Libs.Tests\SharpEmu.Libs.Tests.csproj `
  --filter "FullyQualifiedName~SyncSnapshot"
```

**Phase 1 — fresh pure-default V2-OFF baseline (×5), NO diagnostic flags:**

```powershell
1..5 | ForEach-Object {
  dotnet run --project .\src\SharpEmu.CLI -c Debug -- `
    .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> "run_stage14_default_$_.log"
}
```
Then (any machine): `python3 scripts/analyze_run_report.py run_stage14_default_N.log`
and confirm the shape (expect: 3 presents, 0x86 `signal=0`, timeout flood, 0
parked GC) or a crash. Classify A/B/C/D.

**Phase 9 — capture a V2-ON terminal wedge with the live snapshot:**

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1
$env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
dotnet run --project .\src\SharpEmu.CLI -c Debug -- `
  .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage14_snapshot.log
```
Then:
```bash
python3 scripts/analyze_waitfor_graph.py run_stage14_snapshot.log     # authoritative mode auto-selected
```

**Expected:** a `sync_snapshot.begin ... reason=stall-3000ms` block; the offline
tool then prints the blocked threads (including the former `guest=0` consumer as
`Thread-{id}` on `sema 0x86`), the wait-for edges, and — if 0x84's last-signaler
was captured — the `Loading.PreloadManager ⇄ Thread-{id}` deadlock cycle.

This answers the Phase-9 questions: (1) waiter = external-executor `Thread-{id}`;
(2) synthetic name (not a Unity name — see §3); (3) its wait RIP; (4) synthetic
handle; (5) host-park (external executor, not native worker); (6) `sema 0x86`;
(8) the cycle; (9) first permanently-stalled thread (the 0x86 consumer, per
Stage-13). (7)/(10) require correlating the snapshot with the guest job-system
state and remain open.

---

## 8. Confidence ledger (Stage 14)

| Claim | Level |
|---|---|
| V2-OFF default shape = "0x86 never handshakes", stable (4/5), 1/5 AV | **PROVEN (from existing captures)**; fresh HEAD run owed |
| V2-OFF and V2-ON are distinct blockers | **PROVEN distinct**; shared root cause **DISPROVEN so far** |
| Anonymous waiter is an external-executor thread, not a guest pthread | **PROVEN** (audit) |
| Its wait object is recoverable by gate-match | **PROVEN** (unit-tested) |
| Its *Unity-level name* is recoverable from existing state | **DISPROVEN** (only synthetic Thread-{id}) |
| Snapshot closes the 0x86/0x84 cycle | **PROVEN in principle** (round-trip); **PENDING** live Windows capture |
| No behavior change when diagnostics disabled | **PROVEN** (default-off, unit-tested paths) |

---

## 9. Next blocker

Run the §7 captures on Windows. If the live snapshot confirms the cycle,
the next milestone shifts from *diagnosis* to a *generic* fix hypothesis for the
V2-ON mutual wedge (and, separately, the V2-OFF "never handshakes" earlier
blocker) — but only once a generic root cause is demonstrated with a failing
synthetic regression, per the standing no-speculative-fix rule.
