#!/usr/bin/env python3

from __future__ import annotations

import argparse
import base64
import hashlib
import json
import re
import shutil
import subprocess
from collections import Counter
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
NID_SUFFIX = bytes.fromhex("518d64a635ded8c1e6b039b1c3e55230")

IMPORT_RE = re.compile(
    r"\[LOADER\] Import name resolved to NID: (?P<name>.+?) -> (?P<nid>\S+)"
)
VPS5_EXPORT_RE = re.compile(
    r"\[VPS5\] HLE export: (?P<library>[^:]+):(?P<name>.+?) nid=(?P<nid>\S+)"
)
DLSYM_FAILED_RE = re.compile(
    r"sceKernelDlsym failed:.*?symbol='(?P<name>[^']+)'"
)
BOOTSTRAP_UNRESOLVED_RE = re.compile(
    r"bootstrap_bridge unresolved:.*?symbol='(?P<name>[^']+)'"
)
DATA_REBIND_RE = re.compile(
    r"Imported data rebind: rebound=(?P<rebound>\d+), unresolved=(?P<unresolved>\d+)"
)
GENERATION_RE = re.compile(r"\[LOADER\] Generation: (?P<generation>\S+)")
STALL_IMPORT_RE = re.compile(
    r"\[LOADER\]\[ERROR\] Stall import-stub:.*?nid=(?P<nid>\S+)"
)

SYSABI_ATTRIBUTE_RE = re.compile(r"\[SysAbiExport\s*\((.*?)\)\s*\]", re.DOTALL)
EXPORT_NAME_RE = re.compile(r'ExportName\s*=\s*"([^"]+)"')
LIBRARY_NAME_RE = re.compile(r'LibraryName\s*=\s*"([^"]+)"')
TARGET_RE = re.compile(r"Target\s*=\s*Generation\.(Gen\d+)")

READ_ELF_SYMBOL_RE = re.compile(
    r"^\s*\d+:\s+[0-9A-Fa-f]+\s+\d+\s+"
    r"(?P<type>\w+)\s+\w+\s+\w+\s+UND\s+(?P<name>\S+)\s*$"
)
NEEDED_RE = re.compile(r"Shared library: \[(?P<library>[^\]]+)\]")


def compute_nid(export_name: str) -> str:
    digest = hashlib.sha1(export_name.encode("utf-8") + NID_SUFFIX).digest()
    encoded = base64.b64encode(digest[:8][::-1]).decode("ascii")
    return encoded.rstrip("=").replace("/", "-")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Build a VirtualPS5 compatibility backlog from a real ELF and runtime log."
    )
    parser.add_argument("--elf", required=True, type=Path, help="Target ELF/eboot path")
    parser.add_argument("--log", type=Path, help="real-run log produced with tee")
    parser.add_argument("--output", type=Path, help="JSON output path")
    parser.add_argument(
        "--print-all",
        action="store_true",
        help="Print implemented imports too; default console view focuses on work remaining",
    )
    return parser.parse_args()


def find_readelf() -> str:
    for candidate in ("llvm-readelf-18", "llvm-readelf", "readelf"):
        resolved = shutil.which(candidate)
        if resolved:
            return resolved
    raise RuntimeError("llvm-readelf-18/llvm-readelf/readelf was not found in PATH")


def run_readelf(elf: Path, *args: str) -> str:
    command = [find_readelf(), *args, str(elf)]
    result = subprocess.run(
        command,
        check=True,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=True,
        encoding="utf-8",
        errors="replace",
    )
    return result.stdout


def read_elf_metadata(elf: Path) -> tuple[dict[str, str], list[str]]:
    symbols: dict[str, str] = {}
    for line in run_readelf(elf, "-Ws").splitlines():
        match = READ_ELF_SYMBOL_RE.match(line)
        if not match:
            continue

        name = match.group("name").split("@", 1)[0]
        symbol_type = match.group("type")
        if name and name != "0":
            symbols.setdefault(name, symbol_type)

    libraries: list[str] = []
    for line in run_readelf(elf, "-d").splitlines():
        match = NEEDED_RE.search(line)
        if match:
            libraries.append(match.group("library"))

    return symbols, libraries


