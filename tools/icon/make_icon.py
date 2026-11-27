"""Builds PodBridge's app icon: SVG source -> PNGs (via Chromium) -> multi-size .ico.

Usage: python3 make_icon.py   (needs Pillow and Node + Playwright)
Original artwork: a squircle with two generic earbuds. No Apple imagery.
"""
import math, pathlib, subprocess, json
from PIL import Image

HERE = pathlib.Path(__file__).parent
OUT = HERE.parent.parent / "src" / "PodBridge.App" / "Assets"
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]

def squircle(size, pad, n=5.0, steps=256):
    c, a = size / 2, size / 2 - pad
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        ct, st = math.cos(t), math.sin(t)
        x = c + a * math.copysign(abs(ct) ** (2 / n), ct)
        y = c + a * math.copysign(abs(st) ** (2 / n), st)
        pts.append(f"{x:.2f},{y:.2f}")
    return "M" + " L".join(pts) + " Z"

def bud(cx, cy, r, stem_w, stem_h, mirror, fill):
    # round head + stem hanging down on the inner side
    sx = cx + (r * 0.15 if not mirror else -r * 0.15 - stem_w)
    return (f'<circle cx="{cx}" cy="{cy}" r="{r}" fill="{fill}"/>'
            f'<rect x="{sx}" y="{cy}" width="{stem_w}" height="{stem_h}" rx="{stem_w/2}" fill="{fill}"/>')

def svg(detail: bool):
    S = 256
    pad = 10
    shape = squircle(S, pad)
    if detail:
        buds = bud(84, 96, 30, 22, 100, False, "#fff") + bud(172, 96, 30, 22, 100, True, "#fff")
    else:  # small sizes: bigger, simpler shapes so they survive 16 px
        buds = bud(78, 88, 40, 30, 112, False, "#fff") + bud(178, 88, 40, 30, 112, True, "#fff")
    arc = ""
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 {S} {S}" width="{S}" height="{S}">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#36D1DC"/>
      <stop offset="1" stop-color="#2F5BEA"/>
    </linearGradient>
    <linearGradient id="sheen" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#fff" stop-opacity="0.22"/>
      <stop offset="0.5" stop-color="#fff" stop-opacity="0"/>
    </linearGradient>
    <filter id="lift" x="-20%" y="-20%" width="140%" height="140%">
      <feDropShadow dx="0" dy="5" stdDeviation="6" flood-color="#0B1F6B" flood-opacity="0.35"/>
    </filter>
  </defs>
  <path d="{shape}" fill="url(#bg)"/>
  <path d="{shape}" fill="url(#sheen)"/>
  <g filter="url(#lift)">{buds}{arc}</g>
</svg>'''

def main():
    OUT.mkdir(parents=True, exist_ok=True)
    big, small = svg(True), svg(False)
    (HERE / "icon.svg").write_text(big)
    (HERE / "icon-small.svg").write_text(small)
    jobs = [{"svg": small if s <= 32 else big, "size": s, "out": str(HERE / f"icon-{s}.png")} for s in SIZES]
    subprocess.run(["node", str(HERE / "render.js")], input=json.dumps(jobs), text=True, check=True)
    imgs = [Image.open(HERE / f"icon-{s}.png").convert("RGBA") for s in SIZES]
    imgs[-1].save(OUT / "PodBridge.ico", format="ICO", sizes=[(s, s) for s in SIZES], append_images=imgs[:-1])
    imgs[-1].save(OUT / "PodBridge-256.png")
    print("wrote", OUT / "PodBridge.ico")

if __name__ == "__main__":
    main()
