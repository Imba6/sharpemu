# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Generic semaphore-timeline analyzer for SharpEmu / VirtualPS5 run logs.

This tool reconstructs the lifecycle of one or more guest semaphores from a
SharpEmu trace log so that producer/consumer handshake stalls can be diagnosed
without hand-grepping hundreds of thousands of lines.

It is intentionally *generic*: nothing about any particular game or any
particular semaphore handle is baked into the parsing logic. The only
title-specific knowledge lives in the CLI arguments the caller supplies
(``--handle``, ``--name``, ``--caller``).

Recognised log vocabulary (all optional; unknown lines are ignored):

  sema.create        handle=0xH name='N' attr=0xA init=I max=M
  sema.delete        handle=0xH ...
  sema.signal        handle=0xH name='N' signal=S count=C waiters=W guest=0xG ret=0xR
  sema.wait          handle=0xH name='N' need=D count=C timeout=T
  sema.wait-block    handle=0xH name='N' need=D count=C timeout=T waiters=W guest=0xG ret=0xR
  sema.wait-host-block handle=0xH name='N' need=D count=C timeout=T guest=0xG ret=0xR
  sema.wait-wake     handle=0xH ... guest=0xG ret=0xR
  sema.wait-host-wake handle=0xH ... guest=0xG ret=0xR
  sema.wait-recheck  handle=0xH ...
  Import#N result: <CODE> (<NID>) rdi=0xH rsi=.. rdx=.. rcx=.. ret=0xR
  Scheduled guest thread 'NAME' handle=0xH entry=0xE ...
  Vulkan VideoOut presented <kind>: ...
  Forcing call to sce::Agc::suspendPoint ...
  guest_exception.<verb> target=0xT type=0xTY ...

For an ``Import#`` result line whose NID/return code represent a semaphore wait,
the semaphore handle is taken from ``rdi`` (first integer argument in the PS5
kernel ABI). This is how timeouts (ORBIS_GEN2_ERROR_TIMED_OUT) are attributed to
a semaphore, since the timeout is reported on the import boundary rather than as
a ``sema.*`` trace line.

Usage:
    python3 scripts/analyze_semaphore_timeline.py LOG [LOG ...]
        # no target -> list every semaphore with summary counters

    python3 scripts/analyze_semaphore_timeline.py LOG --handle 0x86
    python3 scripts/analyze_semaphore_timeline.py LOG --name Baselib_SystemSemaphore
    python3 scripts/analyze_semaphore_timeline.py LOG --caller 0x800D18129 --json
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import dataclass, field
from typing import Dict, Iterable, List, Optional, Sequence, Tuple


# --------------------------------------------------------------------------- #
# Parsing
# --------------------------------------------------------------------------- #

# A single "kv=value" scanner; values may be 0xHEX, decimal, 'infinite', or a
# single-quoted string. We keep raw string forms and normalise on demand.
_KV_RE = re.compile(r"(\w+)=('(?:[^']*)'|0x[0-9A-Fa-f]+|-?\d+|infinite|\w+)")

_SEMA_RE = re.compile(r"sema\.([a-z-]+)\s+(.*)$")

_IMPORT_RESULT_RE = re.compile(
    r"Import#(\d+)\s+result:\s+(\S+)\s+\(([^)]*)\)\s+(.*)$"
)

_SCHED_RE = re.compile(
    r"Scheduled guest thread '([^']*)'\s+handle=(0x[0-9A-Fa-f]+)"
)

_PRESENT_RE = re.compile(r"Vulkan VideoOut presented\s+([A-Za-z ]+?):")

_SUSPENDPOINT_RE = re.compile(r"Forcing call to sce::Agc::suspendPoint")

_GUEST_EXC_RE = re.compile(
    r"guest_exception\.(\w+)\s+target=(0x[0-9A-Fa-f]+)\s+type=(0x[0-9A-Fa-f]+)"
)


