#!/usr/bin/env python3
# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later
"""
Parser regressions for analyze_stage10b_capture.py, pinned to the real run_stage10b_bad.log
line shapes (Stage 10B). Dependency-free: run `python3 scripts/test_analyze_stage10b_capture.py`.

The first real capture disproved the module_start/equeue hypothesis (every module completed) yet
the run still failed -- the Unity main thread spin-looped scePthreadYield during preload and was
force-unwound by the 5s import-loop guard, then misreported as a native-backend failure. These
tests lock in that the analyzer parses those shapes and no longer calls such a run "GOOD".
"""

import contextlib
import importlib.util
import io
import os
import tempfile
import unittest

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location(
    "analyze_stage10b_capture", os.path.join(_HERE, "analyze_stage10b_capture.py"))
azr = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(azr)


def _parse_text(text):
    with tempfile.NamedTemporaryFile("w", suffix=".log", delete=False) as fh:
        fh.write(text)
        path = fh.name
    try:
        return azr.parse(path), path
    finally:
        pass  # leave file for caller if needed; tempdir is cleaned by the OS


# Real line shapes taken verbatim from run_stage10b_bad.log / run_fix.log.
BAD_GUARD = """\
[LOADER][INFO] === Execute START ===
[LOADER][LOADSTART] enter seq=3 guest_thread=0x0000000809AA0000 path='/app0/PSNCore.prx' handle=18 init=0x0000000809D28010 prior_state=NotStarted
[LOADER][LOADSTART] module_start.begin seq=3 module='PSNCore.prx' init=0x0000000809D28010 guest_thread=0x0000000809AA0000
[LOADER][LOADSTART] module_start.complete seq=3 module='PSNCore.prx' started=True elapsed_ms=1 guest_thread=0x0000000809AA0000
[LOADER][INFO] sceKernelLoadStartModule started 'PSNCore.prx' at 0x0000000809D28010
[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160
Forcing call to sce::Agc::suspendPoint to avoid TRC R4089 breach
Forcing call to sce::Agc::suspendPoint to avoid TRC R4089 breach
[LOADER][ERROR] Import-loop guard fired at import#18371840: nid=T72hz6ffq08 ret=0x00000008006B8B5B -> host_exit=0x000002C747850000
[LOADER][INFO] Guest returned: 1202156176
[LOADER][ERROR] Detected repeating import loop and forced guest unwind to host.
[LOADER][INFO] === Execute END (LastError: null) ===
[DISPATCHER] Native backend FAILED: unknown backend error
[RUNTIME] DispatchEntry returned: ORBIS_GEN2_ERROR_NOT_IMPLEMENTED
[INFO][SharpEmu.CLI] Program.cs:376 Summary: result=ORBIS_GEN2_ERROR_NOT_IMPLEMENTED reason=NativeBackendUnavailable exit=? last_guest_rip=0x0000000800000070
"""

GOOD_RUN = """\
[LOADER][LOADSTART] module_start.begin seq=3 module='PSNCore.prx' init=0x0000000809D28010 guest_thread=0x0000000809AA0000
[LOADER][LOADSTART] module_start.complete seq=3 module='PSNCore.prx' started=True elapsed_ms=1 guest_thread=0x0000000809AA0000
[LOADER][INFO] sceKernelLoadStartModule started 'PSNCore.prx' at 0x0000000809D28010
[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160
""" + "Forcing call to sce::Agc::suspendPoint to avoid TRC R4089 breach\n" * 20

MODULE_STALL = """\
[LOADER][LOADSTART] module_start.begin seq=5 module='PSNCommon.prx' init=0x0000000809B1A010 guest_thread=0x0000000809AA0000
[LOADER][TRACE] equeue.wait-block: handle=0x0000000900112233 depth=0 registrations=1 generation=0x1 waiter=7 capacity=16 timeout=infinite events=0x0 out_count=0x0 thread='PSNWorker' pthread=0x0 gth=0x0000000809AA0000 managed=14 ret=0x0000000809B1C044 frames=[0x0]
"""


