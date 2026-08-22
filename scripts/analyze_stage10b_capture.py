#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Stage 10B capture analyzer (read-only).

Joins the Path-B LoadStartModule lifecycle trace (SHARPEMU_LOG_LOADSTART=1) with the
event-queue trace (SHARPEMU_LOG_EQUEUE=1) and the stall watchdog dump, to answer the
Stage-10B causal questions from a single Windows-native Cocoon log:

  1. which on-demand module_start never completed (begin without complete)
  2. which guest thread was running it
  3. which equeue that thread blocked on (handle/timeout/waiter/caller RIP)
  4. whether any producer ever delivered an event to that handle (and its ident/filter)
  5. the blocked-thread / candidate-producer states from the stall dump

It parses only; it never launches or modifies anything. Run it against the BAD log the
user captures on Windows. Pass --good <log> to diff a good ~30fps run against the bad one
and print the first module_start divergence.

Line grammars (from KernelRuntimeCompatExports.cs / KernelEventQueueCompatExports.cs /
KernelSyncTraceFormatter.cs):

  [LOADER][LOADSTART] enter seq=N guest_thread=0xH path='P' handle=N init=0xI prior_state=S
  [LOADER][LOADSTART] module_start.begin seq=N module='M' init=0xI guest_thread=0xH
  [LOADER][LOADSTART] module_start.complete seq=N module='M' started=B elapsed_ms=N guest_thread=0xH
  [LOADER][LOADSTART] module_start.skip seq=N handle=N state=S guest_thread=0xH
  [LOADER][TRACE] equeue.<op>: handle=0xH depth=N registrations=M <detail> thread='T' pthread=0x.. gth=0xH2 managed=.. ret=0xR frames=..
  [LOADER][ERROR] Stall guest-thread: handle=0xH name='N' state=S imports=N nid=.. ret=0xR .. block=<reason>
