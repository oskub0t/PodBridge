# 3D artwork renderer

Renders `assets/3d/airpods_4.glb` into the app's artwork (`src/PodBridge.App/Assets/airpods-hero.png`)
with three.js in headless Chromium (software WebGL, so it also runs on servers without a GPU).

```bash
cd tools/render3d
npm install three@0.170.0
cp ../../assets/3d/airpods_4.glb model.glb
node render.mjs hero-2x.png "w=1600&h=1400&az=32&el=18"   # needs Playwright + Chromium
```

Then crop to content and scale to 520 px tall (see the commit that added this folder).

Page parameters: `az`/`el` camera angles (degrees), `w`/`h` size, `exp` exposure, `env` reflection
strength, `key` key light, `shadow` floor shadow opacity, `hide` comma-separated node names,
`lidT` lid open amount (0 = closed, 1 = as modelled; the closed angle is solved from the model's
hinge part), `budsT` buds (0 = seated in the case with their heads above the rim, everything below the rim
clipped; 1 = floating), `seat` how much of the buds shows above the rim when seated (default 0.33).

## Case-open animation (src/PodBridge.App/Assets/anim)

60 frames over 1 s (played in sync with the display), 1200×1050 each, then cropped to one shared
box and scaled to 300 px tall (about 2 MB in total):

```python
def clamp(x): return max(0.0, min(1.0, x))
def in_out_cubic(x): return 4*x*x*x if x < 0.5 else 1 - (-2*x + 2)**3 / 2
def bezier(x1, y1, x2, y2):                      # CSS-style cubic-bezier easing
    def f(x):
        lo, hi = 0.0, 1.0
        for _ in range(60):
            s = (lo + hi) / 2
            bx = 3*(1-s)**2*s*x1 + 3*(1-s)*s*s*x2 + s**3
            lo, hi = (s, hi) if bx < x else (lo, s)
        s = (lo + hi) / 2
        return 3*(1-s)**2*s*y1 + 3*(1-s)*s*s*y2 + s**3
    return f
rise = bezier(0.2, 0.3, 0.1, 1.0)                 # soft start, long settle
for i in range(60):
    t = i / 59
    lid  = in_out_cubic(clamp(t / 0.45))          # lid opens over the first 0.45 s
    buds = rise(clamp((t - 0.20) / 0.80))         # buds start rising with the lid about half open
    print(f"frames/f{i:02d}.png", f"lidT={lid:.4f}&budsT={buds:.4f}&w=1200&h=1050&az=32&el=18")
```

The buds overlap the lid on purpose: starting them only once the lid had settled (0.4.5 started
at 0.35 s, after the lid's ease-out) read as "case, pause, buds".

Crop: the union of every frame's non-transparent pixels, plus 30 px on each side (at 1200×1050),
then Lanczos-scaled to 300 px tall.

The Settings still (`airpods-hero.png`, 520 px tall) is the last frame with the same crop.

Model parts: case body `iqLXttIronNPiXb`, lid `nJoEOlgGwcdXBHJ`, hinge `rXHaxBciRFTeNPF`,
buds `NwGGgfiOhfXuluV`.

Model: "AirPods 4" by Falah3D (https://sketchfab.com/3dFalah), CC BY 4.0. See THIRD-PARTY-NOTICES.md.
