// Run only with a locally built binary. All jobs are synthetic dry runs, with no FTP sites.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const net = require('node:net');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const binary = path.resolve(process.env.WEAVEFXP_TEST_BINARY || 'Release/win-x64/WeaveFXP.exe');
  const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'weave-retry-ui-'));
  const probe = net.createServer();
  probe.listen(0, '127.0.0.1'); await once(probe, 'listening');
  const port = probe.address().port;
  await new Promise(resolve => probe.close(resolve));
  const url = `http://127.0.0.1:${port}`;
  fs.writeFileSync(path.join(fixture, 'settings.json'), JSON.stringify({
    enable_https_json_api: false, enable_udp_api: false, tray_icon_enabled: false,
    auto_open_web_ui: false, check_for_updates: false, web_bind_address: '127.0.0.1', web_port: port,
    max_concurrent_race_jobs: 2
  }));
  fs.writeFileSync(path.join(fixture, 'sites.json'), '{}');
  const files = Array.from({ length: 1200 }, (_, i) => ({ name: `payload.${i}.rar`, size: 50000000, status: 'fail', error: 'fixture: disk full' }));
  const jobs = Object.fromEntries(Array.from({ length: 100 }, (_, i) => [`retry-${i}`, {
    id: `retry-${i}`, type: 'race', state: 'failed', created_at: new Date(Date.now() - i * 1000).toISOString(),
    race_coverage_known: true, error: 'fixture: disk full', files,
    request: { race: true, dry_run: true, label: `Retry.Load.Fixture.${i}`, from_site: 'source', to_site: 'target', source_path: `/fixture/Retry.Load.Fixture.${i}`, dest_path: `/fixture/Retry.Load.Fixture.${i}` }
  }]));
  fs.writeFileSync(path.join(fixture, 'jobs.json'), JSON.stringify(jobs));
  const child = spawn(binary, ['--no-browser'], { windowsHide: true,
    env: { ...process.env, WEAVEFXP_STATE: path.join(fixture, 'state.json'), WEAVEFXP_URL: url }, stdio: 'ignore' });
  const exited = once(child, 'exit');
  let browser;
  try {
    for (let i = 0; ; i++) {
      try { if ((await fetch(`${url}/races`)).ok) break; } catch {}
      if (i >= 100) throw new Error('Local retry UI fixture did not start');
      await new Promise(resolve => setTimeout(resolve, 100));
    }
    browser = await chromium.launch({ channel: 'chrome', headless: true });
    const page = await browser.newPage({ viewport: { width: 1920, height: 1080 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => { if (message.type() === 'error') errors.push(message.text()); });
    await page.goto(`${url}/races`);
    await page.waitForFunction(() => document.querySelectorAll('.race-row').length === 100);
    const search = page.getByPlaceholder('Search release, site, path...');
    // Prerendered rows appear before the Blazor circuit is interactive. Confirm a
    // real filter round-trip before measuring the retry burst.
    for (let attempt = 0; ; attempt++) {
      await search.fill('');
      await search.fill('Retry.Load.Fixture.99');
      try { await page.waitForFunction(() => document.querySelectorAll('.race-row').length === 1, undefined, { timeout: 2000 }); break; }
      catch (error) { if (attempt >= 4) throw error; }
    }
    await search.fill('');
    await page.waitForFunction(() => document.querySelectorAll('.race-row').length === 100);
    const started = Date.now();
    await page.getByRole('button', { name: 'Retry', exact: true }).evaluateAll(buttons => buttons.slice(0, 30).forEach(button => button.click()));
    await search.fill('Retry.Load.Fixture.99');
    await page.waitForFunction(() => document.querySelectorAll('.race-row').length === 1);
    const responsiveMs = Date.now() - started;
    assert(responsiveMs < 5000, `Search remained blocked for ${responsiveMs}ms`);
    await search.fill('');
    await page.waitForFunction(() => document.querySelectorAll('.race-row .pill.green').length >= 30, undefined, { timeout: 20000 });
    assert.match(await page.locator('.page-sub').innerText(), /\d+ running · \d+ queued/);
    assert.deepEqual(errors, []);
    console.log(`PASS: Chrome at 1080p, 100 jobs/120000 old attempts, 30 retries completed; live search responded in ${responsiveMs}ms`);
    if (process.env.RETRY_LOAD_SCREENSHOT) await page.screenshot({ path: process.env.RETRY_LOAD_SCREENSHOT });
  } finally {
    if (browser) await browser.close();
    if (child.exitCode === null) child.kill();
    await exited;
    const resolved = path.resolve(fixture);
    assert.equal(path.dirname(resolved), path.resolve(os.tmpdir()));
    assert(path.basename(resolved).startsWith('weave-retry-ui-'));
    fs.rmSync(resolved, { recursive: true, force: true });
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