"""

import argparse
import re
import sys
from collections import defaultdict

LOADSTART = re.compile(r"\[LOADER\]\[LOADSTART\] (\S+) (.*)$")
KV = re.compile(r"(\w+)=('[^']*'|\S+)")
EQUEUE = re.compile(r"\[LOADER\]\[TRACE\] equeue\.([\w-]+): (.*)$")
STALL_THREAD = re.compile(r"\[LOADER\]\[ERROR\] Stall guest-thread: (.*)$")
STALL_STUB = re.compile(r"\[LOADER\]\[ERROR\] Stall import-stub: (.*)$")
NOPROG = re.compile(r"\[LOADER\]\[WARN\] No import progress for .*")
LOADSTART_STARTED = re.compile(r"\[LOADER\]\[INFO\] sceKernelLoadStartModule started '([^']+)'")

# Terminal-outcome grammar (added Stage-10B after the first real capture disproved the
# module_start/equeue hypothesis: the run instead reached preload and was force-unwound by the
# import-loop guard, then misreported as a backend failure).
GUARD_FIRED = re.compile(r"\[LOADER\]\[ERROR\] Import-loop guard fired at import#(\d+): nid=(\S+) ret=0x([0-9A-Fa-f]+)")
REPEAT_LOOP = re.compile(r"\[LOADER\]\[ERROR\] Detected repeating import loop and forced guest unwind to host\.")
BACKEND_FAILED = re.compile(r"\[DISPATCHER\] Native backend FAILED: (.*)$")
SUMMARY = re.compile(r"Summary: result=(\S+) reason=(\S+)")
FIRST_FRAME = re.compile(r"presented first frame")
SUSPEND_POINT = re.compile(r"suspendPoint")
# A guest thread polling one semaphore/equeue handle that never completes (finite-timeout fence
# poll that always times out) is the Stage-11B stall signature. suspendPoint count alone does NOT
# prove sustained gameplay -- both a good run and this stall emit ~30 forced suspends -- so a large,
# run-spanning repeated-timeout flood on one (nid,handle,caller) is a stronger "stuck" signal.
TIMED_OUT_WAIT = re.compile(
    r"Import#(\d+) result: ORBIS_GEN2_ERROR_TIMED_OUT \((\w+)\).*?rdi=0x([0-9A-Fa-f]+).*?ret=0x([0-9A-Fa-f]+)")

# Common spin/wait NIDs so the terminal report is human-readable without an aerolib round-trip.
NID_NAMES = {
    "T72hz6ffq08": "scePthreadYield",
    "9UK1vLZQft4": "pthread_mutex_lock",
    "JTvBflhYazQ": "sceKernelWaitEventFlag",
    "Zxa0VhQVTsk": "sceKernelWaitSema",
    "Op8TBGY5KHg": "pthread_cond_wait",
    "fzyMKs9kim0": "sceKernelWaitEqueue",
    "wzvqT4UqKX8": "sceKernelLoadStartModule",
}


def kv(text):
    out = {}
    for m in KV.finditer(text):
        v = m.group(2)
        out[m.group(1)] = v[1:-1] if v.startswith("'") else v
    return out


def norm_handle(h):
    """Normalize a 0x.. handle to a comparable lowercase hex string."""
    if h is None:
        return None
    try:
        return hex(int(h, 16))
    except ValueError:
        return h


def parse(path):
    events = {
        "loadstart": [],      # (lineno, kind, fields)
        "equeue": [],         # (lineno, op, fields, raw)
        "stall_threads": [],  # (lineno, fields)
        "stall_stub": [],     # (lineno, text)
        "started_info": [],   # (lineno, module) -- the completion INFO the emulator always prints
        "guard_fired": [],    # (lineno, import_index, nid, ret)
        "repeat_loop": [],    # (lineno,)
        "backend_failed": [], # (lineno, detail)
        "summary": [],        # (lineno, result, reason)
        "first_frame": 0,     # count
        "suspend_point": 0,   # count -- gameplay progress proxy (weak; see timed_out below)
        "timed_out": [],      # (lineno, import_index, nid, handle, caller)
    }
    with open(path, "r", errors="replace") as fh:
        for lineno, line in enumerate(fh, 1):
            line = line.rstrip("\n")
            m = LOADSTART.search(line)
            if m:
                events["loadstart"].append((lineno, m.group(1), kv(m.group(2))))
                continue
            m = EQUEUE.search(line)
            if m:
                events["equeue"].append((lineno, m.group(1), kv(m.group(2)), line))
                continue
            m = STALL_THREAD.search(line)
            if m:
                events["stall_threads"].append((lineno, kv(m.group(1))))
                continue
            m = STALL_STUB.search(line)
            if m:
                events["stall_stub"].append((lineno, m.group(1)))
                continue
            m = LOADSTART_STARTED.search(line)
            if m:
                events["started_info"].append((lineno, m.group(1)))
                continue
            m = GUARD_FIRED.search(line)
            if m:
                events["guard_fired"].append((lineno, m.group(1), m.group(2), m.group(3)))
                continue
            if REPEAT_LOOP.search(line):
                events["repeat_loop"].append((lineno,))
                continue
            m = BACKEND_FAILED.search(line)
            if m:
                events["backend_failed"].append((lineno, m.group(1)))
                continue
            m = SUMMARY.search(line)
            if m:
                events["summary"].append((lineno, m.group(1), m.group(2)))
                continue
            m = TIMED_OUT_WAIT.search(line)
            if m:
                events["timed_out"].append(
                    (lineno, m.group(1), m.group(2), norm_handle("0x" + m.group(3)),
                     "0x" + m.group(4)))
                continue
            if FIRST_FRAME.search(line):
                events["first_frame"] += 1
                continue
            if SUSPEND_POINT.search(line):
                events["suspend_point"] += 1
    return events


def find_stuck_fence(events, threshold=200):
    """Return the dominant (nid, handle, caller) finite-wait that times out repeatedly and spans to
    near the end of the log, or None. This is the Stage-11B 'stuck fence poll' signature."""
    from collections import Counter
    if not events["timed_out"]:
        return None
    groups = Counter((nid, handle, caller) for _, _, nid, handle, caller in events["timed_out"])
    (nid, handle, caller), count = groups.most_common(1)[0]
    if count < threshold:
        return None
    lines = [ln for ln, _, n, h, c in events["timed_out"] if (n, h, c) == (nid, handle, caller)]
    first_imp = next(imp for _, imp, n, h, c in events["timed_out"] if (n, h, c) == (nid, handle, caller))
    last_imp = [imp for _, imp, n, h, c in events["timed_out"] if (n, h, c) == (nid, handle, caller)][-1]
    return {"nid": nid, "handle": handle, "caller": caller, "count": count,
            "first_line": lines[0], "last_line": lines[-1],
            "first_import": first_imp, "last_import": last_imp}


def build_loadstart_table(events):
    """seq -> {enter, begin, complete, skip} dicts (each augmented with _lineno)."""
    table = defaultdict(dict)
    for lineno, kind, fields in events["loadstart"]:
        seq = fields.get("seq")
        if seq is None:
            continue
        fields = dict(fields)
        fields["_lineno"] = lineno
        # kind is one of: enter, module_start.begin, module_start.complete, module_start.skip
        key = kind.split(".")[-1]
        table[seq][key] = fields
    return table


def analyze_terminal(events):
    """Report how the run ended and whether it reached gameplay.

    A run can have every module_start complete yet still fail later (preload stall force-unwound
    by the import-loop guard, then misreported as a backend failure). This section makes that
    outcome visible instead of the module-only view calling it 'GOOD'.
    """
    print("\n-- Terminal outcome / progress --")
    ff = events["first_frame"]
    sp = events["suspend_point"]
    stuck = find_stuck_fence(events)
    # suspendPoint alone is a weak proxy (a stall past first frame emits ~as many as a good run).
    # A run-spanning stuck fence-poll overrides it: the game reached the render loop but wedged.
    reached_gameplay = sp >= 10 and stuck is None
    print(f"  first_frame_presented={ff}  suspendPoint(weak gameplay proxy)={sp}  "
          f"-> {'reached gameplay' if reached_gameplay else 'did NOT reach sustained gameplay'}")
    if stuck is not None:
        name = NID_NAMES.get(stuck["nid"], "?")
        print(f"  [!] STUCK FENCE POLL: nid={stuck['nid']} ({name}) handle={stuck['handle']} "
              f"caller=0x{int(stuck['caller'],16):X} timed out {stuck['count']}x "
              f"from import#{stuck['first_import']} to #{stuck['last_import']} (L{stuck['first_line']}"
              f"..L{stuck['last_line']}) -> a guest thread polls this fence forever; it is never "
              f"posted. The run reached the render loop but never sustained gameplay.")

    for ln, imp, nid, ret in events["guard_fired"]:
        name = NID_NAMES.get(nid, "?")
        print(f"  [!] IMPORT-LOOP GUARD FIRED @L{ln}: import#{imp} nid={nid} ({name}) ret=0x{ret} "
              f"-> a guest thread spin-looped this import >5s and was force-unwound to host.")
    for ln, in events["repeat_loop"]:
        print(f"  [!] Forced guest unwind (repeating import loop) @L{ln}.")
    for ln, detail in events["backend_failed"]:
        print(f"  [!] Native backend FAILED @L{ln}: {detail}  "
              f"(NOTE: 'unknown backend error' + NOT_IMPLEMENTED is a fallback bucket, not a real "
              f"crash/unimplemented feature -- see the guard firing above for the true cause.)")
    for ln, result, reason in events["summary"]:
        print(f"  Summary @L{ln}: result={result} reason={reason}")

    ok = (not events["guard_fired"] and not events["backend_failed"] and reached_gameplay)
    return ok, reached_gameplay


def analyze_single(path, events):
    print(f"\n=== Stage 10B analysis: {path} ===")
    _ok, reached_gameplay = analyze_terminal(events)
    table = build_loadstart_table(events)

    if not table and not events["started_info"]:
        # Expected when the run was captured with SHARPEMU_LOG_LOADSTART off (e.g. the Stage-11
        # guard-disabled A/B run). Module completion is still visible via the always-printed
        # "sceKernelLoadStartModule started 'X'" INFO lines.
        print("  [i] No [LOADSTART] trace lines (SHARPEMU_LOG_LOADSTART off?); "
              "using the unconditional 'started' INFO lines + terminal outcome instead.")
    # Path-B lifecycle summary
    print("\n-- Path-B LoadStartModule lifecycle --")
    stalled = []
    for seq in sorted(table, key=lambda s: int(s)):
        e = table[seq]
        begin = e.get("begin")
        complete = e.get("complete")
        skip = e.get("skip")
        enter = e.get("enter")
        name = (begin or {}).get("module") or (enter or {}).get("path") or "?"
        gth = (begin or enter or {}).get("guest_thread") or (skip or {}).get("guest_thread")
        if begin and not complete:
            stalled.append((seq, name, gth, begin))
            print(f"  seq={seq} module={name!r} guest_thread={gth} init={begin.get('init')}  "
                  f"** BEGIN, NO COMPLETE -> STALLED ** (begin@L{begin['_lineno']})")
        elif complete:
            print(f"  seq={seq} module={name!r} started={complete.get('started')} "
                  f"elapsed_ms={complete.get('elapsed_ms')} (COMPLETE@L{complete['_lineno']})")
        elif skip:
            print(f"  seq={seq} handle={skip.get('handle')} state={skip.get('state')} (SKIP -- claim refused)")
        elif enter:
            print(f"  seq={seq} path={enter.get('path')!r} handle={enter.get('handle')} "
                  f"prior_state={enter.get('prior_state')} (ENTER only -- no begin/skip)")

    if not stalled:
        n_started = len(events["started_info"])
        guard = bool(events["guard_fired"] or events["backend_failed"])
        n_modules = n_started if n_started else len({s for s in table if table[s].get("complete")})
        if guard:
            print(f"\n  [OK-module] No stalled module_start ({n_modules} module(s) started) -> the "
                  f"Stage-10B module_start/equeue hypothesis is DISPROVEN for this capture.")
            print(f"  [FAIL-run] The run did NOT succeed: see the terminal outcome above "
                  f"(import-loop guard / backend failure). Blocker is the preload spin, not module load.")
        elif reached_gameplay:
            print(f"\n  [OK] No stalled module_start ({n_modules} module(s) started) and the run "
                  f"REACHED SUSTAINED GAMEPLAY. GOOD run.")
            print(f"  [STAGE-11 SUCCESS] If this is the guard-disabled A/B run, the import-loop-guard "
                  f"false-positive is causally proven (same config + guard off -> gameplay).")
        else:
            print(f"\n  [WARN] No stalled module_start ({n_modules} module(s) started) and no guard "
                  f"firing, but the run did NOT reach sustained gameplay.")
            print(f"  [STAGE-11 FAILURE-CASE] If this is the guard-disabled A/B run, disabling the "
                  f"guard did NOT unblock it -> the guard was only shortening a pre-existing preload "
                  f"stall; the next blocker is preload throughput/correctness, not the guard.")
        return stalled

    # Correlate each stalled thread to its equeue wait + producers.
    print("\n-- Stalled module -> equeue wait -> producer correlation --")
    for seq, name, gth, begin in stalled:
        gthn = norm_handle(gth)
        print(f"\n  STALLED seq={seq} module={name!r} guest_thread={gth}")
        waits = [(ln, op, f, raw) for (ln, op, f, raw) in events["equeue"]
                 if op.startswith("wait") and norm_handle(f.get("gth")) == gthn]
        if not waits:
            print(f"    [!] No equeue wait line found for gth={gth}. "
                  f"Was SHARPEMU_LOG_EQUEUE=1 set? Or the block is not an equeue.")
        handles = set()
        for ln, op, f, raw in waits:
            h = norm_handle(f.get("handle"))
            handles.add(h)
            print(f"    equeue.{op}@L{ln}: handle={f.get('handle')} timeout={f.get('timeout')} "
                  f"waiter={f.get('waiter')} depth={f.get('depth')} registrations={f.get('registrations')} "
                  f"caller_rip={f.get('ret')}")
        # For each waited handle, did any real producer deliver after the wait?
        for h in sorted(x for x in handles if x):
            first_wait_ln = min((ln for ln, op, f, raw in waits if norm_handle(f.get("handle")) == h),
                                default=0)
            producers = []
            for ln, op, f, raw in events["equeue"]:
                if norm_handle(f.get("handle")) != h:
                    continue
                if ln <= first_wait_ln:
                    continue
                src = f.get("source", "")
                # A real external producer, not the waiter's own post-registration belt.
                is_real = op in ("wake", "enqueue", "trigger", "trigger_user") and src not in (
                    "post-registration-state-check", "")
                if op in ("trigger", "trigger_user", "enqueue") or (op == "wake" and src and
                                                                     src != "post-registration-state-check"):
                    producers.append((ln, op, src, f.get("ident"), f.get("filter"), is_real))
            if producers:
                print(f"    handle={h}: {len(producers)} producer/wake event(s) AFTER the block:")
                for ln, op, src, ident, filt, is_real in producers[:20]:
                    flag = "REAL-PRODUCER" if is_real else "belt/self"
                    print(f"      L{ln} equeue.{op} {src} ident={ident} filter={filt} [{flag}]")
                print("      -> event WAS delivered to the queue; if the waiter stayed blocked this is "
                      "CASE C (possible lost wake). Verify the thread never re-readied.")
            else:
                print(f"    handle={h}: NO producer/real-wake event after the block "
                      f"-> event was NEVER produced (CASE A/B: producer starvation or never-generated).")

    # Stall watchdog dump
    if events["stall_stub"] or events["stall_threads"]:
        print("\n-- Stall watchdog dump (candidate producers + blocked threads) --")
        for ln, txt in events["stall_stub"][:8]:
            print(f"    L{ln} stall-stub: {txt}")
        for ln, f in events["stall_threads"][:40]:
            print(f"    L{ln} thread handle={f.get('handle')} name={f.get('name')} "
                  f"state={f.get('state')} block={f.get('block')} nid={f.get('nid')}")
    return stalled


def analyze_diff(good_path, good_events, bad_path, bad_events):
    print(f"\n=== GOOD vs BAD first-divergence ({good_path} vs {bad_path}) ===")

    def seq_order(events):
        table = build_loadstart_table(events)
        rows = []
        for seq in sorted(table, key=lambda s: int(s)):
            e = table[seq]
            name = (e.get("begin") or e.get("enter") or {}).get("module") \
                or (e.get("enter") or {}).get("path") or "?"
            outcome = ("complete" if e.get("complete")
                       else "STALLED" if e.get("begin")
                       else "skip" if e.get("skip") else "enter")
            rows.append((seq, name, outcome))
        return rows

    g = seq_order(good_events)
    b = seq_order(bad_events)
    print("\n  seq  GOOD                              BAD")
    n = max(len(g), len(b))
    first_div = None
    for i in range(n):
        gr = g[i] if i < len(g) else None
        br = b[i] if i < len(b) else None
        gs = f"{gr[1]}:{gr[2]}" if gr else "-"
        bs = f"{br[1]}:{br[2]}" if br else "-"
        mark = ""
        if first_div is None and gs != bs:
            first_div = (i, gr, br)
            mark = "   <== FIRST DIVERGENCE"
        print(f"  {i:>3}  {gs:<32} {bs:<32}{mark}")
    if first_div:
        i, gr, br = first_div
        print(f"\n  First divergence at index {i}: GOOD={gr}  BAD={br}")
    else:
        print("\n  No module_start-sequence divergence found (the stall is later / equeue-timing only).")


def main():
    ap = argparse.ArgumentParser(description="Stage 10B LoadStartModule/equeue capture analyzer (read-only).")
    ap.add_argument("bad_log", help="the captured BAD (first-frame -> stall) Windows-native log")
    ap.add_argument("--good", help="an optional GOOD ~30fps log to diff against", default=None)
    args = ap.parse_args()

    bad_events = parse(args.bad_log)
    analyze_single(args.bad_log, bad_events)

    if args.good:
        good_events = parse(args.good)
        analyze_single(args.good, good_events)
        analyze_diff(args.good, good_events, args.bad_log, bad_events)

    print()


if __name__ == "__main__":
    sys.exit(main())