def _norm_hex(value: Optional[str]) -> Optional[str]:
    """Normalise a hex handle/RIP string to lower-case 0x form without leading
    zeros (but keep at least one digit). Non-hex inputs return unchanged."""
    if value is None:
        return None
    v = value.strip().strip("'")
    if v.lower().startswith("0x"):
        digits = v[2:].lstrip("0") or "0"
        return "0x" + digits.lower()
    return v


def _parse_kv(rest: str) -> Dict[str, str]:
    out: Dict[str, str] = {}
    for key, val in _KV_RE.findall(rest):
        if val.startswith("'") and val.endswith("'"):
            out[key] = val[1:-1]
        else:
            out[key] = val
    return out


# Which sema.* verbs count as which logical operation.
_WAIT_ENTER_VERBS = {"wait", "wait-block", "wait-host-block", "wait-recheck"}
_WAKE_VERBS = {"wait-wake", "wait-host-wake"}
_HOST_BLOCK_VERBS = {"wait-host-block"}
_COOP_BLOCK_VERBS = {"wait-block"}


@dataclass
class Event:
    """One parsed, semaphore-relevant log event."""

    line_no: int
    source: str            # which log file
    kind: str              # create/delete/signal/wait.../timeout/import-result
    handle: Optional[str]  # normalised 0x form
    name: Optional[str]
    rip: Optional[str]     # normalised caller ret address
    guest: Optional[str]   # normalised guest thread/object pointer
    fields: Dict[str, str] = field(default_factory=dict)


@dataclass
class Marker:
    line_no: int
    source: str
    kind: str          # present / suspendPoint / guest_exception
    detail: str


# --------------------------------------------------------------------------- #
# Aggregation
# --------------------------------------------------------------------------- #


