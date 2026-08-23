# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Unit tests for scripts/analyze_waitfor_graph.py."""
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import analyze_waitfor_graph as wf  # noqa: E402


def _feed(g, text):
    for i, line in enumerate(text.strip("\n").splitlines(), start=1):
        g.feed_line(i, line)
    for gid, ts in g.threads.items():
        if not ts.name:
            ts.name = g.thread_names.get(gid, "")


# Two threads A and B in a classic cross-signal deadlock: A last blocks on X
# (which B last signaled), B last blocks on Y (which A last signaled).
DEADLOCK = """
[LOADER][INFO] Scheduled guest thread 'ThreadA' handle=0x00000000000000AA entry=0x0 arg=0x0
[LOADER][INFO] Scheduled guest thread 'ThreadB' handle=0x00000000000000BB entry=0x0 arg=0x0
[LOADER][TRACE] sema.create handle=0x00000010 name='X' attr=0x1 init=0 max=1
[LOADER][TRACE] sema.create handle=0x00000011 name='Y' attr=0x1 init=0 max=1
[LOADER][TRACE] sema.signal handle=0x00000010 name='X' signal=1 count=1 waiters=1 guest=0x00000000000000BB ret=0x0000000000001000
[LOADER][TRACE] sema.signal handle=0x00000011 name='Y' signal=1 count=1 waiters=1 guest=0x00000000000000AA ret=0x0000000000002000
[LOADER][TRACE] sema.wait-block handle=0x00000010 name='X' need=1 count=0 timeout=infinite waiters=1 guest=0x00000000000000AA ret=0x0000000000003000
[LOADER][TRACE] sema.wait-block handle=0x00000011 name='Y' need=1 count=0 timeout=infinite waiters=1 guest=0x00000000000000BB ret=0x0000000000004000
"""


# A cross-wait where the peer is anonymous (host-blocked guest=0): producer P is
# blocked on B, while handle A (last signaled by P) is timing out from an unknown
# host-blocked waiter.
CROSS_WAIT = """
[LOADER][INFO] Scheduled guest thread 'Producer' handle=0x00000000000000CC entry=0x0 arg=0x0
[LOADER][TRACE] sema.create handle=0x00000086 name='Baselib' attr=0x1 init=0 max=99
[LOADER][TRACE] sema.create handle=0x00000084 name='Baselib' attr=0x1 init=0 max=99
[LOADER][TRACE] sema.signal handle=0x00000086 name='Baselib' signal=1 count=1 waiters=1 guest=0x00000000000000CC ret=0x0000000800D17D53
[LOADER][TRACE] sema.wait-block handle=0x00000084 name='Baselib' need=1 count=0 timeout=infinite waiters=1 guest=0x00000000000000CC ret=0x0000000800D17CFB
""" + "".join(
    f"[LOADER][WARN] Import#{i} result: ORBIS_GEN2_ERROR_TIMED_OUT (Zxa0VhQVTsk) rdi=0x0000000000000086 rsi=0x1 rdx=0x0 rcx=0x1 ret=0x0000000800D18129\n"
    for i in range(25)
)


class DeadlockCycleTest(unittest.TestCase):
    def setUp(self):
        self.g = wf.WaitForGraph()
        _feed(self.g, DEADLOCK)

    def test_both_threads_terminally_blocked(self):
        names = {ts.name for ts in self.g.terminally_blocked()}
        self.assertEqual(names, {"ThreadA", "ThreadB"})

    def test_edges_resolve_producers(self):
        edges = {e["thread"]: e for e in self.g.edges()}
        self.assertEqual(edges["ThreadA"]["waits_on"], "0x10")
        self.assertEqual(edges["ThreadA"]["handle_last_signaled_by"], "ThreadB")
        self.assertEqual(edges["ThreadB"]["handle_last_signaled_by"], "ThreadA")

    def test_cycle_detected(self):
        cycles = self.g.cycles()
        self.assertEqual(len(cycles), 1)
        self.assertEqual(set(cycles[0]), {"ThreadA", "ThreadB"})


class CrossWaitTest(unittest.TestCase):
    def setUp(self):
        self.g = wf.WaitForGraph()
        _feed(self.g, CROSS_WAIT)

    def test_producer_terminally_blocked(self):
        names = {ts.name for ts in self.g.terminally_blocked()}
        self.assertIn("Producer", names)

    def test_cross_wait_flagged(self):
        cw = self.g.cross_waits()
        self.assertEqual(len(cw), 1)
        self.assertEqual(cw[0]["producer_thread"], "Producer")
        self.assertEqual(cw[0]["producer_blocked_on"], "0x84")
        self.assertEqual(cw[0]["starved_handle"], "0x86")
        self.assertGreaterEqual(cw[0]["starved_timeout_count"], 25)
        self.assertIn("0x800d18129", cw[0]["starved_waiter_rips"])

    def test_no_resolvable_cycle_when_peer_anonymous(self):
        # The 0x86 waiter is host-blocked (guest=0) so the cycle cannot fully
        # resolve; the cross-wait heuristic is what surfaces the deadlock.
        self.assertEqual(self.g.cycles(), [])

    def test_render_smoke(self):
        out = wf.render_text(self.g)
        self.assertIn("cross-wait", out.lower())


if __name__ == "__main__":
    unittest.main()
