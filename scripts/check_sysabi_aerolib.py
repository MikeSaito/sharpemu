# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

#!/usr/bin/env python3

"""Offline check: SysAbiExport ExportName must hash to its Nid (name2nid).

Run from the repository root:
  python scripts/check_sysabi_aerolib.py
  python scripts/check_sysabi_aerolib.py --strict
"""

from __future__ import annotations

import argparse
import hashlib
import re
import struct
import sys
from base64 import b64encode as base64enc
from binascii import unhexlify as uhx
from pathlib import Path

SRC_ROOT = Path("src")
SYSABI_EXPORT_RE = re.compile(r"\[SysAbiExport\((.*?)\)\]", re.DOTALL)
NID_RE = re.compile(r'Nid\s*=\s*"([^"]+)"')
EXPORT_NAME_RE = re.compile(r'ExportName\s*=\s*"([^"]+)"')


def name2nid(name: str) -> str:
    symbol = hashlib.sha1(name.encode() + uhx("518D64A635DED8C1E6B039B1C3E55230")).digest()
    id_val = struct.unpack("<Q", symbol[:8])[0]
    nid = base64enc(uhx("%016x" % id_val), b"+-").rstrip(b"=")
    return nid.decode("utf-8")


def find_repo_root() -> Path:
    cwd = Path.cwd()
    if (cwd / SRC_ROOT).is_dir() and (cwd / "scripts").is_dir():
        return cwd
    script_root = Path(__file__).resolve().parent.parent
    if (script_root / SRC_ROOT).is_dir():
        return script_root
    raise SystemExit("Run from the repository root (src/ and scripts/ expected).")


def iter_sysabi_exports(cs_path: Path, text: str):
    for match in SYSABI_EXPORT_RE.finditer(text):
        block = match.group(1)
        nid_match = NID_RE.search(block)
        export_match = EXPORT_NAME_RE.search(block)
        if nid_match is None or export_match is None:
            continue

        nid = nid_match.group(1)
        export_name = export_match.group(1)
        # Prefer the Nid= attribute line for reporter location.
        nid_attr = f'Nid = "{nid}"'
        abs_pos = text.find(nid_attr, match.start(), match.end())
        if abs_pos < 0:
            abs_pos = match.start()
        line = text.count("\n", 0, abs_pos) + 1
        yield cs_path, line, nid, export_name


def scan(src_root: Path):
    checked = 0
    mismatches = []
    for cs_path in sorted(src_root.rglob("*.cs")):
        text = cs_path.read_text(encoding="utf-8")
        for path, line, nid, export_name in iter_sysabi_exports(cs_path, text):
            checked += 1
            computed = name2nid(export_name)
            if computed != nid:
                mismatches.append((path, line, nid, export_name, computed))
    return checked, mismatches


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Check that SysAbiExport ExportName values hash to their Nid via name2nid."
    )
    parser.add_argument(
        "--strict",
        action="store_true",
        help="Exit 1 when any ExportName does not hash to its Nid.",
    )
    parser.add_argument(
        "--quiet",
        action="store_true",
        help="Print only the summary line.",
    )
    args = parser.parse_args()

    repo_root = find_repo_root()
    src_root = repo_root / SRC_ROOT
    checked, mismatches = scan(src_root)
    ok = checked - len(mismatches)

    if not args.quiet:
        for path, line, nid, export_name, computed in mismatches:
            rel = path.relative_to(repo_root).as_posix()
            print(
                f"{rel}:{line}: NID={nid} ExportName={export_name!r} "
                f"computed={computed}"
            )

    print(f"checked={checked} ok={ok} fail={len(mismatches)}")

    if args.strict and mismatches:
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
