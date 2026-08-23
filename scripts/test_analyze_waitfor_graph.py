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


# A live snapshot resolving the 0x86/0x84 cycle with real identities, including
# the former anonymous host-parked guest=0 waiter (now Background Job.Worker 0).
SNAPSHOT = """
[LOADER][DIAG] sync_snapshot.begin reason=stall stall_ms=2000 seq=1
[LOADER][DIAG] sync_snapshot.thread handle=0xAA pthread=0x100 name='Loading.PreloadManager' state=Blocked rip=0x800D17CFB resume_rip=0x0 host_tid=7 worker=inline wait=sema obj=0x84 key='k84' need=1 timeout=infinite parked=1 reentrant=0 wait_ms=6200
[LOADER][DIAG] sync_snapshot.thread handle=0xBB pthread=0x200 name='Background Job.Worker 0' state=Blocked rip=0x800D18129 resume_rip=0x0 host_tid=9 worker=inline wait=sema obj=0x86 key='k86' need=1 timeout=1000 parked=1 reentrant=0 wait_ms=6100
[LOADER][DIAG] sync_snapshot.sema handle=0x86 name='Baselib_SystemSemaphore' count=0 max=2147483647 waiters=1 last_signaler=0xAA last_signaler_name='Loading.PreloadManager'
[LOADER][DIAG] sync_snapshot.sema handle=0x84 name='Baselib_SystemSemaphore' count=0 max=2147483647 waiters=1 last_signaler=0xBB last_signaler_name='Background Job.Worker 0'
[LOADER][DIAG] sync_snapshot.end seq=1 threads=2 blocked=2
"""

# A mutex-cycle snapshot: A holds M1 (0x1001) waits M2 (0x1002); B holds M2 waits M1.
MUTEX_SNAPSHOT = """
[LOADER][DIAG] sync_snapshot.begin reason=manual seq=1
[LOADER][DIAG] sync_snapshot.thread handle=0xA1 name='ThreadA' state=Blocked rip=0x1 wait=mutex obj=0x1002 need=1 timeout=infinite parked=0 reentrant=0 wait_ms=10
[LOADER][DIAG] sync_snapshot.thread handle=0xB2 name='ThreadB' state=Blocked rip=0x2 wait=mutex obj=0x1001 need=1 timeout=infinite parked=0 reentrant=0 wait_ms=10
[LOADER][DIAG] sync_snapshot.mutex obj=0x1001 name='m1' owner=0xA1 owner_name='ThreadA' recursion=1 waiters=1 abandoned=0
[LOADER][DIAG] sync_snapshot.mutex obj=0x1002 name='m2' owner=0xB2 owner_name='ThreadB' recursion=1 waiters=1 abandoned=0
[LOADER][DIAG] sync_snapshot.end seq=1 threads=2 blocked=2
"""

# Anonymous provider: the peer that would signal 0x86 was not recoverable.
UNKNOWN_SNAPSHOT = """
[LOADER][DIAG] sync_snapshot.begin reason=manual seq=1
[LOADER][DIAG] sync_snapshot.thread handle=0xCC name='Consumer' state=Blocked rip=0x9 wait=sema obj=0x86 need=1 timeout=1000 parked=1 reentrant=0 wait_ms=99
[LOADER][DIAG] sync_snapshot.sema handle=0x86 name='Baselib' count=0 max=99 waiters=1 last_signaler=UNKNOWN last_signaler_name=UNKNOWN
[LOADER][DIAG] sync_snapshot.equeue handle=0x200 name='eq' events=0 waiters=1
[LOADER][DIAG] sync_snapshot.end seq=1 threads=1 blocked=1
"""


class LiveSnapshotTest(unittest.TestCase):
    def _parse(self, text):
        s = wf.SyncSnapshot()
        for line in text.strip("\n").splitlines():
            s.feed_line(line)
        return s

    def test_reason_parsed_cleanly(self):
        s = self._parse(SNAPSHOT)
        self.assertEqual(s.reason, "stall")

    def test_host_parked_identity_recovered(self):
        s = self._parse(SNAPSHOT)
        blocked = {t["name"] for t in s.blocked_threads()}
        self.assertIn("Background Job.Worker 0", blocked)
        self.assertIn("Loading.PreloadManager", blocked)

    def test_sema_cycle_detected_with_identities(self):
        s = self._parse(SNAPSHOT)
        cycles = s.cycles()
        self.assertEqual(len(cycles), 1)
        self.assertEqual(set(cycles[0]),
                         {"Loading.PreloadManager", "Background Job.Worker 0"})

    def test_edges_resolve_provider(self):
        s = self._parse(SNAPSHOT)
        edges = {e["thread"]: e for e in s.edges()}
        self.assertEqual(edges["Loading.PreloadManager"]["provider"],
                         "Background Job.Worker 0")
        self.assertEqual(edges["Background Job.Worker 0"]["provider"],
                         "Loading.PreloadManager")

    def test_mutex_owner_cycle(self):
        s = self._parse(MUTEX_SNAPSHOT)
        cycles = s.cycles()
        self.assertEqual(len(cycles), 1)
        self.assertEqual(set(cycles[0]), {"ThreadA", "ThreadB"})

    def test_unknown_identity_is_preserved_not_guessed(self):
        s = self._parse(UNKNOWN_SNAPSHOT)
        edge = s.edges()[0]
        self.assertEqual(edge["provider"], "UNKNOWN")
        # No cycle can be fabricated from an unknown provider.
        self.assertEqual(s.cycles(), [])

    def test_equeue_provider_is_external_not_cycle(self):
        s = self._parse(UNKNOWN_SNAPSHOT)
        # equeue has no provider mapping -> external event source.
        objs = {o["id"]: o for o in s.to_dict()["objects"]}
        self.assertIn("0x200", objs)
        self.assertEqual(objs["0x200"]["kind"], "equeue")

    def test_last_block_wins(self):
        # Two blocks; the second (empty) must replace the first.
        two = SNAPSHOT + "\n[LOADER][DIAG] sync_snapshot.begin reason=manual seq=2\n[LOADER][DIAG] sync_snapshot.end seq=2 threads=0 blocked=0\n"
        s = self._parse(two)
        self.assertEqual(s.threads, [])

    def test_render_smoke(self):
        s = self._parse(SNAPSHOT)
        out = wf.render_snapshot(s)
        self.assertIn("DEADLOCK CYCLES DETECTED", out)
        self.assertIn("Background Job.Worker 0", out)


if __name__ == "__main__":
    unittest.main()
