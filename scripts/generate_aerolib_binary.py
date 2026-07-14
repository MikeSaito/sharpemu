# Copyright (C) 2026 SharpEmu Emulator Project
# SPDX-License-Identifier: GPL-2.0-or-later

#!/usr/bin/env python3

import hashlib
import struct
from base64 import b64encode as base64enc
from binascii import unhexlify as uhx
from pathlib import Path

CSV = 'scripts/nids.csv'
NAMES = 'scripts/ps5_names.txt'
OUTPUT = 'src/SharpEmu.HLE/Aerolib/aerolib.bin'

def name2nid(name):
    symbol = hashlib.sha1(name.encode() + uhx('518D64A635DED8C1E6B039B1C3E55230')).digest()
    id_val = struct.unpack('<Q', symbol[:8])[0]
    nid = base64enc(uhx('%016x' % id_val), b'+-').rstrip(b'=')
    return nid.decode('utf-8')

def load_csv_entries(csv_path):
    entries = {}
    skipped_dup_nid = 0
    with open(csv_path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            parts = line.split(None, 1)
            if len(parts) != 2:
                continue
            nid, name = parts[0], parts[1]
            if nid in entries:
                skipped_dup_nid += 1
                continue
            entries[nid] = name
    return entries, skipped_dup_nid

def load_ps5_names(names_path):
    names = []
    with open(names_path, 'r', encoding='utf-8') as f:
        for line in f:
            name = line.strip()
            if name:
                names.append(name)
    return names

def generate():
    csv_path = Path(CSV)
    names_path = Path(NAMES)
    output_path = Path(OUTPUT)

    csv_entries, skipped_dup_nid = load_csv_entries(csv_path)
    ps5_names = load_ps5_names(names_path)
    ps5_set = set(ps5_names)
    csv_names = set(csv_entries.values())

    catalog = dict(csv_entries)
    csv_only = len(csv_names - ps5_set)
    computed_only = 0
    name_mismatches = 0
    nid_collisions = 0

    for name in ps5_names:
        if name in csv_names:
            csv_nid = next(nid for nid, export_name in csv_entries.items() if export_name == name)
            if csv_nid != name2nid(name):
                name_mismatches += 1
            continue

        computed_nid = name2nid(name)
        if computed_nid in catalog:
            if catalog[computed_nid] != name:
                nid_collisions += 1
            continue

        catalog[computed_nid] = name
        computed_only += 1

    entries = sorted(catalog.items(), key=lambda item: item[1].casefold())

    print(f"CSV entries (unique NIDs): {len(csv_entries)}")
    if skipped_dup_nid:
        print(f"CSV duplicate NIDs skipped: {skipped_dup_nid}")
    print(f"ps5_names entries: {len(ps5_names)}")
    print(f"Catalog total: {len(entries)}")
    print(f"csv-only names: {csv_only}")
    print(f"computed-only names: {computed_only}")
    print(f"name/NID mismatches (csv wins): {name_mismatches}")
    print(f"computed NID collisions skipped: {nid_collisions}")

    data = bytearray()
    data.extend(struct.pack('<I', len(entries)))

    for nid, name in entries:
        nid_bytes = nid.encode('utf-8')
        name_bytes = name.encode('utf-8')
        data.append(len(nid_bytes))
        data.extend(nid_bytes)
        data.extend(struct.pack('<H', len(name_bytes)))
        data.extend(name_bytes)

    output_path.parent.mkdir(parents=True, exist_ok=True)
    with open(output_path, 'wb') as f:
        f.write(data)

    print(f"Generated: {output_path} ({len(data):,} bytes)")

if __name__ == "__main__":
    generate()
