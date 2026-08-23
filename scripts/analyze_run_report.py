# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Generic per-title compatibility reporter for SharpEmu / VirtualPS5 run logs.

Given one SharpEmu run log, produce a concise "what happened / where did it get
stuck" report so an untouched title can be triaged into: first causal blocker ->
generic fix -> regression. Nothing title-specific is baked in.

It summarises:
  * run size (lines, max Import#, first/last present)
  * missing dynamic symbols (dlsym FAIL) grouped by module
  * unresolved imports (by NID)
  * failing HLE return codes (by code, with the NIDs that produced them)
  * repeated wait timeouts (by semaphore handle / caller)
  * guest exception fingerprints (by type) and fatal/AV lines
  * thread names seen
  * a stall-point candidate: the semaphore whose terminal timeout streak is
    longest (reusing analyze_semaphore_timeline), with its waiter/producer RIPs
  * a first-causal-blocker heuristic

Usage:
    python3 scripts/analyze_run_report.py LOG [--json] [--top N]
"""
from __future__ import annotations

import argparse
import json
import os
import re
import sys
from collections import Counter, defaultdict
from typing import Dict, List, Optional, Sequence

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import analyze_semaphore_timeline as az  # noqa: E402


_DLSYM_FAIL_RE = re.compile(
    r"dlsym#\d+\s+FAIL\s+handle=(0x[0-9A-Fa-f]+)\s+module='([^']*)'\s+symbol='([^']*)'"
)
_IMPORT_RESULT_RE = re.compile(
    r"Import#(\d+)\s+result:\s+(\S+)\s+\(([^)]*)\)"
)
_IMPORT_UNRESOLVED_RE = re.compile(r"Import#\d+\s+unresolved:\s+nid=(\S+)")
_PRESENT_RE = re.compile(r"Vulkan VideoOut presented\s+([A-Za-z ]+?):")
_SCHED_RE = re.compile(r"Scheduled guest thread '([^']*)'")
_GUEST_EXC_RE = re.compile(r"guest_exception\.(\w+)\s+target=(0x[0-9A-Fa-f]+)\s+type=(0x[0-9A-Fa-f]+)")
_IMPORT_NO_RE = re.compile(r"Import#(\d+)")
_FATAL_RE = re.compile(
    r"(__fastfail|access violation|AccessViolation|Unhandled exception|"
    r"FATAL|MEMORY_FAULT|segmentation fault|Fatal error)",
    re.IGNORECASE,
)


class RunReport:
    def __init__(self) -> None:
        self.total_lines = 0
        self.max_import = 0
        self.present: List[Dict] = []
        self.missing_symbols: "defaultdict[str, list]" = defaultdict(list)
        self.unresolved_nids: Counter = Counter()
        self.error_codes: Counter = Counter()
        self.error_code_nids: "defaultdict[str, Counter]" = defaultdict(Counter)
        self.exception_types: Counter = Counter()
        self.fatal_lines: List[Dict] = []
        self.thread_names: set = set()

    def feed(self, line_no: int, line: str) -> None:
        self.total_lines = line_no

        m = _IMPORT_NO_RE.search(line)
        if m:
            n = int(m.group(1))
            if n > self.max_import:
                self.max_import = n

        m = _DLSYM_FAIL_RE.search(line)
        if m:
            handle, module, symbol = m.group(1), m.group(2), m.group(3)
            self.missing_symbols[module].append(symbol)
            return

        m = _IMPORT_UNRESOLVED_RE.search(line)
        if m:
            self.unresolved_nids[m.group(1)] += 1
            return

        m = _IMPORT_RESULT_RE.search(line)
        if m:
            code, nid = m.group(2), m.group(3)
            # OK-ish codes are not "failures"; count anything not OK.
            if "OK" not in code:
                self.error_codes[code] += 1
                self.error_code_nids[code][nid] += 1
            return

        m = _PRESENT_RE.search(line)
        if m:
            self.present.append({"line": line_no, "kind": m.group(1).strip()})
            return

        m = _SCHED_RE.search(line)
        if m:
            self.thread_names.add(m.group(1))
            return

        m = _GUEST_EXC_RE.search(line)
        if m:
            if m.group(1) in ("raise", "queued"):
                self.exception_types[m.group(3)] += 1
            return

        if _FATAL_RE.search(line):
            # Keep a bounded sample of fatal-looking lines.
            if len(self.fatal_lines) < 40:
                self.fatal_lines.append({"line": line_no, "text": line.strip()[:200]})


def _dedupe_counts(items: List[str]) -> List[Dict]:
    c = Counter(items)
    return [{"symbol": k, "count": v} for k, v in c.most_common()]


def build_report(path: str, top: int) -> dict:
    rep = RunReport()
    with open(path, "r", encoding="utf-8", errors="replace") as fh:
        for i, line in enumerate(fh, start=1):
            rep.feed(i, line.rstrip("\n"))

    # Stall candidate: first rank semaphores by timeout count (aggregate pass),
    # then run the analyzer in TARGETED mode on the worst handle so its per-event
    # sequence (and therefore its terminal timeout streak) is available.
    agg = az.Analyzer()
    agg.feed_file(path)
    ranked = sorted(agg.timelines.values(), key=lambda t: t.n_timeout, reverse=True)
    stall_candidate = None
    if ranked and ranked[0].n_timeout >= 20 and ranked[0].handle:
        target = az.Analyzer(target_handle=ranked[0].handle)
        target.feed_file(path)
        tl = target.timelines.get(ranked[0].handle)
        if tl is not None:
            d = tl.to_dict()
            stall_candidate = {
                "handle": d["handle"],
                "names": d["names"],
                "timeouts": d["counts"]["timeout"],
                "signals": d["counts"]["signal"],
                "wakes": d["counts"]["wake"],
                "signal_equals_wake": d["counts"]["signal"] == d["counts"]["wake"],
                "last_progress_line": d["last_progress_line"],
                "last_timeout_line": d["last_timeout_line"],
                "longest_timeout_streak": d["longest_timeout_streak"]["length"],
                "waiter_rips": list(d["waiter_rips"].keys()),
                "signaler_rips": list(d["signaler_rips"].keys()),
                "signaler_threads": [
                    target.thread_names.get(g, g) for g in d["signaler_guests"]
                ],
            }

    missing = {
        module: _dedupe_counts(syms)[:top]
        for module, syms in rep.missing_symbols.items()
    }

    blocker = _first_causal_blocker(rep, stall_candidate)

    return {
        "log": path,
        "total_lines": rep.total_lines,
        "max_import": rep.max_import,
        "present_markers": rep.present,
        "first_present": rep.present[0] if rep.present else None,
        "last_present": rep.present[-1] if rep.present else None,
        "thread_count": len(rep.thread_names),
        "thread_names": sorted(rep.thread_names),
        "missing_symbols_by_module": missing,
        "unresolved_nids": rep.unresolved_nids.most_common(top),
        "error_codes": rep.error_codes.most_common(),
        "error_code_top_nids": {
            code: nids.most_common(top) for code, nids in rep.error_code_nids.items()
        },
        "exception_types": rep.exception_types.most_common(),
        "fatal_lines": rep.fatal_lines,
        "stall_candidate": stall_candidate,
        "first_causal_blocker": blocker,
    }


def _first_causal_blocker(rep: RunReport, stall: Optional[dict]) -> dict:
    """Heuristic ranking of the single most likely first blocker."""
    # A fatal/AV line is the strongest signal.
    if rep.fatal_lines:
        return {
            "kind": "fatal-or-fault",
            "detail": rep.fatal_lines[0]["text"],
            "line": rep.fatal_lines[0]["line"],
            "confidence": "high",
        }
    # A semaphore stuck in a long terminal timeout streak with balanced
    # signal==wake => upstream producer starvation / higher-level deadlock.
    if stall and stall["longest_timeout_streak"] >= 20:
        return {
            "kind": "producer-starvation-or-deadlock"
            if stall["signal_equals_wake"]
            else "possible-lost-wakeup",
            "detail": (
                f"semaphore {stall['handle']} ({','.join(stall['names'])}) timed out "
                f"{stall['timeouts']}x; signals={stall['signals']} wakes={stall['wakes']}; "
                f"last progress line {stall['last_progress_line']}"
            ),
            "confidence": "medium",
        }
    # Otherwise the most-repeated unresolved NID or missing symbol.
    if rep.unresolved_nids:
        nid, n = rep.unresolved_nids.most_common(1)[0]
        return {"kind": "unresolved-import", "detail": f"nid={nid} x{n}", "confidence": "medium"}
    if rep.missing_symbols:
        module = max(rep.missing_symbols, key=lambda k: len(rep.missing_symbols[k]))
        return {
            "kind": "missing-dynamic-symbol",
            "detail": f"module={module} missing={len(rep.missing_symbols[module])}",
            "confidence": "low",
        }
    return {"kind": "none-identified", "detail": "no clear blocker", "confidence": "low"}


def render_text(r: dict) -> str:
    L: List[str] = []
    L.append("=" * 72)
    L.append("VIRTUALPS5 RUN COMPATIBILITY REPORT")
    L.append("=" * 72)
    L.append(f"log             : {r['log']}")
    L.append(f"log lines       : {r['total_lines']}")
    L.append(f"max Import#      : {r['max_import']}")
    fp, lp = r["first_present"], r["last_present"]
    L.append(f"first present    : {fp['kind'] + ' @ line ' + str(fp['line']) if fp else 'NONE'}")
    L.append(f"last present     : {lp['kind'] + ' @ line ' + str(lp['line']) if lp else 'NONE'}")
    L.append(f"guest threads    : {r['thread_count']}")
    L.append("")
    L.append("--- FIRST CAUSAL BLOCKER CANDIDATE ---")
    b = r["first_causal_blocker"]
    L.append(f"  kind      : {b['kind']}")
    L.append(f"  detail    : {b['detail']}")
    L.append(f"  confidence: {b['confidence']}")
    L.append("")
    if r["stall_candidate"]:
        s = r["stall_candidate"]
        L.append("--- STALL CANDIDATE (worst terminal timeout streak) ---")
        L.append(f"  handle {s['handle']} {','.join(s['names'])}")
        L.append(f"  timeouts={s['timeouts']} signals={s['signals']} wakes={s['wakes']} "
                 f"(signal==wake: {s['signal_equals_wake']})")
        L.append(f"  longest timeout streak={s['longest_timeout_streak']} "
                 f"last progress line={s['last_progress_line']}")
        L.append(f"  waiter RIPs   : {', '.join(s['waiter_rips'])}")
        L.append(f"  signaler RIPs : {', '.join(s['signaler_rips'])}")
        L.append(f"  signaler thrds: {', '.join(str(x) for x in s['signaler_threads'])}")
        L.append("")
    L.append("--- FAILING HLE RETURN CODES ---")
    for code, n in r["error_codes"]:
        L.append(f"  {code}: {n}")
        for nid, cn in r["error_code_top_nids"].get(code, [])[:5]:
            L.append(f"      {nid} x{cn}")
    L.append("")
    L.append("--- UNRESOLVED IMPORTS (NID) ---")
    for nid, n in r["unresolved_nids"]:
        L.append(f"  {nid} x{n}")
    L.append("")
    L.append("--- MISSING DYNAMIC SYMBOLS (dlsym FAIL) ---")
    for module, syms in r["missing_symbols_by_module"].items():
        L.append(f"  {module}:")
        for s in syms:
            L.append(f"      {s['symbol']} x{s['count']}")
    L.append("")
    L.append("--- GUEST EXCEPTION FINGERPRINTS (type) ---")
    for t, n in r["exception_types"]:
        L.append(f"  type={t} x{n}")
    if r["fatal_lines"]:
        L.append("")
        L.append("--- FATAL / FAULT LINES (sample) ---")
        for fl in r["fatal_lines"][:10]:
            L.append(f"  line {fl['line']}: {fl['text']}")
    return "\n".join(L)


def run(argv: Optional[Sequence[str]] = None) -> int:
    p = argparse.ArgumentParser(description="Summarise a SharpEmu run log into a compatibility report.")
    p.add_argument("log", help="a SharpEmu run log")
    p.add_argument("--json", action="store_true", help="emit JSON")
    p.add_argument("--top", type=int, default=15, help="max items per ranked list")
    args = p.parse_args(argv)
    try:
        report = build_report(args.log, args.top)
    except OSError as exc:
        print(f"error: cannot read {args.log}: {exc}", file=sys.stderr)
        return 2
    print(json.dumps(report, indent=2) if args.json else render_text(report))
    return 0


if __name__ == "__main__":
    raise SystemExit(run())