def scan_sysabi_exports(root: Path) -> dict[str, dict[str, str]]:
    exports: dict[str, dict[str, str]] = {}
    if not root.exists():
        return exports

    for path in root.rglob("*.cs"):
        try:
            text = path.read_text(encoding="utf-8-sig", errors="replace")
        except OSError:
            continue

        for attribute in SYSABI_ATTRIBUTE_RE.findall(text):
            name_match = EXPORT_NAME_RE.search(attribute)
            if not name_match:
                continue

            name = name_match.group(1)
            library_match = LIBRARY_NAME_RE.search(attribute)
            target_match = TARGET_RE.search(attribute)
            exports.setdefault(
                name,
                {
                    "library": library_match.group(1) if library_match else "unknown",
                    "generation": target_match.group(1) if target_match else "unspecified",
                    "source": str(path.relative_to(ROOT)).replace("\\", "/"),
                },
            )

    return exports


def infer_library(name: str, needed: list[str]) -> str:
    prefixes = (
        ("sceSystemService", "libSceSystemService"),
        ("sceUserService", "libSceUserService"),
        ("sceVideoOut", "libSceVideoOut"),
        ("sceAudioOut", "libSceAudioOut"),
        ("sceKeyboard", "libSceKeyboard"),
        ("sceImeDialog", "libSceImeDialog"),
        ("sceRemoteplay", "libSceRemoteplay"),
        ("sceRegMgr", "libSceRegMgr"),
        ("scePad", "libScePad"),
        ("sceKernel", "libKernel"),
    )
    for prefix, library in prefixes:
        if name.startswith(prefix):
            return library

    if "libSceLibcInternal.sprx" in needed:
        return "libSceLibcInternal"
    return "unknown"


def parse_log(log_path: Path | None) -> dict[str, object]:
    if log_path is None or not log_path.exists():
        return {
            "text": "",
            "imports": {},
            "vps5_exports": {},
            "dynamic_failures": Counter(),
            "generation": None,
            "data_rebound": None,
            "data_unresolved": None,
            "stall_import": None,
        }

    text = log_path.read_text(encoding="utf-8", errors="replace")

    imports: dict[str, str] = {}
    for match in IMPORT_RE.finditer(text):
        imports[match.group("name")] = match.group("nid")

    vps5_exports: dict[str, dict[str, str]] = {}
    for match in VPS5_EXPORT_RE.finditer(text):
        vps5_exports[match.group("name")] = {
            "library": match.group("library"),
            "nid": match.group("nid"),
        }

    dynamic_failures: Counter[str] = Counter()
    for regex in (DLSYM_FAILED_RE, BOOTSTRAP_UNRESOLVED_RE):
        for match in regex.finditer(text):
            dynamic_failures[match.group("name")] += 1

    generation_match = GENERATION_RE.search(text)
    data_matches = list(DATA_REBIND_RE.finditer(text))
    data_match = data_matches[-1] if data_matches else None
    stall_matches = list(STALL_IMPORT_RE.finditer(text))

    return {
        "text": text,
        "imports": imports,
        "vps5_exports": vps5_exports,
        "dynamic_failures": dynamic_failures,
        "generation": generation_match.group("generation") if generation_match else None,
        "data_rebound": int(data_match.group("rebound")) if data_match else None,
        "data_unresolved": int(data_match.group("unresolved")) if data_match else None,
        "stall_import": stall_matches[-1].group("nid") if stall_matches else None,
    }


