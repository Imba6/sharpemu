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


# --------------------------------------------------------------------------- #
# Live sync-snapshot parser (authoritative point-in-time state)
# --------------------------------------------------------------------------- #
#
# A live snapshot (emitted by the runtime under SHARPEMU_DIAG_SYNC_SNAPSHOT=1 or
# SHARPEMU_DIAG_STALL_SNAPSHOT_MS) is a block of lines with a stable key=value
# grammar. Unlike the historical reconstruction above, it carries the REAL
# identity of host-parked waiters (no anonymous guest=0), so the wait-for graph
# it yields is authoritative for the instant it was taken:
#
#   [LOADER][DIAG] sync_snapshot.begin reason=<str> seq=<n>
#   [LOADER][DIAG] sync_snapshot.thread handle=0xH pthread=0xP name='NAME'
#       state=<S> rip=0xR resume_rip=0xRR host_tid=<n> worker=<inline|worker:n|none>
#       wait=<sema|mutex|equeue|eventflag|cond|none> obj=0xO key='K' need=<n>
#       timeout=<us|infinite|none> parked=<0|1> reentrant=<0|1> wait_ms=<n>
#   [LOADER][DIAG] sync_snapshot.sema handle=0xH name='NAME' count=<c> max=<m>
#       waiters=<w> last_signaler=0xG last_signaler_name='NAME'
#   [LOADER][DIAG] sync_snapshot.mutex obj=0xA name='NAME' owner=0xO
#       owner_name='NAME' recursion=<n> waiters=<w> abandoned=<0|1>
#   [LOADER][DIAG] sync_snapshot.equeue handle=0xH name='NAME' events=<n> waiters=<w>
#   [LOADER][DIAG] sync_snapshot.eventflag handle=0xH name='NAME' bits=0xB waiters=<w>
#   [LOADER][DIAG] sync_snapshot.end seq=<n> threads=<n> blocked=<n>
#
# The C# emitter MUST match this grammar. When a peer identity genuinely cannot
# be recovered, the emitter writes the literal token UNKNOWN (never a fabricated
# handle), and this parser preserves it verbatim.

_SNAP_RE = re.compile(r"sync_snapshot\.(\w+)\s*(.*)$")

# object-providing kinds: how you find the thread that would unblock a waiter.
_PROVIDER_KEY = {
    "sema": "last_signaler",   # who last posted it
    "mutex": "owner",          # who holds it
}


class SyncSnapshot:
    """Authoritative wait-for graph built from the LAST complete snapshot block."""

    def __init__(self) -> None:
        self.reason: Optional[str] = None
        self.threads: List[dict] = []
        self.objects: Dict[str, dict] = {}   # obj id -> record (incl. 'kind')
        self._cur_threads: List[dict] = []
        self._cur_objects: Dict[str, dict] = {}
        self._in_block = False
        self.present = False

    def feed_line(self, line: str) -> None:
        m = _SNAP_RE.search(line)
        if not m:
            return
        self.present = True
        kind, rest = m.group(1), m.group(2)
        kv = az._parse_kv(rest)
        if kind == "begin":
            self._in_block = True
            self._cur_threads = []
            self._cur_objects = {}
            self._cur_reason = kv.get("reason")
            return
        if kind == "end":
            # commit the just-completed block (last one wins).
            self.threads = self._cur_threads
            self.objects = self._cur_objects
            self.reason = getattr(self, "_cur_reason", None)
            self._in_block = False
            return
        if not self._in_block:
            return
        if kind == "thread":
            self._cur_threads.append(self._norm_thread(kv))
        elif kind in ("sema", "mutex", "equeue", "eventflag"):
            rec = {k: v for k, v in kv.items()}
            rec["kind"] = kind
            obj_id = az._norm_hex(kv.get("handle") or kv.get("obj"))
            rec["id"] = obj_id
            if obj_id:
                self._cur_objects[obj_id] = rec

    @staticmethod
    def _norm_thread(kv: Dict[str, str]) -> dict:
        return {
            "handle": az._norm_hex(kv.get("handle")),
            "pthread": az._norm_hex(kv.get("pthread")),
            "name": kv.get("name", ""),
            "state": kv.get("state", ""),
            "rip": az._norm_hex(kv.get("rip")),
            "resume_rip": az._norm_hex(kv.get("resume_rip")),
            "host_tid": kv.get("host_tid"),
            "worker": kv.get("worker"),
            "wait": kv.get("wait", "none"),
            "obj": az._norm_hex(kv.get("obj")),
            "key": kv.get("key"),
            "need": kv.get("need"),
            "timeout": kv.get("timeout"),
            "parked": kv.get("parked") == "1",
            "reentrant": kv.get("reentrant") == "1",
            "wait_ms": kv.get("wait_ms"),
        }

    def feed_file(self, path: str) -> None:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "sync_snapshot." in line:
                    self.feed_line(line.rstrip("\n"))

    # -- graph -------------------------------------------------------------- #

    def _name_of(self, handle: Optional[str]) -> Optional[str]:
        if not handle:
            return None
        for t in self.threads:
            if t["handle"] == handle:
                return t["name"] or handle
        return handle

    def blocked_threads(self) -> List[dict]:
        return [t for t in self.threads if t["wait"] not in ("none", "", None)]

    def _provider_handle(self, obj_id: Optional[str]) -> Optional[str]:
        """Handle of the thread expected to unblock a waiter on obj_id, or the
        literal 'UNKNOWN' when the snapshot could not recover it."""
        if not obj_id or obj_id not in self.objects:
            return None
        rec = self.objects[obj_id]
        field = _PROVIDER_KEY.get(rec["kind"])
        if field is None:
            return None  # external event source (equeue/eventflag)
        val = rec.get(field)
        if val is None or val == "UNKNOWN":
            return val
        return az._norm_hex(val)

    def edges(self) -> List[dict]:
        out = []
        for t in self.blocked_threads():
            provider = self._provider_handle(t["obj"])
            out.append({
                "thread": t["name"] or t["handle"],
                "thread_handle": t["handle"],
                "wait": t["wait"],
                "obj": t["obj"],
                "obj_name": (self.objects.get(t["obj"], {}) or {}).get("name"),
                "key": t["key"],
                "provider_handle": provider,
                "provider": ("UNKNOWN" if provider == "UNKNOWN"
                             else self._name_of(provider) if provider else None),
                "parked": t["parked"],
                "rip": t["rip"],
            })
        return out

    def cycles(self) -> List[List[str]]:
        # thread-handle -> provider-thread-handle (only resolvable providers).
        blocked = {t["handle"]: t for t in self.blocked_threads() if t["handle"]}
        dep: Dict[str, str] = {}
        for h, t in blocked.items():
            prov = self._provider_handle(t["obj"])
            if prov and prov != "UNKNOWN" and prov in blocked:
                dep[h] = prov
        cycles: List[List[str]] = []
        for start in dep:
            path, node, local = [], start, set()
            while node in dep and node not in local:
                local.add(node)
                path.append(node)
                node = dep[node]
            if node in local:
                cyc = path[path.index(node):]
                names = [self._name_of(h) for h in cyc]
                if sorted(names) not in [sorted(c) for c in cycles]:
                    cycles.append(names)
        return cycles

    def to_dict(self) -> dict:
        return {
            "source": "live-snapshot",
            "reason": self.reason,
            "threads": self.threads,
            "objects": list(self.objects.values()),
            "blocked_threads": [
                {"thread": t["name"] or t["handle"], "handle": t["handle"],
                 "wait": t["wait"], "obj": t["obj"], "parked": t["parked"],
                 "rip": t["rip"]}
                for t in self.blocked_threads()
            ],
            "edges": self.edges(),
            "cycles": self.cycles(),
        }


