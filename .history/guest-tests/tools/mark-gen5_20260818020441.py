#!/usr/bin/env python3

import sys
from pathlib import Path

if len(sys.argv) != 3:
    print(f"Usage: {sys.argv[0]} <input.elf> <output.elf>")
    raise SystemExit(1)

src = Path(sys.argv[1])
dst = Path(sys.argv[2])

data = bytearray(src.read_bytes())

if data[:4] != b"\x7fELF":
    raise SystemExit(f"{src} is not an ELF file")

data[8] = 2

dst.write_bytes(data)

print(f"[GEN5] {src} -> {dst}")