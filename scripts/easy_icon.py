"""Render the easy-mode emblem into a Windows .ico (PNG entries, 16 to 256 px).

    python scripts/easy_icon.py src/tramoya/easy/ui/tramoya.ico

Needs Playwright's Chromium; the result is committed, so this only runs when the
emblem changes.
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

SVG = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 32 32" width="{n}" height="{n}">'
       '<rect width="32" height="32" rx="7" fill="#7A2635"/>'
       '<path d="M7 7h18v2.6c-2.4.3-3.6 2.8-3.6 6.4 0 3.4 1.2 7.2 3.6 9H7c2.4-1.8 3.6-5.6 '
       '3.6-9 0-3.6-1.2-6.1-3.6-6.4z" fill="#F3E3C3"/>'
       '<path d="M16 9.5v15" stroke="#7A2635" stroke-width="1.4"/></svg>')
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)


def ico(pngs: dict[int, bytes]) -> bytes:
    head = struct.pack("<HHH", 0, 1, len(pngs))
    offset = 6 + 16 * len(pngs)
    entries, data = b"", b""
    for n, png in sorted(pngs.items()):
        side = 0 if n >= 256 else n
        entries += struct.pack("<BBBBHHII", side, side, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    return head + entries + data


def main(out: str) -> int:
    from playwright.sync_api import sync_playwright
    pngs = {}
    with sync_playwright() as pw:
        browser = pw.chromium.launch()
        for n in SIZES:
            page = browser.new_page(viewport={"width": n, "height": n})
            page.set_content(f"<body style='margin:0'>{SVG.format(n=n)}</body>")
            pngs[n] = page.screenshot(omit_background=True,
                                      clip={"x": 0, "y": 0, "width": n, "height": n})
            page.close()
        browser.close()
    Path(out).write_bytes(ico(pngs))
    print(f"{out}: {len(pngs)} sizes")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1] if len(sys.argv) > 1 else "tramoya.ico"))