@dataclass
class SemaphoreTimeline:
    handle: Optional[str] = None
    names: set = field(default_factory=set)
    create_line: Optional[int] = None
    create_source: Optional[str] = None
    init_count: Optional[str] = None
    max_count: Optional[str] = None
    attr: Optional[str] = None
    delete_lines: List[int] = field(default_factory=list)

    waiter_rips: Dict[str, int] = field(default_factory=dict)
    signaler_rips: Dict[str, int] = field(default_factory=dict)
    waiter_guests: Dict[str, int] = field(default_factory=dict)
    signaler_guests: Dict[str, int] = field(default_factory=dict)

    n_wait_enter: int = 0
    n_wait_block: int = 0        # cooperative block
    n_wait_host_block: int = 0   # host block
    n_wake: int = 0
    n_signal: int = 0
    n_timeout: int = 0

    first_signal_line: Optional[int] = None
    last_signal_line: Optional[int] = None
    first_timeout_line: Optional[int] = None
    last_timeout_line: Optional[int] = None

    # Ordered, lightweight record of (line_no, category) where category is
    # "signal" | "wake" | "timeout" | "block" for streak computation. Only kept
    # when a specific semaphore is targeted (bounded to one semaphore).
    sequence: List[Tuple[int, str]] = field(default_factory=list)

    def observe(self, ev: Event, keep_sequence: bool) -> None:
        if ev.handle and self.handle is None:
            self.handle = ev.handle
        if ev.name:
            self.names.add(ev.name)

        if ev.kind == "create":
            self.create_line = ev.line_no
            self.create_source = ev.source
            self.init_count = ev.fields.get("init")
            self.max_count = ev.fields.get("max")
            self.attr = ev.fields.get("attr")
        elif ev.kind == "delete":
            self.delete_lines.append(ev.line_no)
        elif ev.kind == "signal":
            self.n_signal += 1
            if self.first_signal_line is None:
                self.first_signal_line = ev.line_no
            self.last_signal_line = ev.line_no
            if ev.rip:
                self.signaler_rips[ev.rip] = self.signaler_rips.get(ev.rip, 0) + 1
            if ev.guest and ev.guest != "0x0":
                self.signaler_guests[ev.guest] = self.signaler_guests.get(ev.guest, 0) + 1
            if keep_sequence:
                self.sequence.append((ev.line_no, "signal"))
        elif ev.kind == "wake":
            self.n_wake += 1
            if keep_sequence:
                self.sequence.append((ev.line_no, "wake"))
        elif ev.kind == "timeout":
            self.n_timeout += 1
            if self.first_timeout_line is None:
                self.first_timeout_line = ev.line_no
            self.last_timeout_line = ev.line_no
            if ev.rip:
                self.waiter_rips[ev.rip] = self.waiter_rips.get(ev.rip, 0) + 1
            if keep_sequence:
                self.sequence.append((ev.line_no, "timeout"))
        elif ev.kind in ("wait_enter", "wait_block", "wait_host_block"):
            self.n_wait_enter += 1
            if ev.kind == "wait_block":
                self.n_wait_block += 1
            elif ev.kind == "wait_host_block":
                self.n_wait_host_block += 1
            if ev.rip:
                self.waiter_rips[ev.rip] = self.waiter_rips.get(ev.rip, 0) + 1
            if ev.guest and ev.guest != "0x0":
                self.waiter_guests[ev.guest] = self.waiter_guests.get(ev.guest, 0) + 1
            if keep_sequence:
                self.sequence.append((ev.line_no, "block"))

    # -- derived metrics ---------------------------------------------------- #

    def longest_timeout_streak(self) -> Tuple[int, Optional[int], Optional[int]]:
        """Return (length, start_line, end_line) of the longest consecutive run
        of timeouts uninterrupted by a signal or a wake."""
        best = (0, None, None)
        cur = 0
        cur_start = None
        cur_end = None
        for line_no, cat in self.sequence:
            if cat == "timeout":
                if cur == 0:
                    cur_start = line_no
                cur += 1
                cur_end = line_no
                if cur > best[0]:
                    best = (cur, cur_start, cur_end)
            elif cat in ("signal", "wake"):
                cur = 0
                cur_start = None
                cur_end = None
        return best

    def last_progress_line(self) -> Optional[int]:
        """Line number of the last signal or wake before the terminal timeout
        flood (i.e. the last time the handshake actually made progress)."""
        last = None
        for line_no, cat in self.sequence:
            if cat in ("signal", "wake"):
                last = line_no
        return last

    def to_dict(self) -> dict:
        streak_len, streak_start, streak_end = self.longest_timeout_streak()
        return {
            "handle": self.handle,
            "names": sorted(self.names),
            "create_line": self.create_line,
            "create_source": self.create_source,
            "attr": self.attr,
            "init": self.init_count,
            "max": self.max_count,
            "delete_lines": self.delete_lines,
            "counts": {
                "wait_enter": self.n_wait_enter,
                "wait_block_coop": self.n_wait_block,
                "wait_host_block": self.n_wait_host_block,
                "wake": self.n_wake,
                "signal": self.n_signal,
                "timeout": self.n_timeout,
            },
            "first_signal_line": self.first_signal_line,
            "last_signal_line": self.last_signal_line,
            "first_timeout_line": self.first_timeout_line,
            "last_timeout_line": self.last_timeout_line,
            "last_progress_line": self.last_progress_line(),
            "longest_timeout_streak": {
                "length": streak_len,
                "start_line": streak_start,
                "end_line": streak_end,
            },
            "waiter_rips": self.waiter_rips,
            "signaler_rips": self.signaler_rips,
            "waiter_guests": self.waiter_guests,
            "signaler_guests": self.signaler_guests,
        }


# --------------------------------------------------------------------------- #
# Driver
# --------------------------------------------------------------------------- #


