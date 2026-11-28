// Usage: node render.mjs out.png "query string"
import { createRequire } from 'node:module'; const { chromium } = createRequire(import.meta.url)('playwright');
import http from 'node:http'; import fs from 'node:fs'; import path from 'node:path';
const root = path.dirname(new URL(import.meta.url).pathname);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.glb': 'model/gltf-binary' };
const server = http.createServer((req, res) => {
  const p = path.join(root, decodeURIComponent(req.url.split('?')[0]));
  if (!p.startsWith(root) || !fs.existsSync(p)) { res.writeHead(404); return res.end(); }
  res.writeHead(200, { 'Content-Type': types[path.extname(p)] || 'application/octet-stream' });
  fs.createReadStream(p).pipe(res);
}).listen(0);
const port = server.address().port;
const browser = await chromium.launch({ args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist'] });
const page = await browser.newPage({ deviceScaleFactor: 1 });
page.on('console', m => m.type() === 'error' && console.error('page:', m.text()));
page.on('pageerror', e => console.error('pageerror:', e.message));
for (let i = 2; i < process.argv.length; i += 2) {
  await page.goto(`http://localhost:${port}/index.html?${process.argv[i + 1] || ''}`);
  await page.waitForFunction('window.__done === true', null, { timeout: 120000 });
  const png = await page.evaluate(() => document.querySelector('canvas').toDataURL('image/png'));
  fs.writeFileSync(process.argv[i], Buffer.from(png.split(',')[1], 'base64'));
  console.log(process.argv[i], JSON.stringify(await page.evaluate(() => window.__info)));
}
await browser.close(); server.close();
