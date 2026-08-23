# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Generic wait-for-graph / deadlock analyzer for SharpEmu run logs.

This is the OFFLINE form of a synchronization stall snapshot (see the live
design in docs/native-guest-execution-v2-stage13-*.md). It reconstructs, from a
completed run log, the terminal wait-for graph:

    guest-thread A  --waits-on-->  semaphore X
    semaphore X     --last-signaled-by-->  guest-thread B
    guest-thread B  --waits-on-->  semaphore Y ...

and flags cycles (classic deadlock) plus "cross-wait" structures where a thread
that is the sole producer of a semaphore with a terminal timeout flood is itself
terminally blocked on another semaphore.

A thread is "terminally blocked on H" when its LAST synchronization event in the
log is a blocking wait-enter on H (there is no later wake/signal/wait for it).

Nothing title-specific is baked in.

Usage:
    python3 scripts/analyze_waitfor_graph.py LOG [--json]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from dataclasses import dataclass, field
from typing import Dict, List, Optional, Sequence, Tuple

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_semaphore_timeline as az  # noqa: E402


_SEMA_RE = re.compile(r"sema\.([a-z-]+)\s+(.*)$")
_SCHED_RE = re.compile(r"Scheduled guest thread '([^']*)'\s+handle=(0x[0-9A-Fa-f]+)")
_TIMEOUT_RE = re.compile(
    r"Import#\d+\s+result:\s+\S*TIMED_OUT\S*\s+\([^)]*\)\s+rdi=(0x[0-9A-Fa-f]+).*?ret=(0x[0-9A-Fa-f]+)"
)

_BLOCK_ENTER = {"wait", "wait-block", "wait-host-block", "wait-recheck"}
_PROGRESS = {"wait-wake", "wait-host-wake", "signal"}


@dataclass
class ThreadState:
    guest: str
    name: str = ""
    last_line: int = 0
    last_kind: str = ""      # last sema verb seen for this thread
    last_handle: Optional[str] = None
    last_rip: Optional[str] = None


@dataclass
class HandleState:
    handle: str
    names: set = field(default_factory=set)
    signal_count: int = 0
    last_signaler_guest: Optional[str] = None
    last_signaler_rip: Optional[str] = None
    timeout_count: int = 0
    waiter_rips: set = field(default_factory=set)


