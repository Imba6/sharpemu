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
NAMES_FILE = ROOT / "scripts" / "ps5_names.txt"

NID_SUFFIX = bytes.fromhex("518d64a635ded8c1e6b039b1c3e55230")
NID_PATTERN = re.compile(r"^[A-Za-z0-9+\-]{11}$")

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

GENERATION_RE = re.compile(
    r"\[LOADER\] Generation: (?P<generation>\S+)"
)

STALL_IMPORT_RE = re.compile(
    r"\[LOADER\]\[ERROR\] Stall import-stub:.*?nid=(?P<nid>\S+)"
)

SYSABI_ATTRIBUTE_RE = re.compile(
    r"\[SysAbiExport\s*\((.*?)\)\s*\]",
    re.DOTALL,
)

EXPORT_NAME_RE = re.compile(
    r'ExportName\s*=\s*"([^"]+)"'
)

LIBRARY_NAME_RE = re.compile(
    r'LibraryName\s*=\s*"([^"]+)"'
)

TARGET_RE = re.compile(
    r"Target\s*=\s*Generation\.(Gen\d+)"
)

NID_VALUE_RE = re.compile(
    r'Nid\s*=\s*"([^"]+)"'
)

READ_ELF_SYMBOL_RE = re.compile(
    r"^\s*\d+:\s+[0-9A-Fa-f]+\s+\d+\s+"
    r"(?P<type>\w+)\s+\w+\s+\w+\s+UND\s+(?P<name>\S+)\s*$"
)

NEEDED_RE = re.compile(
    r"Shared library: \[(?P<library>[^\]]+)\]"
)


def compute_nid(export_name: str) -> str:
    digest = hashlib.sha1(
        export_name.encode("utf-8") + NID_SUFFIX
    ).digest()

    encoded = base64.b64encode(
        digest[:8][::-1]
    ).decode("ascii")

    return encoded.rstrip("=").replace("/", "-")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Build a VirtualPS5 compatibility backlog "
            "from a real ELF and runtime log."
        )
    )

    parser.add_argument(
        "--elf",
        required=True,
        type=Path,
        help="Target ELF/eboot path",
    )

    parser.add_argument(
        "--log",
        type=Path,
        help="real-run log produced with tee",
    )

    parser.add_argument(
        "--output",
        type=Path,
        help="JSON output path",
    )

    parser.add_argument(
        "--print-all",
        action="store_true",
        help="Print implemented imports too",
    )

    return parser.parse_args()


def find_readelf() -> str:
    for candidate in (
        "llvm-readelf-18",
        "llvm-readelf",
        "readelf",
    ):
        resolved = shutil.which(candidate)

        if resolved:
            return resolved

    raise RuntimeError(
        "llvm-readelf-18/llvm-readelf/readelf was not found in PATH"
    )


def run_readelf(
    elf: Path,
    *args: str,
) -> str:
    command = [
        find_readelf(),
        *args,
        str(elf),
    ]

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


def read_elf_metadata(
    elf: Path,
) -> tuple[dict[str, str], list[str]]:

    symbols: dict[str, str] = {}

    for line in run_readelf(
        elf,
        "-Ws",
    ).splitlines():

        match = READ_ELF_SYMBOL_RE.match(line)

        if not match:
            continue

        name = match.group("name").split("@", 1)[0]
        symbol_type = match.group("type")

        if name and name != "0":
            symbols.setdefault(
                name,
                symbol_type,
            )

    libraries: list[str] = []

    for line in run_readelf(
        elf,
        "-d",
    ).splitlines():

        match = NEEDED_RE.search(line)

        if match:
            libraries.append(
                match.group("library")
            )

    return symbols, libraries


def read_name_catalog() -> tuple[
    dict[str, str],
    dict[str, str],
]:
    if not NAMES_FILE.is_file():
        raise RuntimeError(
            f"PS5 export-name catalog not found: {NAMES_FILE}"
        )

    name_to_nid: dict[str, str] = {}
    nid_to_name: dict[str, str] = {}

    text = NAMES_FILE.read_text(
        encoding="utf-8",
        errors="replace",
    )

    for line in text.splitlines():

        name = line.strip()

        if not name:
            continue

        if name.startswith("#"):
            continue

        nid = compute_nid(name)

        name_to_nid.setdefault(
            name,
            nid,
        )

        nid_to_name.setdefault(
            nid,
            name,
        )

    return name_to_nid, nid_to_name


