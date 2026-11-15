#!/usr/bin/env python3
"""Package only explicitly permitted mod files. Does not build or test."""
from pathlib import Path
from zipfile import ZIP_DEFLATED, ZipFile

root = Path(__file__).resolve().parent.parent
allowed = (
    'About/About.xml',
    'About/Preview.png',
    '1.7/Assemblies/AutoBlinkCaravenSpeed.dll',
    'README.md',
    'LICENSE',
    'docs/compatibility.md',
    'docs/art-provenance.md',
)
for name in allowed:
    path = root / name
    if path.is_symlink() or not path.is_file() or not path.resolve().is_relative_to(root):
        raise SystemExit(f'Missing or unsafe required file: {name}; build the current DLL first')
    if path.stat().st_size == 0:
        raise SystemExit(f'Empty required file: {name}')

out = root / 'Auto-Blink-Caraven-Speed-CANDIDATE.zip'
with ZipFile(out, 'w', ZIP_DEFLATED) as archive:
    for name in allowed:
        archive.write(root / name, (Path('Auto-Blink Caraven Speed') / name).as_posix())
print(f'{out}\nCandidate only: build freshness, tests, and in-game validation were not checked.')