class WaitForGraph:
    def __init__(self) -> None:
        self.thread_names: Dict[str, str] = {}
        self.threads: Dict[str, ThreadState] = {}
        self.handles: Dict[str, HandleState] = {}

    def _thread(self, guest: str) -> ThreadState:
        ts = self.threads.get(guest)
        if ts is None:
            ts = ThreadState(guest=guest, name=self.thread_names.get(guest, ""))
            self.threads[guest] = ts
        return ts

    def _handle(self, handle: str, name: Optional[str]) -> HandleState:
        hs = self.handles.get(handle)
        if hs is None:
            hs = HandleState(handle=handle)
            self.handles[handle] = hs
        if name:
            hs.names.add(name)
        return hs

    def feed_line(self, line_no: int, line: str) -> None:
        m = _SCHED_RE.search(line)
        if m:
            self.thread_names[az._norm_hex(m.group(2))] = m.group(1)
            return

        m = _TIMEOUT_RE.search(line)
        if m:
            handle = az._norm_hex(m.group(1))
            rip = az._norm_hex(m.group(2))
            hs = self._handle(handle, None)
            hs.timeout_count += 1
            if rip:
                hs.waiter_rips.add(rip)
            return

        m = _SEMA_RE.search(line)
        if not m:
            return
        verb, rest = m.group(1), m.group(2)
        kv = az._parse_kv(rest)
        handle = az._norm_hex(kv.get("handle"))
        name = kv.get("name")
        rip = az._norm_hex(kv.get("ret"))
        guest = az._norm_hex(kv.get("guest"))

        if handle is None:
            return
        hs = self._handle(handle, name)

        if verb == "signal":
            hs.signal_count += 1
            if guest and guest != "0x0":
                hs.last_signaler_guest = guest
                hs.last_signaler_rip = rip
                ts = self._thread(guest)
                ts.last_line, ts.last_kind, ts.last_handle, ts.last_rip = (
                    line_no, verb, handle, rip)
            return

        # Per-thread last event (only when we know the thread).
        if guest and guest != "0x0":
            ts = self._thread(guest)
            ts.last_line, ts.last_kind, ts.last_handle, ts.last_rip = (
                line_no, verb, handle, rip)

    def feed_file(self, path: str) -> None:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for i, line in enumerate(fh, start=1):
                self.feed_line(i, line.rstrip("\n"))
        # Backfill names now that all Scheduled lines are seen.
        for g, ts in self.threads.items():
            if not ts.name:
                ts.name = self.thread_names.get(g, "")

    # -- analysis ----------------------------------------------------------- #

    def terminally_blocked(self) -> List[ThreadState]:
        """Threads whose last observed event is a blocking wait-enter."""
        return [
            ts for ts in self.threads.values()
            if ts.last_kind in _BLOCK_ENTER
        ]

    def edges(self) -> List[dict]:
        """thread --waits-on--> handle --last-signaled-by--> thread edges for the
        terminally-blocked set."""
        out = []
        for ts in self.terminally_blocked():
            hs = self.handles.get(ts.last_handle)
            producer = None
            if hs and hs.last_signaler_guest:
                producer = self.thread_names.get(
                    hs.last_signaler_guest, hs.last_signaler_guest)
            out.append({
                "thread": ts.name or ts.guest,
                "thread_guest": ts.guest,
                "waits_on": ts.last_handle,
                "wait_rip": ts.last_rip,
                "handle_names": sorted(hs.names) if hs else [],
                "handle_last_signaled_by": producer,
                "handle_signal_count": hs.signal_count if hs else 0,
                "handle_timeout_count": hs.timeout_count if hs else 0,
            })
        return out

    def cycles(self) -> List[List[str]]:
        """Detect dependency cycles among terminally-blocked threads:
        thread T depends on the last signaler of the handle it waits on."""
        # Build guest -> depends-on-guest.
        dep: Dict[str, str] = {}
        blocked = {ts.guest: ts for ts in self.terminally_blocked()}
        for guest, ts in blocked.items():
            hs = self.handles.get(ts.last_handle)
            if hs and hs.last_signaler_guest and hs.last_signaler_guest in blocked:
                dep[guest] = hs.last_signaler_guest

        seen_cycles: List[List[str]] = []
        for start in dep:
            path = []
            node = start
            local = set()
            while node in dep and node not in local:
                local.add(node)
                path.append(node)
                node = dep[node]
            if node in local:
                # found a cycle; rotate to canonical form
                idx = path.index(node)
                cyc = path[idx:]
                names = [self.thread_names.get(g, g) for g in cyc]
                if sorted(names) not in [sorted(c) for c in seen_cycles]:
                    seen_cycles.append(names)
        return seen_cycles

    def cross_waits(self) -> List[dict]:
        """A terminally-blocked thread that is also the sole/last producer of a
        DIFFERENT handle which is suffering a terminal timeout flood. Strong
        two-primitive-deadlock signal even when the peer waiter is anonymous
        (host-blocked guest=0)."""
        out = []
        blocked = {ts.guest: ts for ts in self.terminally_blocked()}
        for guest, ts in blocked.items():
            for h, hs in self.handles.items():
                if h == ts.last_handle:
                    continue
                if hs.last_signaler_guest == guest and hs.timeout_count >= 20:
                    out.append({
                        "producer_thread": ts.name or guest,
                        "producer_blocked_on": ts.last_handle,
                        "producer_block_rip": ts.last_rip,
                        "starved_handle": h,
                        "starved_handle_names": sorted(hs.names),
                        "starved_timeout_count": hs.timeout_count,
                        "starved_waiter_rips": sorted(hs.waiter_rips),
                    })
        return out

    def to_dict(self) -> dict:
        return {
            "terminally_blocked_threads": [
                {"thread": ts.name or ts.guest, "guest": ts.guest,
                 "waits_on": ts.last_handle, "wait_rip": ts.last_rip,
                 "last_line": ts.last_line}
                for ts in sorted(self.terminally_blocked(), key=lambda t: t.last_line)
            ],
            "edges": self.edges(),
            "cycles": self.cycles(),
            "cross_waits": self.cross_waits(),
        }


def render_text(g: WaitForGraph) -> str:
    d = g.to_dict()
    L = ["=" * 72, "WAIT-FOR GRAPH (terminal snapshot reconstructed from log)", "=" * 72]
    L.append("")
    L.append("Terminally-blocked threads (last event is a blocking wait):")
    for t in d["terminally_blocked_threads"]:
        L.append(f"  {t['thread']:<28} waits on {t['waits_on']} "
                 f"(rip {t['wait_rip']}) @ line {t['last_line']}")
    L.append("")
    L.append("Wait-for edges:")
    for e in d["edges"]:
        L.append(f"  {e['thread']} -> {e['waits_on']} "
                 f"{'/'.join(e['handle_names'])} "
                 f"(last signaled by: {e['handle_last_signaled_by']}, "
                 f"signals={e['handle_signal_count']}, timeouts={e['handle_timeout_count']})")
    L.append("")
    if d["cycles"]:
        L.append("*** DEADLOCK CYCLES DETECTED ***")
        for c in d["cycles"]:
            L.append("  " + " -> ".join(c) + " -> (back to start)")
    else:
        L.append("No fully-resolvable dependency cycle (peer may be host-blocked/anonymous).")
    L.append("")
    if d["cross_waits"]:
        L.append("Suspected two-primitive cross-waits (producer blocked while its "
                 "consumers starve):")
        for cw in d["cross_waits"]:
            L.append(f"  {cw['producer_thread']} is blocked on {cw['producer_blocked_on']} "
                     f"but is the last producer of {cw['starved_handle']} "
                     f"({'/'.join(cw['starved_handle_names'])}) which timed out "
                     f"{cw['starved_timeout_count']}x "
                     f"(waiters {', '.join(cw['starved_waiter_rips'])})")
    return "\n".join(L)


def run(argv: Optional[Sequence[str]] = None) -> int:
    p = argparse.ArgumentParser(description="Reconstruct a wait-for graph / detect deadlock from a run log.")
    p.add_argument("log")
    p.add_argument("--json", action="store_true")
    args = p.parse_args(argv)
    g = WaitForGraph()
    try:
        g.feed_file(args.log)
    except OSError as exc:
        print(f"error: cannot read {args.log}: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(g.to_dict(), indent=2) if args.json else render_text(g))
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