class TerminalOutcomeTests(unittest.TestCase):
    def test_guard_fire_capture_is_parsed_and_not_called_good(self):
        events, _ = _parse_text(BAD_GUARD)
        # import-loop guard firing recognized, NID resolved to scePthreadYield.
        self.assertEqual(len(events["guard_fired"]), 1)
        _, imp, nid, ret = events["guard_fired"][0]
        self.assertEqual(nid, "T72hz6ffq08")
        self.assertEqual(azr.NID_NAMES[nid], "scePthreadYield")
        self.assertEqual(imp, "18371840")
        self.assertEqual(len(events["repeat_loop"]), 1)
        self.assertEqual(events["backend_failed"][0][1], "unknown backend error")
        self.assertEqual(events["first_frame"], 1)
        self.assertEqual(events["suspend_point"], 2)  # far below the gameplay threshold
        # analyze_terminal must report NOT-ok (the run failed) despite modules completing.
        with contextlib.redirect_stdout(io.StringIO()):
            ok, reached = azr.analyze_terminal(events)
        self.assertFalse(ok)
        self.assertFalse(reached)
        # And no module_start is stalled (hypothesis disproven, not a loader stall).
        table = azr.build_loadstart_table(events)
        stalled = [s for s in table if table[s].get("begin") and not table[s].get("complete")]
        self.assertEqual(stalled, [])

    def test_good_run_reaches_gameplay(self):
        events, _ = _parse_text(GOOD_RUN)
        self.assertEqual(events["guard_fired"], [])
        self.assertEqual(events["backend_failed"], [])
        self.assertGreaterEqual(events["suspend_point"], 10)
        with contextlib.redirect_stdout(io.StringIO()):
            ok, reached = azr.analyze_terminal(events)
        self.assertTrue(ok)
        self.assertTrue(reached)

    def test_guard_disabled_reaching_gameplay_is_ok(self):
        # Stage-11 A/B SUCCESS shape: flags-off run (no LOADSTART/EQUEUE trace), guard disabled so no
        # guard firing, modules complete via the unconditional 'started' INFO, gameplay reached.
        text = (
            "[LOADER][INFO] sceKernelLoadStartModule started 'PSNCore.prx' at 0x0000000809D28010\n"
            "[LOADER][INFO] sceKernelLoadStartModule started 'SaveData.prx' at 0x0000000809FA1010\n"
            "[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160\n"
            + "Forcing call to sce::Agc::suspendPoint to avoid TRC R4089 breach\n" * 40
        )
        events, _ = _parse_text(text)
        self.assertEqual(events["guard_fired"], [])
        self.assertEqual(len(events["started_info"]), 2)
        with contextlib.redirect_stdout(io.StringIO()):
            ok, reached = azr.analyze_terminal(events)
        self.assertTrue(ok)
        self.assertTrue(reached)

    def test_guard_disabled_still_stalled_is_failure_case(self):
        # Stage-11 A/B FAILURE shape: guard disabled, no guard firing, modules complete, but the run
        # never reaches sustained gameplay -> the guard was only shortening a pre-existing stall.
        text = (
            "[LOADER][INFO] sceKernelLoadStartModule started 'PSNCore.prx' at 0x0000000809D28010\n"
            "[LOADER][INFO] Vulkan VideoOut presented first frame: 3840x2160\n"
            "Forcing call to sce::Agc::suspendPoint to avoid TRC R4089 breach\n"
        )
        events, _ = _parse_text(text)
        self.assertEqual(events["guard_fired"], [])
        with contextlib.redirect_stdout(io.StringIO()):
            ok, reached = azr.analyze_terminal(events)
        self.assertFalse(ok)       # not a success
        self.assertFalse(reached)  # did not reach gameplay -> FAILURE-case branch

    def test_module_stall_is_detected(self):
        events, _ = _parse_text(MODULE_STALL)
        table = azr.build_loadstart_table(events)
        stalled = [s for s in table if table[s].get("begin") and not table[s].get("complete")]
        self.assertEqual(stalled, ["5"])
        # the stalled thread's equeue wait is present and joinable by gth.
        waits = [f for (_, op, f, _) in events["equeue"] if op.startswith("wait")]
        self.assertEqual(azr.norm_handle(waits[0]["gth"]), azr.norm_handle("0x0000000809AA0000"))


if __name__ == "__main__":
    unittest.main(verbosity=2)
