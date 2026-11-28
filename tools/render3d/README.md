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
`lid` lid rotation in degrees around the hinge (positive closes), `budsDy`/`budsOpacity`.

Model parts: case body `iqLXttIronNPiXb`, lid `nJoEOlgGwcdXBHJ`, hinge `rXHaxBciRFTeNPF`,
buds `NwGGgfiOhfXuluV`.

Model: "AirPods 4" by Falah3D (https://sketchfab.com/3dFalah), CC BY 4.0. See THIRD-PARTY-NOTICES.md.