def parse_decorated_symbol(
    raw_name: str,
) -> tuple[
    str | None,
    list[str],
]:
    """
    SharpProspero imports look like:

        hv1luiJrqQM#D#D
        xk0AcarP3V4#D#D

    The first 11 characters are already the real PS5 NID.
    """

    parts = raw_name.split("#")

    if (
        len(parts) >= 2
        and NID_PATTERN.fullmatch(parts[0])
    ):
        return parts[0], parts[1:]

    if NID_PATTERN.fullmatch(raw_name):
        return raw_name, []

    return None, []


def scan_sysabi_exports(
    root: Path,
) -> tuple[
    dict[str, dict[str, str]],
    dict[str, dict[str, str]],
]:

    by_name: dict[str, dict[str, str]] = {}
    by_nid: dict[str, dict[str, str]] = {}

    if not root.exists():
        return by_name, by_nid

    for path in root.rglob("*.cs"):

        try:
            text = path.read_text(
                encoding="utf-8-sig",
                errors="replace",
            )

        except OSError:
            continue

        for attribute in SYSABI_ATTRIBUTE_RE.findall(text):

            name_match = EXPORT_NAME_RE.search(attribute)

            if not name_match:
                continue

            name = name_match.group(1)

            nid_match = NID_VALUE_RE.search(attribute)
            library_match = LIBRARY_NAME_RE.search(attribute)
            target_match = TARGET_RE.search(attribute)

            nid = (
                nid_match.group(1)
                if nid_match
                else compute_nid(name)
            )

            metadata = {
                "name": name,
                "nid": nid,
                "library": (
                    library_match.group(1)
                    if library_match
                    else "unknown"
                ),
                "generation": (
                    target_match.group(1)
                    if target_match
                    else "unspecified"
                ),
                "source": str(
                    path.relative_to(ROOT)
                ).replace("\\", "/"),
            }

            by_name.setdefault(
                name,
                metadata,
            )

            by_nid.setdefault(
                nid,
                metadata,
            )

    return by_name, by_nid


def merge_exports(
    destination_name: dict[str, dict[str, str]],
    destination_nid: dict[str, dict[str, str]],
    source_name: dict[str, dict[str, str]],
    source_nid: dict[str, dict[str, str]],
) -> None:

    for key, value in source_name.items():
        destination_name.setdefault(
            key,
            value,
        )

    for key, value in source_nid.items():
        destination_nid.setdefault(
            key,
            value,
        )


def infer_library(
    name: str,
    needed: list[str],
) -> str:

    prefixes = (
        (
            "sceSystemService",
            "libSceSystemService",
        ),
        (
            "sceUserService",
            "libSceUserService",
        ),
        (
            "sceVideoOut",
            "libSceVideoOut",
        ),
        (
            "sceAudioOut",
            "libSceAudioOut",
        ),
        (
            "sceKeyboard",
            "libSceKeyboard",
        ),
        (
            "sceImeDialog",
            "libSceImeDialog",
        ),
        (
            "sceRemoteplay",
            "libSceRemoteplay",
        ),
        (
            "sceRegMgr",
            "libSceRegMgr",
        ),
        (
            "scePad",
            "libScePad",
        ),
        (
            "sceKernel",
            "libKernel",
        ),
        (
            "scePthread",
            "libKernel",
        ),
        (
            "pthread_",
            "libKernel",
        ),
    )

    for prefix, library in prefixes:

        if name.startswith(prefix):
            return library

    if "libc.prx" in needed:
        return "libc"

    if "libSceLibcInternal.sprx" in needed:
        return "libSceLibcInternal"

    return "unknown"


