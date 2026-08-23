# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Unit tests for scripts/analyze_semaphore_timeline.py.

Run: python3 -m unittest scripts.test_analyze_semaphore_timeline
 or: python3 scripts/test_analyze_semaphore_timeline.py
"""
import io
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import analyze_semaphore_timeline as az  # noqa: E402


def _feed(analyzer: az.Analyzer, text: str, source: str = "synthetic.log") -> None:
    for i, line in enumerate(text.strip("\n").splitlines(), start=1):
        analyzer.feed_line(i, source, line)


# A minimal synthetic log exercising the full vocabulary. Handle 0x86 is the
# "producer/consumer handshake" under study; it gets one signal + wake then a
# permanent timeout flood. Handle 0x40 is an unrelated healthy semaphore.
SYNTHETIC = """
[LOADER][INFO] Scheduled guest thread 'Loading.PreloadManager' handle=0x00000021D739A7E70 entry=0x0800DD4460 arg=0x0
[LOADER][TRACE] sema.create handle=0x00000086 name='Baselib_SystemSemaphore' attr=0x1 init=0 max=2147483647
[LOADER][TRACE] sema.create handle=0x00000040 name='ResumeSemaphore' attr=0x0 init=0 max=256
[LOADER][TRACE] sema.wait-host-block handle=0x00000086 name='Baselib_SystemSemaphore' need=1 count=0 timeout=1000 guest=0x0 ret=0x0800D18129
[LOADER][WARN] Import#100 result: ORBIS_GEN2_ERROR_TIMED_OUT (Zxa0VhQVTsk) rdi=0x0000000000000086 rsi=0x1 rdx=0x7fff rcx=0x1 ret=0x0000000800D18129
[LOADER][TRACE] sema.signal handle=0x00000086 name='Baselib_SystemSemaphore' signal=1 count=1 waiters=1 guest=0x00000021D739A7E70 ret=0x0000000800D17D53
[LOADER][TRACE] sema.wait-host-wake handle=0x00000086 name='Baselib_SystemSemaphore' need=1 count=0 guest=0x0 ret=0x0800D18129
[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160
[LOADER][TRACE] sema.wait-host-block handle=0x00000086 name='Baselib_SystemSemaphore' need=1 count=0 timeout=1000 guest=0x0 ret=0x0800D18129
[LOADER][WARN] Import#200 result: ORBIS_GEN2_ERROR_TIMED_OUT (Zxa0VhQVTsk) rdi=0x0000000000000086 rsi=0x1 rdx=0x7fff rcx=0x1 ret=0x0000000800D18129
[LOADER][WARN] Import#300 result: ORBIS_GEN2_ERROR_TIMED_OUT (Zxa0VhQVTsk) rdi=0x0000000000000086 rsi=0x1 rdx=0x7fff rcx=0x1 ret=0x0000000800D18129
[LOADER][WARN] Import#400 result: ORBIS_GEN2_ERROR_TIMED_OUT (Zxa0VhQVTsk) rdi=0x0000000000000086 rsi=0x1 rdx=0x7fff rcx=0x1 ret=0x0000000800D18129
[LOADER][TRACE] sema.signal handle=0x00000040 name='ResumeSemaphore' signal=1 count=1 waiters=1 guest=0x00000021D739A7E70 ret=0x0805D5463B
[LOADER][TRACE] guest_exception.raise target=0x00000021D739A7E70 type=0x1E handler=0x08082E5210
[LOADER][TRACE] Forcing call to sce::Agc::suspendPoint to avoid TRC R4089 breach
"""


class ParseHelpersTest(unittest.TestCase):
    def test_norm_hex_strips_leading_zeros(self):
        self.assertEqual(az._norm_hex("0x00000086"), "0x86")
        self.assertEqual(az._norm_hex("0x0000000800D18129"), "0x800d18129")
        self.assertEqual(az._norm_hex("0x0"), "0x0")

    def test_norm_hex_passthrough_non_hex(self):
        self.assertEqual(az._norm_hex("infinite"), "infinite")
        self.assertIsNone(az._norm_hex(None))

    def test_parse_kv_quoted_name(self):
        kv = az._parse_kv("handle=0x86 name='Baselib_SystemSemaphore' init=0 max=2147483647")
        self.assertEqual(kv["name"], "Baselib_SystemSemaphore")
        self.assertEqual(kv["handle"], "0x86")
        self.assertEqual(kv["max"], "2147483647")


class TargetedTimelineTest(unittest.TestCase):
    def setUp(self):
        self.az = az.Analyzer(target_handle="0x86")
        _feed(self.az, SYNTHETIC)
        self.d = self.az.timelines["0x86"].to_dict()

    def test_creation_metadata(self):
        self.assertEqual(self.d["handle"], "0x86")
        self.assertIn("Baselib_SystemSemaphore", self.d["names"])
        self.assertEqual(self.d["init"], "0")
        self.assertEqual(self.d["max"], "2147483647")
        self.assertEqual(self.d["attr"], "0x1")

    def test_counts(self):
        c = self.d["counts"]
        self.assertEqual(c["signal"], 1)
        self.assertEqual(c["wake"], 1)
        self.assertEqual(c["wait_host_block"], 2)
        # 4 timeouts attributed via rdi=0x86 import-result lines
        self.assertEqual(c["timeout"], 4)

    def test_signal_and_wake_balanced(self):
        # The crux invariant used in the 0x86 diagnosis: signal==wake => no lost
        # wakeup; the stall is producer starvation, not a dropped signal.
        self.assertEqual(self.d["counts"]["signal"], self.d["counts"]["wake"])

    def test_waiter_and_signaler_rips(self):
        self.assertIn("0x800d18129", self.d["waiter_rips"])
        self.assertIn("0x800d17d53", self.d["signaler_rips"])

    def test_signaler_guest_mapped_to_thread_name(self):
        # guest pointer on the signal maps to a scheduled thread name.
        self.assertIn("0x21d739a7e70", self.d["signaler_guests"])
        self.assertEqual(
            self.az.thread_names.get("0x21d739a7e70"), "Loading.PreloadManager"
        )

    def test_landmarks(self):
        self.assertIsNotNone(self.d["first_signal_line"])
        self.assertEqual(self.d["first_signal_line"], self.d["last_signal_line"])
        # last progress = the wake following the single signal
        self.assertIsNotNone(self.d["last_progress_line"])

    def test_longest_timeout_streak(self):
        streak = self.d["longest_timeout_streak"]
        # three consecutive import-result timeouts after the wake
        self.assertEqual(streak["length"], 3)

    def test_unrelated_semaphore_not_captured_under_target(self):
        # Only 0x86 should be present when targeting 0x86.
        self.assertEqual(set(self.az.timelines.keys()), {"0x86"})


class ListModeTest(unittest.TestCase):
    def test_list_mode_captures_all_semaphores(self):
        a = az.Analyzer()  # no target -> aggregate all
        _feed(a, SYNTHETIC)
        self.assertIn("0x86", a.timelines)
        self.assertIn("0x40", a.timelines)

    def test_global_markers(self):
        a = az.Analyzer()
        _feed(a, SYNTHETIC)
        s = a.summary_dict()
        self.assertEqual(s["suspendpoint_count"], 1)
        self.assertEqual(s["guest_exception_count"], 1)
        self.assertEqual(len(s["present_markers"]), 1)
        self.assertEqual(s["max_import"], 400)


class NameAndCallerTargetTest(unittest.TestCase):
    def test_target_by_name(self):
        a = az.Analyzer(target_name="ResumeSemaphore")
        _feed(a, SYNTHETIC)
        self.assertIn("0x40", a.timelines)
        self.assertNotIn("0x86", a.timelines)

    def test_target_by_caller_rip(self):
        # The waiter caller RIP 0x800d18129 belongs to the 0x86 handshake.
        a = az.Analyzer(target_caller="0x800D18129")
        _feed(a, SYNTHETIC)
        # timeouts + host-block waits carry that ret; they attribute to 0x86.
        self.assertIn("0x86", a.timelines)
        self.assertGreaterEqual(a.timelines["0x86"].n_timeout, 1)


class RenderSmokeTest(unittest.TestCase):
    def test_text_render_targeted(self):
        a = az.Analyzer(target_handle="0x86")
        _feed(a, SYNTHETIC)
        out = az.render_text(a, ["synthetic.log"], context=0)
        self.assertIn("SEMAPHORE 0x86", out)
        self.assertIn("signal", out)
        self.assertIn("0x800d17d53", out)

    def test_text_render_list_mode(self):
        a = az.Analyzer()
        _feed(a, SYNTHETIC)
        out = az.render_text(a, ["synthetic.log"], context=0)
        self.assertIn("All semaphores", out)

    def test_json_summary_is_serializable(self):
        import json
        a = az.Analyzer(target_handle="0x86")
        _feed(a, SYNTHETIC)
        json.dumps(a.summary_dict())  # must not raise


if __name__ == "__main__":
    unittest.main()