class Analyzer:
    def __init__(
        self,
        target_handle: Optional[str] = None,
        target_name: Optional[str] = None,
        target_caller: Optional[str] = None,
    ) -> None:
        self.target_handle = _norm_hex(target_handle) if target_handle else None
        self.target_name = target_name
        self.target_caller = _norm_hex(target_caller) if target_caller else None
        self.timelines: Dict[str, SemaphoreTimeline] = {}
        self.thread_names: Dict[str, str] = {}   # handle -> name
        self.present_markers: List[Marker] = []
        self.suspendpoints: List[int] = []
        self.guest_exceptions: List[Marker] = []
        self.total_lines = 0
        self.max_import = 0
        # whether we retain per-event sequences (only when a single target is
        # selected, to keep memory bounded on multi-GB logs)
        self._keep_sequence = bool(
            self.target_handle or self.target_name or self.target_caller
        )

    # -- targeting ---------------------------------------------------------- #

    def _is_targeted(self, ev: Event) -> bool:
        if not self._keep_sequence:
            return True  # aggregate everything in list mode
        if self.target_handle and ev.handle == self.target_handle:
            return True
        if self.target_name and ev.name == self.target_name:
            return True
        if self.target_caller and ev.rip == self.target_caller:
            return True
        return False

    # -- line parsing ------------------------------------------------------- #

    def _parse_sema_line(self, line_no: int, source: str, verb: str, rest: str) -> Optional[Event]:
        kv = _parse_kv(rest)
        handle = _norm_hex(kv.get("handle"))
        name = kv.get("name")
        rip = _norm_hex(kv.get("ret"))
        guest = _norm_hex(kv.get("guest"))

        if verb == "create":
            kind = "create"
        elif verb == "delete":
            kind = "delete"
        elif verb == "signal":
            kind = "signal"
        elif verb in _WAKE_VERBS:
            kind = "wake"
        elif verb in _HOST_BLOCK_VERBS:
            kind = "wait_host_block"
        elif verb in _COOP_BLOCK_VERBS:
            kind = "wait_block"
        elif verb in _WAIT_ENTER_VERBS:
            kind = "wait_enter"
        else:
            kind = "wait_enter"  # unknown wait-ish verb; treat as an enter

        return Event(line_no, source, kind, handle, name, rip, guest, kv)

    def _parse_import_result(self, line_no: int, source: str, m: re.Match) -> Optional[Event]:
        import_no = int(m.group(1))
        code = m.group(2)
        nid = m.group(3)
        rest = m.group(4)
        if import_no > self.max_import:
            self.max_import = import_no
        # Only timeouts / errors on a wait are interesting for a semaphore; we
        # attribute them to the handle carried in rdi (first kernel arg).
        if "TIMED_OUT" not in code and "TIMEOUT" not in code.upper():
            return None
        kv = _parse_kv(rest)
        handle = _norm_hex(kv.get("rdi"))
        rip = _norm_hex(kv.get("ret"))
        fields = dict(kv)
        fields["nid"] = nid
        fields["code"] = code
        fields["import"] = str(import_no)
        return Event(line_no, source, "timeout", handle, None, rip, None, fields)

    def feed_line(self, line_no: int, source: str, line: str) -> None:
        self.total_lines = line_no

        m = _SCHED_RE.search(line)
        if m:
            self.thread_names[_norm_hex(m.group(2))] = m.group(1)
            return

        m = _PRESENT_RE.search(line)
        if m:
            self.present_markers.append(Marker(line_no, source, "present", m.group(1).strip()))
            return

        if _SUSPENDPOINT_RE.search(line):
            self.suspendpoints.append(line_no)
            return

        m = _GUEST_EXC_RE.search(line)
        if m:
            self.guest_exceptions.append(
                Marker(line_no, source, "guest_exception",
                       f"{m.group(1)} target={m.group(2)} type={m.group(3)}")
            )
            return

        m = _SEMA_RE.search(line)
        if m:
            ev = self._parse_sema_line(line_no, source, m.group(1), m.group(2))
            if ev and self._is_targeted(ev):
                self._record(ev)
            return

        m = _IMPORT_RESULT_RE.search(line)
        if m:
            ev = self._parse_import_result(line_no, source, m)
            # Timeout attribution: honour handle target, and also caller RIP
            # target (the wait NID's ret address) even if handle unknown.
            if ev and self._is_targeted(ev):
                self._record(ev)
            return

    def _record(self, ev: Event) -> None:
        key = ev.handle or (f"name:{ev.name}" if ev.name else f"rip:{ev.rip}")
        tl = self.timelines.get(key)
        if tl is None:
            tl = SemaphoreTimeline()
            self.timelines[key] = tl
        tl.observe(ev, self._keep_sequence)

    def feed_file(self, path: str) -> None:
        with open(path, "r", encoding="utf-8", errors="replace") as fh:
            for i, line in enumerate(fh, start=1):
                self.feed_line(i, path, line.rstrip("\n"))

    # -- reporting ---------------------------------------------------------- #

    def summary_dict(self) -> dict:
        return {
            "total_lines": self.total_lines,
            "max_import": self.max_import,
            "present_markers": [
                {"line": mk.line_no, "kind": mk.detail} for mk in self.present_markers
            ],
            "suspendpoint_count": len(self.suspendpoints),
            "suspendpoint_first_line": self.suspendpoints[0] if self.suspendpoints else None,
            "suspendpoint_last_line": self.suspendpoints[-1] if self.suspendpoints else None,
            "guest_exception_count": len(self.guest_exceptions),
            "semaphores": {k: tl.to_dict() for k, tl in self.timelines.items()},
        }


