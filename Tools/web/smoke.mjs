// Headless smoke test of the web build (Chromium + SwiftShader WebGL).
//   node Tools/web/smoke.mjs [--page dev/avatar-viewer.html] [--query "autoplay=1&spectate=1"] [--seconds 20] [--shots 4] [--tag name] [--keys]
//        [--root dir] (serve another directory, e.g. the site from `deploy_pages.sh --dry-run dir`; env DU_ROOT works too)
// Prints a JSON report (page errors, console errors/warnings, failed requests, game stats) and writes screenshots to
// Tools/web/screenshots/<tag>-N.png. Exit code 1 when the page threw or logged errors.
import { chromium } from 'playwright-core';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { startServer } from './serve.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const args = process.argv.slice(2);
const opt = (name, def) => { const i = args.indexOf('--' + name); return i >= 0 ? args[i + 1] : def; };
const flag = (name) => args.includes('--' + name);
const query = opt('query', 'autoplay=1&spectate=1&seed=7&quality=low&maxdt=0.6');
const seconds = Number(opt('seconds', '20'));
const shots = Number(opt('shots', '4'));
const tag = opt('tag', 'smoke');
const width = Number(opt('width', '1280')), height = Number(opt('height', '720'));
const exe = process.env.PW_CHROMIUM || '/opt/pw-browsers/chromium-1194/chrome-linux/chrome';

const server = await startServer(0, opt('root') ? { root: opt('root') } : {});
const pagePath = opt('page', 'index.html');
const url = `http://127.0.0.1:${server.address().port}/${pagePath}?${query}`;
const browser = await chromium.launch({
  executablePath: exe,
  args: ['--use-angle=swiftshader', '--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--enable-webgl', '--autoplay-policy=no-user-gesture-required'],
});
const page = await browser.newPage({ viewport: { width, height } });
const report = { url, pageErrors: [], consoleErrors: [], consoleWarnings: [], failedRequests: [], screenshots: [], stats: null };
page.on('pageerror', (e) => report.pageErrors.push(String(e && e.stack || e)));
page.on('console', (m) => {
  const t = m.type();
  if (t === 'error') report.consoleErrors.push(m.text());
  else if (t === 'warning') report.consoleWarnings.push(m.text());
});
page.on('requestfailed', (r) => report.failedRequests.push(`${r.url()} ${r.failure() && r.failure().errorText}`));
page.on('response', (r) => { if (r.status() >= 400) report.failedRequests.push(`${r.status()} ${r.url()}`); });

fs.mkdirSync(path.join(here, 'screenshots'), { recursive: true });
try {
  await page.goto(url, { waitUntil: 'load', timeout: 60000 });
  await page.waitForFunction(() => window.__DU && window.__DU.ready === true, null, { timeout: 90000 }).catch(() => report.pageErrors.push('timeout: window.__DU.ready never became true'));
  if (flag('keys')) {
    await page.mouse.click(width / 2, height / 2);
    for (const k of ['KeyW', 'KeyA', 'KeyD', 'ShiftLeft', 'Space']) { await page.keyboard.down(k); await page.waitForTimeout(300); await page.keyboard.up(k); }
    await page.mouse.down(); await page.waitForTimeout(700); await page.mouse.up();
    await page.mouse.down({ button: 'right' }); await page.mouse.up({ button: 'right' });
    await page.keyboard.press('KeyF'); await page.keyboard.press('KeyR'); await page.keyboard.press('KeyQ');
  }
  const interval = (seconds * 1000) / Math.max(1, shots);
  for (let i = 0; i < shots; i++) {
    await page.waitForTimeout(interval);
    const file = path.join(here, 'screenshots', `${tag}-${i + 1}.png`);
    await page.screenshot({ path: file });
    report.screenshots.push(path.relative(path.join(here, '..', '..'), file));
  }
  report.stats = await page.evaluate(() => (window.__DU && window.__DU.stats ? window.__DU.stats() : null)).catch((e) => String(e));
} catch (e) {
  report.pageErrors.push('harness: ' + (e && e.message || e));
}
await browser.close();
server.close();
const dedupe = (a) => [...new Set(a)].slice(0, 40);
for (const k of ['pageErrors', 'consoleErrors', 'consoleWarnings', 'failedRequests']) report[k] = dedupe(report[k]);
console.log(JSON.stringify(report, null, 2));
process.exit(report.pageErrors.length || report.consoleErrors.length ? 1 : 0);