def render_snapshot(s: SyncSnapshot) -> str:
    d = s.to_dict()
    L = ["=" * 72, "WAIT-FOR GRAPH (authoritative live sync snapshot)", "=" * 72]
    L.append(f"reason: {d['reason']}")
    L.append("")
    L.append("Blocked threads (identity recovered, incl. former host-parked guest=0):")
    for t in d["blocked_threads"]:
        L.append(f"  {str(t['thread']):<28} waits {t['wait']} on {t['obj']} "
                 f"(rip {t['rip']}, parked={t['parked']})")
    L.append("")
    L.append("Wait-for edges:")
    for e in d["edges"]:
        prov = e["provider"] if e["provider"] is not None else "external-event"
        L.append(f"  {e['thread']} -> {e['wait']}:{e['obj']} "
                 f"{e['obj_name'] or ''} (unblocked by: {prov})")
    L.append("")
    if d["cycles"]:
        L.append("*** DEADLOCK CYCLES DETECTED ***")
        for c in d["cycles"]:
            L.append("  " + " -> ".join(str(x) for x in c) + " -> (back to start)")
    else:
        L.append("No resolvable dependency cycle in this snapshot.")
    return "\n".join(L)


def _log_has_snapshot(path: str) -> bool:
    try:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for line in fh:
                if "sync_snapshot.begin" in line:
                    return True
    except OSError:
        return False
    return False


def run(argv: Optional[Sequence[str]] = None) -> int:
    p = argparse.ArgumentParser(description="Reconstruct a wait-for graph / detect deadlock from a run log.")
    p.add_argument("log")
    p.add_argument("--json", action="store_true")
    p.add_argument("--mode", choices=["auto", "snapshot", "historical"], default="auto",
                   help="auto: use a live snapshot if present, else historical reconstruction")
    args = p.parse_args(argv)

    use_snapshot = args.mode == "snapshot" or (
        args.mode == "auto" and _log_has_snapshot(args.log))

    try:
        if use_snapshot:
            s = SyncSnapshot()
            s.feed_file(args.log)
            print(json.dumps(s.to_dict(), indent=2) if args.json else render_snapshot(s))
        else:
            g = WaitForGraph()
            g.feed_file(args.log)
            print(json.dumps(g.to_dict(), indent=2) if args.json else render_text(g))
    except OSError as exc:
        print(f"error: cannot read {args.log}: {exc}", file=sys.stderr)
        return 2
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
