"""Offline provenance, PNG/ICO structure and desktop command contract checks."""
import hashlib
import json
import re
import struct
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
manifest = json.loads((ROOT / 'assets/branding/manifest.json').read_text(encoding='utf8'))
assert manifest['tool'] == 'image_gen.imagegen (built-in)' and manifest['prompt']
for path, digest in manifest['sha256'].items():
    assert hashlib.sha256((ROOT / path).read_bytes()).hexdigest() == digest, path
icon = (ROOT / 'src/OpenRevo.App/Assets/OpenRevo.ico').read_bytes()
reserved, kind, count = struct.unpack_from('<HHH', icon)
assert (reserved, kind, count) == (0, 1, 10)
end = 6 + 16 * count
for i, size in enumerate(manifest['icoSizes']):
    width, height, colors, reserved, planes, depth, length, offset = struct.unpack_from('<BBBBHHII', icon, 6 + i * 16)
    assert (width or 256) == (height or 256) == size and (planes, depth) == (1, 32)
    assert offset == end and length > 24
    frame = icon[offset:offset + length]
    assert frame[:8] == b'\x89PNG\r\n\x1a\n' and struct.unpack_from('>II', frame, 16) == (size, size)
    end += length
assert end == len(icon)
# These IDs cross a native / managed process boundary. Fail CI on either-side drift.
native = (ROOT / 'src/OpenRevo.Background/tray-menu.h').read_text(encoding='utf8')
managed = (ROOT / 'src/OpenRevo.Core/TrayActions.cs').read_text(encoding='utf8')
names = dict(Overview='Open', Performance='Performance', Settings='Settings', Office='Office', Balanced='Balanced',
             Turbo='Turbo', Custom='Custom', FanBoost='FanBoost', FanAuto='FanAuto', Restore='Restore', Keep='Keep',
             Refresh='Refresh', Diagnostics='Diagnostics', Updates='Updates')
for name, command in names.items():
    assert re.search(r'\b' + name + r'\s*=\s*(\d+)', managed).group(1) == re.search(r'\b' + command + r'Command\s*=\s*(\d+)', native).group(1), name
print('Generated branding hashes, ten physical ICO frames and 14 desktop action IDs verified.')