def status_for_static_import(
    name: str,
    symbol_type: str,
    vps5_exports: dict[str, dict[str, str]],
    vps5_source_exports: dict[str, dict[str, str]],
    sharpemu_exports: dict[str, dict[str, str]],
    unresolved_data_count: int | None,
) -> tuple[str, str | None, dict[str, str] | None]:
    if name in vps5_exports:
        return "implemented", "virtualps5", vps5_exports[name]
    if name in vps5_source_exports:
        return "implemented", "virtualps5", vps5_source_exports[name]
    if name in sharpemu_exports:
        return "implemented", "sharpemu", sharpemu_exports[name]

    if symbol_type == "OBJECT":
        if unresolved_data_count:
            return "unresolved-data", None, None
        return "unknown-data", None, None

    return "missing", None, None


def build_report(elf: Path, log_path: Path | None) -> dict[str, object]:
    elf_symbols, needed = read_elf_metadata(elf)
    log = parse_log(log_path)

    runtime_imports: dict[str, str] = log["imports"]  # type: ignore[assignment]
    vps5_runtime_exports: dict[str, dict[str, str]] = log["vps5_exports"]  # type: ignore[assignment]
    dynamic_failures: Counter[str] = log["dynamic_failures"]  # type: ignore[assignment]

    vps5_source_exports = scan_sysabi_exports(ROOT / "src/VirtualPS5.HLE")

    sharpemu_exports: dict[str, dict[str, str]] = {}
    for source_root in (
        ROOT / "src/SharpEmu.HLE",
        ROOT / "src/SharpEmu.Libs",
    ):
        for name, metadata in scan_sysabi_exports(source_root).items():
            sharpemu_exports.setdefault(name, metadata)

    all_import_names = set(elf_symbols) | set(runtime_imports)
    imports: list[dict[str, object]] = []

    for name in sorted(all_import_names):
        symbol_type = elf_symbols.get(name, "UNKNOWN")
        status, provider, implementation = status_for_static_import(
            name,
            symbol_type,
            vps5_runtime_exports,
            vps5_source_exports,
            sharpemu_exports,
            log["data_unresolved"],  # type: ignore[arg-type]
        )

        library = infer_library(name, needed)
        if implementation and implementation.get("library") not in (None, "unknown"):
            library = implementation["library"]

        priority = 100
        if status == "missing":
            priority = 20 if name.startswith("sce") else 40
        elif status in ("unresolved-data", "unknown-data"):
            priority = 30

        entry: dict[str, object] = {
            "name": name,
            "nid": runtime_imports.get(name) or compute_nid(name),
            "library": library,
            "kind": "data" if symbol_type == "OBJECT" else "function",
            "symbol_type": symbol_type,
            "status": status,
            "priority": priority,
        }
        if provider:
            entry["provider"] = provider
        if implementation and implementation.get("source"):
            entry["implementation_source"] = implementation["source"]
        imports.append(entry)

    dynamic: list[dict[str, object]] = []
    for name, count in dynamic_failures.most_common():
        provider = None
        implementation = None
        if name in vps5_source_exports:
            provider = "virtualps5"
            implementation = vps5_source_exports[name]
        elif name in sharpemu_exports:
            provider = "sharpemu"
            implementation = sharpemu_exports[name]

        status = "implemented" if provider else "blocking"
        entry: dict[str, object] = {
            "name": name,
            "nid": runtime_imports.get(name) or compute_nid(name),
            "library": (
                implementation.get("library", "unknown")
                if implementation
                else infer_library(name, needed)
            ),
            "kind": "dynamic-symbol",
            "status": status,
            "priority": 0 if status == "blocking" else 100,
            "requests": count,
        }
        if provider:
            entry["provider"] = provider
        if implementation and implementation.get("source"):
            entry["implementation_source"] = implementation["source"]
        dynamic.append(entry)

    status_counts = Counter(str(item["status"]) for item in imports)
    provider_counts = Counter(
        str(item["provider"])
        for item in imports
        if item.get("provider") is not None
    )

    blockers = [item for item in dynamic if item["status"] == "blocking"]

    return {
        "schema_version": 1,
        "target": {
            "name": elf.name,
            "path": str(elf),
            "generation": log["generation"],
            "needed_libraries": needed,
        },
        "runtime": {
            "log": str(log_path) if log_path else None,
            "static_nids_resolved": len(runtime_imports),
            "imported_data_rebound": log["data_rebound"],
            "imported_data_unresolved": log["data_unresolved"],
            "stall_import_stub": log["stall_import"],
        },
        "summary": {
            "imports_total": len(imports),
            "implemented_virtualps5": provider_counts.get("virtualps5", 0),
            "implemented_sharpemu": provider_counts.get("sharpemu", 0),
            "missing": status_counts.get("missing", 0),
            "unresolved_data": status_counts.get("unresolved-data", 0),
            "unknown_data": status_counts.get("unknown-data", 0),
            "blocking": len(blockers),
        },
        "blockers": blockers,
        "dynamic_requests": dynamic,
        "imports": imports,
    }


