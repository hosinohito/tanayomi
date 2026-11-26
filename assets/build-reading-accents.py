"""Build the accent sidecar aligned to each row of the existing TYJD1 dictionary.

Use the same naist-jdic.csv version and COPYING as build-reading-dictionary.py.
Conservatively retain only unanimous accent data for independent common nouns.
Run from the repository root; no Python is used by the app.
"""
import csv
import gzip
import io
from pathlib import Path
import re
import struct

votes = {}
with Path(".local/dictionary-source/naist-jdic.csv").open(encoding="utf-8") as source:
    for row in csv.reader(source):
        if len(row) < 13 or not row[0] or row[12] == "*":
            continue
        key = (row[0], row[12], *(int(row[i]) for i in (1, 2, 3)))
        match = re.fullmatch(r"(\d+)/(\d+)", row[13]) if len(row) > 13 and row[4:6] == ["名詞", "一般"] else None
        value = (int(match[1]), int(match[2])) if match else None
        if key not in votes:
            votes[key] = value
        elif votes[key] != value:
            votes[key] = None

r = io.BytesIO(gzip.decompress(Path("assets/naist-jdic.bin.gz").read_bytes()))
assert r.read(5) == b"TYJD1"
count, = struct.unpack("<i", r.read(4))
result = bytearray(b"TYAC1" + struct.pack("<i", count))
def read_word():
    length, = struct.unpack("<H", r.read(2))
    return r.read(length).decode("utf-8")

for _ in range(count):
    key = (read_word(), read_word(), *struct.unpack("<HHh", r.read(6)))
    assert key in votes, key
    value = votes[key]
    result.extend(value if value is not None and 0 <= value[0] <= value[1] <= 63 else (255, 0))
data = gzip.compress(result, compresslevel=9, mtime=0)
Path("assets/naist-jdic-accents.bin.gz").write_bytes(data)
print(f"{count} original rows aligned, {sum(x != 255 for x in result[9::2])} explicit patterns; {len(data)} bytes compressed")
