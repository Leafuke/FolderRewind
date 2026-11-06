"""Copy offline fixtures to a NEW directory and derive two independent real terrain edits.

Only the new copies are written. Both existing MCA payloads stay valid NBT: a palette Name
changes from stone to glass/water, then the edited chunk is appended in ordinary zlib form.
"""
import argparse
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import stat
import struct
import zlib

parser = argparse.ArgumentParser()
parser.add_argument("source", type=Path)
parser.add_argument("destination", type=Path)
args = parser.parse_args()
source = args.source.resolve(strict=True)
destination = args.destination.resolve()
if destination.exists() or destination == source or source in destination.parents:
    raise ValueError("Destination must be a new directory outside the source tree")
destination.mkdir(parents=True)


def reject_link(path):
    if path.is_symlink() or getattr(path.stat(), "st_file_attributes", 0) & 0x400:
        raise ValueError(f"Reparse input is not an offline fixture: {path}")


for corpus in ("modern154", "plugin-small-normal", "plugin-small-embedded"):
    for side in ("base", "ours", "theirs"):
        original = source / corpus / side
        for directory, children, files in os.walk(original, followlinks=False):
            current = Path(directory)
            reject_link(current)
            target = destination / corpus / side / current.relative_to(original)
            target.mkdir(parents=True, exist_ok=True)
            for name in children + files:
                reject_link(current / name)
            for name in files:
                shutil.copy2(current / name, target / name)

pattern = b"\x08\x00\x04Name\x00\x0fminecraft:stone"
selected = []
base = destination / "modern154" / "base"
for path in sorted(base.rglob("*.mca")):
    if path.parent.name != "region":
        continue
    match = re.fullmatch(r"r\.(-?\d+)\.(-?\d+)\.mca", path.name)
    if not match:
        continue
    data = path.read_bytes()
    if len(data) < 8192:
        continue
    for slot in range(1024):
        location = struct.unpack_from(">I", data, slot * 4)[0]
        offset, sectors = (location >> 8) * 4096, location & 255
        if offset < 8192 or sectors == 0 or offset + 5 > len(data):
            continue
        length = struct.unpack_from(">I", data, offset)[0]
        compression = data[offset + 4]
        if compression not in (1, 2, 3) or length < 1 or length > sectors * 4096 - 4:
            continue
        encoded = data[offset + 5:offset + 4 + length]
        raw = gzip.decompress(encoded) if compression == 1 else zlib.decompress(encoded) if compression == 2 else encoded
        if pattern not in raw or len(raw) > 16 * 1024 * 1024:
            continue
        selected.append((path.relative_to(base), slot, raw,
                         int(match[1]) * 32 + slot % 32, int(match[2]) * 32 + slot // 32))
        if len(selected) == 2:
            break
    if len(selected) == 2:
        break
if len(selected) != 2:
    raise ValueError("Expected two ordinary terrain chunks with a stone palette entry")

mutations = []
for side, block, entry in zip(("ours", "theirs"), (b"minecraft:glass", b"minecraft:water"), selected):
    relative, slot, raw, x, z = entry
    target = destination / "modern154" / side / relative
    before = target.stat()
    data = bytearray(target.read_bytes())
    changed = raw.replace(pattern, pattern[:-15] + block, 1)
    encoded = zlib.compress(changed, level=6)
    payload = struct.pack(">I", len(encoded) + 1) + b"\x02" + encoded
    sector = (len(data) + 4095) // 4096
    sector_count = (len(payload) + 4095) // 4096
    if sector_count > 255 or sector > 0xFFFFFF:
        raise ValueError("Edited chunk is outside the ordinary region budget")
    data.extend(b"\0" * (sector * 4096 - len(data)))
    data.extend(payload + b"\0" * (sector_count * 4096 - len(payload)))
    struct.pack_into(">I", data, slot * 4, sector << 8 | sector_count)
    struct.pack_into(">I", data, 4096 + slot * 4, 1_700_000_000 + len(mutations))
    os.chmod(target, before.st_mode | stat.S_IWRITE)
    target.write_bytes(data)
    os.chmod(target, before.st_mode)
    os.utime(target, ns=(before.st_atime_ns, before.st_mtime_ns))
    mutations.append(dict(side=side, path=relative.as_posix(), slot=slot, x=x, z=z,
                          old_palette="minecraft:stone", new_palette=block.decode(),
                          raw_before_sha256=hashlib.sha256(raw).hexdigest(),
                          raw_after_sha256=hashlib.sha256(changed).hexdigest()))

manifest = []
for path in sorted(destination.rglob("*")):
    if path.is_file():
        info = path.stat()
        manifest.append(dict(path=path.relative_to(destination).as_posix(), bytes=info.st_size,
                             modified_ns=info.st_mtime_ns,
                             attributes=getattr(info, "st_file_attributes", None),
                             sha256=hashlib.sha256(path.read_bytes()).hexdigest()))
record = dict(source=str(source), destination=str(destination), mutations=mutations, files=manifest)
(destination / "frozen-corpus.json").write_text(json.dumps(record, indent=2), encoding="utf-8")
print(json.dumps(dict(destination=str(destination), files=len(manifest), mutations=mutations), indent=2))