def print_report(report: dict[str, object], print_all: bool) -> None:
    target = report["target"]
    summary = report["summary"]
    runtime = report["runtime"]

    assert isinstance(target, dict)
    assert isinstance(summary, dict)
    assert isinstance(runtime, dict)

    print()
    print("========================================")
    print(" VirtualPS5 compatibility scan")
    print("========================================")
    print(f" Target:      {target.get('name')}")
    print(f" Generation:  {target.get('generation') or 'unknown'}")
    print(f" Imports:     {summary.get('imports_total')}")
    print(f" VPS5:        {summary.get('implemented_virtualps5')}")
    print(f" SharpEmu:    {summary.get('implemented_sharpemu')}")
    print(f" Missing:     {summary.get('missing')}")
    print(f" Data miss:   {summary.get('unresolved_data')}")
    print(f" Blockers:    {summary.get('blocking')}")
    if runtime.get("stall_import_stub"):
        print(f" Stall stub:  {runtime.get('stall_import_stub')}")
    print("========================================")

    blockers = report["blockers"]
    assert isinstance(blockers, list)
    if blockers:
        print("\nBLOCKING NOW")
        for item in blockers:
            assert isinstance(item, dict)
            print(
                f"  [P{item['priority']}] {item['library']}:{item['name']} "
                f"nid={item.get('nid')} "
                f"({item['kind']}, requests={item.get('requests', 0)})"
            )

    imports = report["imports"]
    assert isinstance(imports, list)
    remaining = [
        item
        for item in imports
        if isinstance(item, dict)
        and (print_all or item.get("status") != "implemented")
    ]

    if remaining:
        print("\nBACKLOG")
        current_library = None
        for item in sorted(
            remaining,
            key=lambda value: (
                int(value.get("priority", 100)),
                str(value.get("library", "unknown")),
                str(value.get("name", "")),
            ),
        ):
            library = str(item.get("library", "unknown"))
            if library != current_library:
                print(f"\n[{library}]")
                current_library = library

            marker = "✓" if item.get("status") == "implemented" else "✗"
            provider = f" via {item['provider']}" if item.get("provider") else ""
            nid = f" nid={item['nid']}" if item.get("nid") else ""
            print(
                f"  {marker} P{item.get('priority')} {item.get('name')} "
                f"[{item.get('status')}]{provider}{nid}"
            )


def main() -> int:
    args = parse_args()
    elf = args.elf.resolve()
    log_path = args.log.resolve() if args.log else None

    if not elf.is_file():
        raise SystemExit(f"ELF not found: {elf}")
    if log_path is not None and not log_path.is_file():
        raise SystemExit(f"log not found: {log_path}")

    output = (
        args.output.resolve()
        if args.output
        else elf.parent / "missing-nids.json"
    )

    report = build_report(elf, log_path)
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(
        json.dumps(report, indent=2, ensure_ascii=False) + "\n",
        encoding="utf-8",
    )

    print_report(report, args.print_all)
    print(f"\nJSON: {output}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
