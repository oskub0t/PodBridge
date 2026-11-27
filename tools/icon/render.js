// Renders SVG strings to transparent PNGs at exact pixel sizes with Chromium.
const { chromium } = require('playwright');
(async () => {
  const jobs = JSON.parse(require('fs').readFileSync(0, 'utf8'));
  const browser = await chromium.launch();
  const page = await browser.newPage({ deviceScaleFactor: 1 });
  for (const j of jobs) {
    await page.setViewportSize({ width: j.size, height: j.size });
    const svg = j.svg.replace(/width="256" height="256"/, `width="${j.size}" height="${j.size}"`);
    await page.setContent(`<html><body style="margin:0;background:transparent">${svg}</body></html>`);
    await page.screenshot({ path: j.out, omitBackground: true, clip: { x: 0, y: 0, width: j.size, height: j.size } });
  }
  await browser.close();
})();