def _context_window(paths: Sequence[str], center: int, radius: int) -> List[str]:
    """Return raw lines [center-radius, center+radius] from the (single) log.
    Used to show neighbours around a key transition. Streams the file."""
    if not paths:
        return []
    path = paths[0]
    lo, hi = max(1, center - radius), center + radius
    out: List[str] = []
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for i, line in enumerate(fh, start=1):
            if i < lo:
                continue
            if i > hi:
                break
            out.append(f"{i}: {line.rstrip()}")
    return out


# --------------------------------------------------------------------------- #
# Text rendering
# --------------------------------------------------------------------------- #


def render_text(analyzer: Analyzer, paths: Sequence[str], context: int) -> str:
    lines: List[str] = []
    summ = analyzer.summary_dict()
    lines.append("=" * 72)
    lines.append("SEMAPHORE TIMELINE ANALYSIS")
    lines.append("=" * 72)
    lines.append(f"logs                : {', '.join(paths)}")
    lines.append(f"total log lines     : {summ['total_lines']}")
    lines.append(f"max Import# seen     : {summ['max_import']}")
    lines.append(f"present markers      : {len(summ['present_markers'])}")
    for mk in summ["present_markers"]:
        lines.append(f"    line {mk['line']}: presented {mk['kind']}")
    lines.append(
        f"forced suspendPoint  : {summ['suspendpoint_count']} "
        f"(first line {summ['suspendpoint_first_line']}, "
        f"last line {summ['suspendpoint_last_line']})"
    )
    lines.append(f"guest exceptions     : {summ['guest_exception_count']}")
    lines.append("")

    if not analyzer.timelines:
        lines.append("No matching semaphore events found.")
        return "\n".join(lines)

    # In list mode (no target), print a compact table sorted by timeout count.
    if not analyzer._keep_sequence:
        lines.append("All semaphores (sorted by timeout count desc):")
        lines.append(
            f"  {'handle':<10} {'signal':>8} {'wait':>8} {'hblock':>8} "
            f"{'wake':>8} {'timeout':>8}  name"
        )
        rows = sorted(
            analyzer.timelines.values(),
            key=lambda t: t.n_timeout,
            reverse=True,
        )
        for tl in rows:
            name = sorted(tl.names)[0] if tl.names else "?"
            lines.append(
                f"  {str(tl.handle):<10} {tl.n_signal:>8} {tl.n_wait_enter:>8} "
                f"{tl.n_wait_host_block:>8} {tl.n_wake:>8} {tl.n_timeout:>8}  {name}"
            )
        lines.append("")
        lines.append("Re-run with --handle 0xNN (or --name / --caller) for a deep timeline.")
        return "\n".join(lines)

    # Deep single-semaphore report.
    for key, tl in analyzer.timelines.items():
        d = tl.to_dict()
        lines.append("-" * 72)
        lines.append(f"SEMAPHORE {d['handle'] or key}")
        lines.append("-" * 72)
        lines.append(f"names               : {', '.join(d['names']) or '?'}")
        lines.append(
            f"created              : line {d['create_line']} "
            f"attr={d['attr']} init={d['init']} max={d['max']}"
        )
        if d["delete_lines"]:
            lines.append(f"deleted              : lines {d['delete_lines']}")
        c = d["counts"]
        lines.append("")
        lines.append("counters:")
        lines.append(f"    wait-enter total : {c['wait_enter']}")
        lines.append(f"    wait-block coop  : {c['wait_block_coop']}")
        lines.append(f"    wait-host-block  : {c['wait_host_block']}")
        lines.append(f"    wake             : {c['wake']}")
        lines.append(f"    signal           : {c['signal']}")
        lines.append(f"    timeout          : {c['timeout']}")
        lines.append("")
        lines.append("progress landmarks:")
        lines.append(f"    first signal line: {d['first_signal_line']}")
        lines.append(f"    last  signal line: {d['last_signal_line']}")
        lines.append(f"    first timeout ln : {d['first_timeout_line']}")
        lines.append(f"    last  timeout ln : {d['last_timeout_line']}")
        lines.append(f"    last progress ln : {d['last_progress_line']}  "
                     "(last signal/wake before terminal state)")
        st = d["longest_timeout_streak"]
        lines.append(
            f"    longest timeout streak: {st['length']} "
            f"(lines {st['start_line']}..{st['end_line']})"
        )
        lines.append("")
        lines.append("unique waiter RIPs (caller ret):")
        for rip, n in sorted(d["waiter_rips"].items(), key=lambda kv: -kv[1]):
            lines.append(f"    {rip}  x{n}")
        lines.append("unique signaler RIPs (caller ret):")
        for rip, n in sorted(d["signaler_rips"].items(), key=lambda kv: -kv[1]):
            lines.append(f"    {rip}  x{n}")
        lines.append("unique signaler guest pointers:")
        for g, n in sorted(d["signaler_guests"].items(), key=lambda kv: -kv[1]):
            nm = analyzer.thread_names.get(g, "")
            lines.append(f"    {g}  x{n}  {nm}")
        lines.append("")

        # Neighbour context around the last real progress point -> the stall.
        last_prog = d["last_progress_line"]
        if last_prog and context > 0:
            lines.append(f"context around LAST PROGRESS (line {last_prog}, +/-{context}):")
            for cl in _context_window(paths, last_prog, context):
                lines.append("    " + cl)
            lines.append("")

    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# CLI