def parse_log(
    log_path: Path | None,
) -> dict[str, object]:

    if (
        log_path is None
        or not log_path.exists()
    ):
        return {
            "text": "",
            "imports": {},
            "vps5_exports_by_name": {},
            "vps5_exports_by_nid": {},
            "dynamic_failures": Counter(),
            "generation": None,
            "data_rebound": None,
            "data_unresolved": None,
            "stall_import": None,
        }

    text = log_path.read_text(
        encoding="utf-8",
        errors="replace",
    )

    imports: dict[str, str] = {}

    for match in IMPORT_RE.finditer(text):
        imports[
            match.group("name")
        ] = match.group("nid")

    vps5_exports_by_name: dict[
        str,
        dict[str, str],
    ] = {}

    vps5_exports_by_nid: dict[
        str,
        dict[str, str],
    ] = {}

    for match in VPS5_EXPORT_RE.finditer(text):

        metadata = {
            "name": match.group("name"),
            "library": match.group("library"),
            "nid": match.group("nid"),
        }

        vps5_exports_by_name[
            match.group("name")
        ] = metadata

        vps5_exports_by_nid[
            match.group("nid")
        ] = metadata

    dynamic_failures: Counter[str] = Counter()

    for regex in (
        DLSYM_FAILED_RE,
        BOOTSTRAP_UNRESOLVED_RE,
    ):

        for match in regex.finditer(text):
            dynamic_failures[
                match.group("name")
            ] += 1

    generation_match = GENERATION_RE.search(text)

    data_matches = list(
        DATA_REBIND_RE.finditer(text)
    )

    data_match = (
        data_matches[-1]
        if data_matches
        else None
    )

    stall_matches = list(
        STALL_IMPORT_RE.finditer(text)
    )

    return {
        "text": text,
        "imports": imports,
        "vps5_exports_by_name": (
            vps5_exports_by_name
        ),
        "vps5_exports_by_nid": (
            vps5_exports_by_nid
        ),
        "dynamic_failures": dynamic_failures,
        "generation": (
            generation_match.group("generation")
            if generation_match
            else None
        ),
        "data_rebound": (
            int(data_match.group("rebound"))
            if data_match
            else None
        ),
        "data_unresolved": (
            int(data_match.group("unresolved"))
            if data_match
            else None
        ),
        "stall_import": (
            stall_matches[-1].group("nid")
            if stall_matches
            else None
        ),
    }


def find_implementation(
    name: str,
    nid: str,
    by_name: dict[str, dict[str, str]],
    by_nid: dict[str, dict[str, str]],
) -> dict[str, str] | None:

    return (
        by_nid.get(nid)
        or by_name.get(name)
    )


def normalize_static_symbol(
    raw_name: str,
    runtime_imports: dict[str, str],
    nid_to_name: dict[str, str],
) -> tuple[
    str,
    str,
    list[str],
    bool,
]:

    encoded_nid, suffix = (
        parse_decorated_symbol(raw_name)
    )

    if encoded_nid:

        name = nid_to_name.get(
            encoded_nid,
            encoded_nid,
        )

        return (
            name,
            encoded_nid,
            suffix,
            True,
        )

    nid = (
        runtime_imports.get(raw_name)
        or compute_nid(raw_name)
    )

    return (
        raw_name,
        nid,
        [],
        False,
    )


def status_for_static_import(
    name: str,
    nid: str,
    symbol_type: str,
    vps5_runtime_by_name: dict[str, dict[str, str]],
    vps5_runtime_by_nid: dict[str, dict[str, str]],
    vps5_source_by_name: dict[str, dict[str, str]],
    vps5_source_by_nid: dict[str, dict[str, str]],
    sharpemu_by_name: dict[str, dict[str, str]],
    sharpemu_by_nid: dict[str, dict[str, str]],
    unresolved_data_count: int | None,
) -> tuple[
    str,
    str | None,
    dict[str, str] | None,
]:

    implementation = find_implementation(
        name,
        nid,
        vps5_runtime_by_name,
        vps5_runtime_by_nid,
    )

    if implementation:
        return (
            "implemented",
            "virtualps5",
            implementation,
        )

    implementation = find_implementation(
        name,
        nid,
        vps5_source_by_name,
        vps5_source_by_nid,
    )

    if implementation:
        return (
            "implemented",
            "virtualps5",
            implementation,
        )

    implementation = find_implementation(
        name,
        nid,
        sharpemu_by_name,
        sharpemu_by_nid,
    )

    if implementation:
        return (
            "implemented",
            "sharpemu",
            implementation,
        )

    if symbol_type == "OBJECT":

        if unresolved_data_count:
            return (
                "unresolved-data",
                None,
                None,
            )

        return (
            "unknown-data",
            None,
            None,
        )

    return (
        "missing",
        None,
        None,
    )


