#!/usr/bin/env python3

from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

VIDEO_OUT = ROOT / "src/SharpEmu.Libs/VideoOut/VideoOutExports.cs"
WRITE_TRACKER = ROOT / "src/SharpEmu.HLE/GuestImageWriteTracker.cs"
VULKAN_PRESENTER = ROOT / "src/SharpEmu.Libs/VideoOut/VulkanVideoPresenter.cs"


def remove_braced_block(text: str, signature: str) -> str:
    start = text.find(signature)
    if start < 0:
        raise RuntimeError(f"cleanup marker not found: {signature}")

    line_start = text.rfind("\n", 0, start) + 1
    brace = text.find("{", start)
    if brace < 0:
        raise RuntimeError(f"opening brace not found: {signature}")

    depth = 0
    index = brace
    while index < len(text):
        char = text[index]
        if char == "{":
            depth += 1
        elif char == "}":
            depth -= 1
            if depth == 0:
                end = index + 1
                while end < len(text) and text[end] in " \t\r":
                    end += 1
                if end < len(text) and text[end] == "\n":
                    end += 1
                return text[:line_start] + text[end:]
        index += 1

    raise RuntimeError(f"closing brace not found: {signature}")


def remove_debug_flip_calls(text: str) -> str:
    lines = text.splitlines(keepends=True)
    output: list[str] = []
    index = 0

    while index < len(lines):
        line = lines[index]
        if "DebugFlip(" not in line:
            output.append(line)
            index += 1
            continue

        # The local DebugFlip method has already been removed. Every remaining
        # occurrence is a temporary diagnostic call; skip through its closing );.
        while index < len(lines):
            current = lines[index]
            index += 1
            if ");" in current:
                break

    return "".join(output)


def cleanup_video_out(text: str) -> str:
    text = text.replace(
        "    private static int _submitFlipDebugCount;\n",
        "",
    )

    debug_counter = (
        "    var debugFlip =\n"
        "        Interlocked.Increment(ref _submitFlipDebugCount) <= 4;\n\n"
    )
    if debug_counter not in text:
        raise RuntimeError("VideoOut debug counter block not found")
    text = text.replace(debug_counter, "", 1)

    text = remove_braced_block(
        text,
        "    void DebugFlip(string text)",
    )
    text = remove_debug_flip_calls(text)

    for forbidden in ("[VPS5][FLIP]", "DebugFlip(", "_submitFlipDebugCount"):
        if forbidden in text:
            raise RuntimeError(f"VideoOut cleanup incomplete: {forbidden}")

    return text


def cleanup_write_tracker(text: str) -> str:
    # Remove the temporary counters we added while diagnosing Windows VEH.
    text = re.sub(
        r"(?m)^\s*private static long _diag[^;]*;\s*\n",
        "",
        text,
    )

    text = remove_braced_block(
        text,
        "public static void FlushFaultDiagnostics()",
    )

    # Remove synthetic counter updates from WarmUp/TryHandleWriteFault while
    # preserving the actual dirty/write-generation logic.
    text = re.sub(
        r"(?ms)^\s*Interlocked\.(?:Increment|Exchange)\(\s*ref _diag[^;]*;\s*\n",
        "",
        text,
    )

    text = re.sub(
        r"(?ms)\n\s*/\*\s*\n"
        r"\s*\* WarmUp intentionally exercises TryHandleWriteFault directly\..*?"
        r"\*/\s*\n",
        "\n",
        text,
    )

    for forbidden in ("[WT][FAULT]", "_diagFault", "_diagEpoch", "FlushFaultDiagnostics"):
        if forbidden in text:
            raise RuntimeError(f"write-tracker cleanup incomplete: {forbidden}")

    # Regression guard: do not accidentally revert the functional Track/Rearm
    # fix that prevents repeated Track() from re-protecting an active CPU surface.
    required = (
        "var shouldArm = false;",
        "Do NOT ArmLocked() on every repeated Track().",
        "public static void Rearm(ulong address)",
    )
    for marker in required:
        if marker not in text:
            raise RuntimeError(f"write-tracker functional marker lost: {marker}")

    return text


def cleanup_vulkan_presenter(text: str) -> str:
    text = text.replace(
        "        private long _cpuDisplayFingerprintRefreshCount;\n",
        "",
    )

    start_marker = "            var traceCount =\n"
    start = text.find(start_marker)
    if start < 0:
        raise RuntimeError("Vulkan CPU-display trace block not found")

    nearby = text[start:start + 500]
    if "_cpuDisplayFingerprintRefreshCount" not in nearby or \
       "[SYNC] cpu-display-refresh" not in nearby:
        raise RuntimeError("unexpected Vulkan trace block near traceCount")

    final_log_line = '                    $"fingerprint=0x{fingerprint:X16}");'
    log_end = text.find(final_log_line, start)
    if log_end < 0:
        raise RuntimeError("Vulkan CPU-display trace end not found")
    log_end += len(final_log_line)

    block_end = text.find("\n            }", log_end)
    if block_end < 0:
        raise RuntimeError("Vulkan CPU-display trace closing brace not found")
    block_end += len("\n            }")

    text = text[:start] + text[block_end:]

    for forbidden in ("[SYNC] cpu-display-refresh", "_cpuDisplayFingerprintRefreshCount"):
        if forbidden in text:
            raise RuntimeError(f"Vulkan cleanup incomplete: {forbidden}")

    # Regression guards for the Windows writable-framebuffer fingerprint path.
    required = (
        "UseCpuDisplayFingerprintSync",
        "ComputeCpuDisplayFingerprint",
        "RefreshCpuDisplayBufferIfChanged",
        "_cpuDisplayFingerprintStates",
    )
    for marker in required:
        if marker not in text:
            raise RuntimeError(f"Vulkan fingerprint fallback marker lost: {marker}")

    return text


def read_source(path: Path) -> str:
    return path.read_text(encoding="utf-8-sig")


def main() -> None:
    # Validate every transform first. Nothing is written unless all three files
    # match the expected diagnostic shapes and all functional regression guards
    # survive the cleanup.
    cleaned = {
        VIDEO_OUT: cleanup_video_out(read_source(VIDEO_OUT)),
        WRITE_TRACKER: cleanup_write_tracker(read_source(WRITE_TRACKER)),
        VULKAN_PRESENTER: cleanup_vulkan_presenter(read_source(VULKAN_PRESENTER)),
    }

    for path, content in cleaned.items():
        path.write_text(content, encoding="utf-8", newline="\n")
        print(f"cleaned: {path.relative_to(ROOT)}")

    print("cleanup complete")
    print("temporary logs removed: [VPS5][FLIP], [WT][FAULT], cpu-display-refresh")

    # This is a one-shot migration helper. Removing itself keeps the final
    # VirtualPS5 branch clean when the user commits the resulting diff.
    Path(__file__).unlink()


if __name__ == "__main__":
    main()