# --------------------------------------------------------------------------- #


def build_arg_parser() -> argparse.ArgumentParser:
    p = argparse.ArgumentParser(
        description="Reconstruct guest semaphore lifecycle(s) from SharpEmu logs."
    )
    p.add_argument("logs", nargs="+", help="one or more SharpEmu run logs")
    p.add_argument("--handle", help="target semaphore handle, e.g. 0x86")
    p.add_argument("--name", help="target semaphore name, e.g. Baselib_SystemSemaphore")
    p.add_argument("--caller", help="target waiter/signaler caller RIP, e.g. 0x800D18129")
    p.add_argument("--json", action="store_true", help="emit JSON instead of text")
    p.add_argument(
        "--context", type=int, default=12,
        help="context radius (lines) to print around the stall transition (text mode)",
    )
    return p


def run(argv: Optional[Sequence[str]] = None) -> int:
    args = build_arg_parser().parse_args(argv)
    analyzer = Analyzer(
        target_handle=args.handle,
        target_name=args.name,
        target_caller=args.caller,
    )
    for path in args.logs:
        try:
            analyzer.feed_file(path)
        except OSError as exc:
            print(f"error: cannot read {path}: {exc}", file=sys.stderr)
            return 2

    if args.json:
        print(json.dumps(analyzer.summary_dict(), indent=2))
    else:
        print(render_text(analyzer, args.logs, args.context))
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