def build_report(
    elf: Path,
    log_path: Path | None,
) -> dict[str, object]:

    elf_symbols, needed = read_elf_metadata(elf)

    _, nid_to_name = read_name_catalog()

    log = parse_log(log_path)

    runtime_imports: dict[str, str] = (
        log["imports"]  # type: ignore[assignment]
    )

    vps5_runtime_by_name: dict[
        str,
        dict[str, str],
    ] = log["vps5_exports_by_name"]  # type: ignore[assignment]

    vps5_runtime_by_nid: dict[
        str,
        dict[str, str],
    ] = log["vps5_exports_by_nid"]  # type: ignore[assignment]

    dynamic_failures: Counter[str] = (
        log["dynamic_failures"]  # type: ignore[assignment]
    )

    (
        vps5_source_by_name,
        vps5_source_by_nid,
    ) = scan_sysabi_exports(
        ROOT / "src/VirtualPS5.HLE"
    )

    sharpemu_by_name: dict[
        str,
        dict[str, str],
    ] = {}

    sharpemu_by_nid: dict[
        str,
        dict[str, str],
    ] = {}

    for source_root in (
        ROOT / "src/SharpEmu.HLE",
        ROOT / "src/SharpEmu.Libs",
    ):

        source_name, source_nid = (
            scan_sysabi_exports(
                source_root
            )
        )

        merge_exports(
            sharpemu_by_name,
            sharpemu_by_nid,
            source_name,
            source_nid,
        )

    raw_symbols = dict(elf_symbols)

    # Avoid duplicate rows if runtime log contains
    # the decoded name for a decorated ELF symbol.
    elf_nids: set[str] = set()

    for raw_name in elf_symbols:

        _, nid, _, _ = normalize_static_symbol(
            raw_name,
            runtime_imports,
            nid_to_name,
        )

        elf_nids.add(nid)

    for (
        runtime_name,
        runtime_nid,
    ) in runtime_imports.items():

        if runtime_name in raw_symbols:
            continue

        if runtime_nid in elf_nids:
            continue

        raw_symbols[
            runtime_name
        ] = "UNKNOWN"

    imports: list[
        dict[str, object]
    ] = []

    for raw_name in sorted(raw_symbols):

        symbol_type = raw_symbols[
            raw_name
        ]

        (
            name,
            nid,
            suffix,
            preencoded,
        ) = normalize_static_symbol(
            raw_name,
            runtime_imports,
            nid_to_name,
        )

        (
            status,
            provider,
            implementation,
        ) = status_for_static_import(
            name,
            nid,
            symbol_type,
            vps5_runtime_by_name,
            vps5_runtime_by_nid,
            vps5_source_by_name,
            vps5_source_by_nid,
            sharpemu_by_name,
            sharpemu_by_nid,
            log["data_unresolved"],  # type: ignore[arg-type]
        )

        library = infer_library(
            name,
            needed,
        )

        if (
            implementation
            and implementation.get(
                "library"
            ) not in (
                None,
                "unknown",
            )
        ):
            library = implementation[
                "library"
            ]

        priority = 100

        if status == "missing":

            priority = (
                20
                if name.startswith("sce")
                else 40
            )

        elif status in (
            "unresolved-data",
            "unknown-data",
        ):
            priority = 30

        entry: dict[str, object] = {
            "name": name,
            "nid": nid,
            "library": library,
            "kind": (
                "data"
                if symbol_type == "OBJECT"
                else "function"
            ),
            "symbol_type": symbol_type,
            "status": status,
            "priority": priority,
        }

        if (
            preencoded
            or raw_name != name
        ):
            entry[
                "raw_symbol"
            ] = raw_name

        if suffix:

            entry[
                "symbol_suffix"
            ] = suffix

            if len(suffix) >= 1:
                entry[
                    "module_id"
                ] = suffix[0]

            if len(suffix) >= 2:
                entry[
                    "library_id"
                ] = suffix[1]

        if provider:
            entry[
                "provider"
            ] = provider

        if (
            implementation
            and implementation.get(
                "source"
            )
        ):
            entry[
                "implementation_source"
            ] = implementation[
                "source"
            ]

        imports.append(entry)

    dynamic: list[
        dict[str, object]
    ] = []

    for (
        name,
        count,
    ) in dynamic_failures.most_common():

        nid = (
            runtime_imports.get(name)
            or compute_nid(name)
        )

        provider = None
        implementation = None

        implementation = find_implementation(
            name,
            nid,
            vps5_source_by_name,
            vps5_source_by_nid,
        )

        if implementation:
            provider = "virtualps5"

        else:

            implementation = find_implementation(
                name,
                nid,
                sharpemu_by_name,
                sharpemu_by_nid,
            )

            if implementation:
                provider = "sharpemu"

        status = (
            "implemented"
            if provider
            else "blocking"
        )

        entry: dict[str, object] = {
            "name": name,
            "nid": nid,
            "library": (
                implementation.get(
                    "library",
                    "unknown",
                )
                if implementation
                else infer_library(
                    name,
                    needed,
                )
            ),
            "kind": "dynamic-symbol",
            "status": status,
            "priority": (
                0
                if status == "blocking"
                else 100
            ),
            "requests": count,
        }

        if provider:
            entry[
                "provider"
            ] = provider

        if (
            implementation
            and implementation.get(
                "source"
            )
        ):
            entry[
                "implementation_source"
            ] = implementation[
                "source"
            ]

        dynamic.append(entry)

    status_counts = Counter(
        str(item["status"])
        for item in imports
    )

    provider_counts = Counter(
        str(item["provider"])
        for item in imports
        if item.get("provider")
        is not None
    )

    blockers = [
        item
        for item in dynamic
        if item["status"]
        == "blocking"
    ]

    stall_nid = log[
        "stall_import"
    ]

    stall_name = (
        nid_to_name.get(
            str(stall_nid)
        )
        if stall_nid
        else None
    )

    return {
        "schema_version": 2,

        "target": {
            "name": elf.name,
            "path": str(elf),
            "generation": log[
                "generation"
            ],
            "needed_libraries": needed,
        },

        "runtime": {
            "log": (
                str(log_path)
                if log_path
                else None
            ),
            "static_nids_resolved": len(
                runtime_imports
            ),
            "imported_data_rebound": log[
                "data_rebound"
            ],
            "imported_data_unresolved": log[
                "data_unresolved"
            ],
            "stall_import_stub": (
                stall_nid
            ),
            "stall_import_name": (
                stall_name
            ),
        },

        "summary": {
            "imports_total": len(
                imports
            ),
            "implemented_virtualps5": (
                provider_counts.get(
                    "virtualps5",
                    0,
                )
            ),
            "implemented_sharpemu": (
                provider_counts.get(
                    "sharpemu",
                    0,
                )
            ),
            "missing": (
                status_counts.get(
                    "missing",
                    0,
                )
            ),
            "unresolved_data": (
                status_counts.get(
                    "unresolved-data",
                    0,
                )
            ),
            "unknown_data": (
                status_counts.get(
                    "unknown-data",
                    0,
                )
            ),
            "blocking": len(
                blockers
            ),
        },

        "blockers": blockers,
        "dynamic_requests": dynamic,
        "imports": imports,
    }


