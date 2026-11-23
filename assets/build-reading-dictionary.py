"""Rebuild the embedded reading data; Python stdlib only, no analyzer code.

Download naist-jdic.csv, matrix.def and COPYING from
https://github.com/jpreprocess/naist-jdic/tree/394af50de2da4a9c73b1bd04fab43e85bb6f03ff
to .local/dictionary-source first. Run from the repository root.
The output retains surface, left/right context IDs, word cost and pronunciation;
POS, accent, and other unused columns are omitted. COPYING must accompany it.
"""
import csv
import gzip
import io
import pathlib
import shutil
import struct

source = pathlib.Path('.local/dictionary-source')
records = set()
for row in csv.reader((source / 'naist-jdic.csv').open(encoding='utf-8')):
    if len(row) >= 13 and row[12] != '*' and row[0]:
        records.add((row[0], int(row[1]), int(row[2]), int(row[3]), row[12]))
records = sorted(records, key=lambda row: (row[0].encode('utf-16-be'), row[1:]))
result = io.BytesIO()
result.write(b'TYJD1')
result.write(struct.pack('<i', len(records)))
for surface, left, right, cost, reading in records:
    for text in (surface, reading):
        encoded = text.encode('utf-8')
        result.write(struct.pack('<H', len(encoded)))
        result.write(encoded)
    result.write(struct.pack('<HHh', left, right, cost))
with (source / 'matrix.def').open(encoding='utf-8') as matrix:
    width, height = map(int, next(matrix).split())
    result.write(struct.pack('<HH', width, height))
    values = [0] * (width * height)
    for line in matrix:
        previous_right, next_left, cost = map(int, line.split())
        values[previous_right * height + next_left] = cost
    result.write(struct.pack('<' + 'h' * len(values), *values))
pathlib.Path('assets/naist-jdic.bin.gz').write_bytes(gzip.compress(result.getvalue(), compresslevel=9, mtime=0))
shutil.copyfile(source / 'COPYING', 'assets/naist-jdic.COPYING')
print(f'{len(records)} entries, {width}x{height} transitions')
