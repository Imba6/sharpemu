# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""Unit tests for scripts/analyze_run_report.py."""
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

import analyze_run_report as rr  # noqa: E402


SYNTHETIC = """
[LOADER][INFO] Scheduled guest thread 'Loading.PreloadManager' handle=0x0000021D739A7E70 entry=0x0 arg=0x0
[LOADER][INFO] Scheduled guest thread 'UnityGfxDeviceWorker' handle=0x0000021D111 entry=0x0 arg=0x0
[LOADER][WARN] dlsym#1 FAIL handle=0x13 module='PSNCore.prx' symbol='UnityRenderEvent' -> 0x0 callerRip=0x0
[LOADER][WARN] dlsym#2 FAIL handle=0x13 module='PSNCore.prx' symbol='UnityRenderEvent' -> 0x0 callerRip=0x0
[LOADER][WARN] Import#10 unresolved: nid=crb5j7mkk1c ret=0x0 rdi=0x2
[LOADER][WARN] Import#11 unresolved: nid=crb5j7mkk1c ret=0x0 rdi=0x2
[LOADER][INFO] Vulkan VideoOut presented splash: 3840x2160
[LOADER][TRACE] sema.create handle=0x00000086 name='Baselib_SystemSemaphore' attr=0x1 init=0 max=2147483647
[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160
[LOADER][TRACE] sema.signal handle=0x00000086 name='Baselib_SystemSemaphore' signal=1 count=1 waiters=1 guest=0x0000021D739A7E70 ret=0x0000000800D17D53
[LOADER][TRACE] sema.wait-host-wake handle=0x00000086 name='Baselib_SystemSemaphore' need=1 count=0 guest=0x0 ret=0x0800D18129
[LOADER][WARN] Import#20 result: ORBIS_GEN2_ERROR_NOT_FOUND (RpQJJVKTiFM) rdi=0x2 ret=0x0
[LOADER][TRACE] guest_exception.raise target=0x0000021D739A7E70 type=0x1E handler=0x0
""".strip("\n") + "\n" + "".join(
    f"[LOADER][WARN] Import#{100+i} result: ORBIS_GEN2_ERROR_TIMED_OUT (Zxa0VhQVTsk) rdi=0x0000000000000086 rsi=0x1 rdx=0x0 rcx=0x1 ret=0x0000000800D18129\n"
    for i in range(30)
)


class RunReportTest(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.NamedTemporaryFile("w", suffix=".log", delete=False)
        self.tmp.write(SYNTHETIC)
        self.tmp.close()
        self.report = rr.build_report(self.tmp.name, top=15)

    def tearDown(self):
        os.unlink(self.tmp.name)

    def test_basic_counts(self):
        self.assertEqual(self.report["thread_count"], 2)
        self.assertEqual(self.report["first_present"]["kind"], "splash")
        self.assertEqual(self.report["last_present"]["kind"], "first frame")

    def test_missing_symbols_grouped_and_deduped(self):
        psn = self.report["missing_symbols_by_module"]["PSNCore.prx"]
        self.assertEqual(psn[0]["symbol"], "UnityRenderEvent")
        self.assertEqual(psn[0]["count"], 2)

    def test_unresolved_nids(self):
        self.assertEqual(dict(self.report["unresolved_nids"]).get("crb5j7mkk1c"), 2)

    def test_error_codes_and_nids(self):
        codes = dict(self.report["error_codes"])
        self.assertEqual(codes["ORBIS_GEN2_ERROR_TIMED_OUT"], 30)
        self.assertEqual(codes["ORBIS_GEN2_ERROR_NOT_FOUND"], 1)

    def test_exception_fingerprints(self):
        self.assertEqual(dict(self.report["exception_types"]).get("0x1E"), 1)

    def test_stall_candidate_identifies_0x86(self):
        s = self.report["stall_candidate"]
        self.assertIsNotNone(s)
        self.assertEqual(s["handle"], "0x86")
        self.assertTrue(s["signal_equals_wake"])
        self.assertIn("Loading.PreloadManager", [str(x) for x in s["signaler_threads"]])

    def test_first_causal_blocker_is_starvation(self):
        b = self.report["first_causal_blocker"]
        self.assertEqual(b["kind"], "producer-starvation-or-deadlock")

    def test_fatal_line_takes_priority(self):
        # A fatal line should dominate the blocker heuristic.
        with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False) as t:
            t.write(SYNTHETIC + "\n[LOADER][ERROR] __fastfail: UnmanagedCallersOnly from managed code\n")
            name = t.name
        try:
            rep = rr.build_report(name, top=15)
            self.assertEqual(rep["first_causal_blocker"]["kind"], "fatal-or-fault")
        finally:
            os.unlink(name)

    def test_json_serializable_and_text_render(self):
        json.dumps(self.report)
        out = rr.render_text(self.report)
        self.assertIn("FIRST CAUSAL BLOCKER", out)
        self.assertIn("0x86", out)


if __name__ == "__main__":
    unittest.main()