def print_report(
    report: dict[str, object],
    print_all: bool,
) -> None:

    target = report["target"]
    summary = report["summary"]
    runtime = report["runtime"]

    assert isinstance(
        target,
        dict,
    )

    assert isinstance(
        summary,
        dict,
    )

    assert isinstance(
        runtime,
        dict,
    )

    print()
    print(
        "========================================"
    )
    print(
        " VirtualPS5 compatibility scan"
    )
    print(
        "========================================"
    )

    print(
        f" Target:      {target.get('name')}"
    )

    print(
        f" Generation:  "
        f"{target.get('generation') or 'unknown'}"
    )

    print(
        f" Imports:     "
        f"{summary.get('imports_total')}"
    )

    print(
        f" VPS5:        "
        f"{summary.get('implemented_virtualps5')}"
    )

    print(
        f" SharpEmu:    "
        f"{summary.get('implemented_sharpemu')}"
    )

    print(
        f" Missing:     "
        f"{summary.get('missing')}"
    )

    print(
        f" Data miss:   "
        f"{summary.get('unresolved_data')}"
    )

    print(
        f" Blockers:    "
        f"{summary.get('blocking')}"
    )

    stall_nid = runtime.get(
        "stall_import_stub"
    )

    stall_name = runtime.get(
        "stall_import_name"
    )

    if stall_nid:

        if stall_name:

            print(
                f" Stall stub:  "
                f"{stall_nid} -> "
                f"{stall_name}"
            )

        else:

            print(
                f" Stall stub:  "
                f"{stall_nid}"
            )

    print(
        "========================================"
    )

    blockers = report[
        "blockers"
    ]

    assert isinstance(
        blockers,
        list,
    )

    if blockers:

        print(
            "\nBLOCKING NOW"
        )

        for item in blockers:

            assert isinstance(
                item,
                dict,
            )

            print(
                f"  [P{item['priority']}] "
                f"{item['library']}:"
                f"{item['name']} "
                f"nid={item.get('nid')} "
                f"({item['kind']}, "
                f"requests="
                f"{item.get('requests', 0)})"
            )

    imports = report[
        "imports"
    ]

    assert isinstance(
        imports,
        list,
    )

    remaining = [
        item
        for item in imports
        if isinstance(
            item,
            dict,
        )
        and (
            print_all
            or item.get("status")
            != "implemented"
        )
    ]

    if remaining:

        print(
            "\nBACKLOG"
        )

        current_library = None

        for item in sorted(
            remaining,
            key=lambda value: (
                int(
                    value.get(
                        "priority",
                        100,
                    )
                ),
                str(
                    value.get(
                        "library",
                        "unknown",
                    )
                ),
                str(
                    value.get(
                        "name",
                        "",
                    )
                ),
            ),
        ):

            library = str(
                item.get(
                    "library",
                    "unknown",
                )
            )

            if (
                library
                != current_library
            ):

                print(
                    f"\n[{library}]"
                )

                current_library = (
                    library
                )

            marker = (
                "✓"
                if item.get(
                    "status"
                )
                == "implemented"
                else "✗"
            )

            provider = (
                f" via {item['provider']}"
                if item.get(
                    "provider"
                )
                else ""
            )

            nid = (
                f" nid={item['nid']}"
                if item.get(
                    "nid"
                )
                else ""
            )

            raw = ""

            if (
                item.get(
                    "raw_symbol"
                )
                and item.get(
                    "raw_symbol"
                )
                != item.get(
                    "name"
                )
            ):
                raw = (
                    f" raw="
                    f"{item['raw_symbol']}"
                )

            print(
                f"  {marker} "
                f"P{item.get('priority')} "
                f"{item.get('name')} "
                f"[{item.get('status')}]"
                f"{provider}"
                f"{nid}"
                f"{raw}"
            )


def main() -> int:

    args = parse_args()

    elf = (
        args.elf.resolve()
    )

    log_path = (
        args.log.resolve()
        if args.log
        else None
    )

    if not elf.is_file():
        raise SystemExit(
            f"ELF not found: {elf}"
        )

    if (
        log_path is not None
        and not log_path.is_file()
    ):
        raise SystemExit(
            f"log not found: {log_path}"
        )

    output = (
        args.output.resolve()
        if args.output
        else (
            elf.parent
            / "missing-nids.json"
        )
    )

    report = build_report(
        elf,
        log_path,
    )

    output.parent.mkdir(
        parents=True,
        exist_ok=True,
    )

    output.write_text(
        json.dumps(
            report,
            indent=2,
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )

    print_report(
        report,
        args.print_all,
    )

    print(
        f"\nJSON: {output}"
    )

    return 0


if __name__ == "__main__":
    raise SystemExit(main())