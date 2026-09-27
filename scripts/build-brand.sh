#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
mkdir -p build/Seamlet.iconset
for size in 16 32 128 256 512; do
    sips -z "$size" "$size" assets/brand/seamlet-icon.png --out "build/Seamlet.iconset/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" assets/brand/seamlet-icon.png --out "build/Seamlet.iconset/icon_${size}x${size}@2x.png" >/dev/null
done
iconutil -c icns build/Seamlet.iconset -o assets/brand/Seamlet.icns
python3 - <<'PY'
from pathlib import Path
import struct, base64
sizes = [16, 32, 128, 256]
images = [(s, Path(f'build/Seamlet.iconset/icon_{s}x{s}.png').read_bytes()) for s in sizes]
header = struct.pack('<HHH', 0, 1, len(images))
offset = 6 + 16 * len(images)
entries = b''
for size, data in images:
    entries += struct.pack('<BBBBHHII', size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
    offset += len(data)
Path('assets/brand/Seamlet.ico').write_bytes(header + entries + b''.join(data for _, data in images))
icon = base64.b64encode(Path('assets/brand/seamlet-icon.png').read_bytes()).decode()
Path('assets/brand/seamlet-logo.svg').write_text(f'''<svg xmlns="http://www.w3.org/2000/svg" width="1000" height="240" viewBox="0 0 1000 240" role="img" aria-label="Seamlet — A little less between you and your screens.">
<image href="data:image/png;base64,{icon}" x="0" y="0" width="240" height="240"/>
<text x="258" y="139" fill="#173536" font-family="Arial, Helvetica, sans-serif" font-size="108" font-weight="700" letter-spacing="-4">Seamlet</text>
<text x="264" y="186" fill="#607171" font-family="Arial, Helvetica, sans-serif" font-size="24">A little less between you and your screens.</text></svg>''')
PY
