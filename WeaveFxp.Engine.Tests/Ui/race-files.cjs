const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    for (const [width, height] of [[1920, 1080], [1536, 864], [1366, 768], [390, 844]]) {
      await page.setViewportSize({ width, height });
      await page.goto(process.env.RACE_FILES_URL || 'http://127.0.0.1:18791/races');
      await page.locator('.race-row').click();
      const table = page.locator('.race-unified-files');
      await table.waitFor();
      assert(await page.locator('.race-row .col-actions').evaluateAll(cells => cells.every(cell => {
        const rect = cell.getBoundingClientRect();
        return [...cell.querySelectorAll('button')].every(button => {
          const bounds = button.getBoundingClientRect();
          return bounds.left >= rect.left && bounds.right <= rect.right;
        });
      })), `race actions stay inside their column at ${width}px`);
      await page.getByRole('button', { name: 'Actions', exact: true }).click();
      const dialog = page.getByRole('dialog');
      await dialog.waitFor();
      assert(await dialog.evaluate(el => el.getBoundingClientRect().width <= innerWidth), 'actions dialog fits the viewport');
      await dialog.getByTitle('Close', { exact: true }).click();
      await dialog.waitFor({ state: 'hidden' });
      if (process.env.CHECK_RECHECK_BUTTON) {
        const recheck = page.getByRole('button', { name: 'Recheck', exact: true });
        assert.equal(await recheck.count(), 1, 'failed route exposes a separate read-only recheck');
        await recheck.click();
        await page.getByText('No stable complete evidence yet', { exact: true }).waitFor();
        assert.equal(await recheck.isEnabled(), true, 'recheck control recovers after an unsuccessful read-only check');
      }
      assert.equal(await table.count(), 1, 'one combined file list');
      assert.equal(await table.locator('tbody > tr').count(), 623, 'all 620 SFV files plus SFV/NFO transfers and concurrent second route');
      assert.equal(await table.locator('.file-active').count(), 2, 'both live routes remain visible; superseded active attempts do not');
      assert.equal(await table.locator('[data-file="payload.0000.rar"]').count(), 2, 'same file can be live on two routes');
      const routes = await table.locator('.file-active .route-cell').allTextContents();
      assert(routes.some(route => route.includes('KPN')) && routes.some(route => route.includes('DC')));
      assert.equal(await table.evaluate(el => !!el.closest('details')), false, 'transfers are not behind another expander');
      assert.equal(await page.locator('.race-transfer-attempts, .race-sfv-table, [role="tablist"]').count(), 0);
      const active = table.locator('tbody > tr').first();
      assert.equal(await active.getAttribute('data-file'), 'payload.0000.rar', 'active transfer is first');
      assert.equal(await active.locator('.pill').textContent(), 'Uploading', 'live transfer beats stale SFV completion');
      assert((await active.locator('.transferred-cell').textContent()).includes('9.5 MB'), 'observed transfer bytes visible immediately');
      assert.equal(await active.locator('.file-own').textContent(), 'No', 'active/failed attempts earn no upload credit');
      const own = table.locator('[data-file="payload.0001.rar"]');
      assert.equal(await own.locator('.file-own').textContent(), 'Yes');
      assert((await own.textContent()).includes('23.8 MB/s'), 'completed transfer speed retained');
      const peer = table.locator('[data-file="payload.0002.rar"]');
      assert.equal(await peer.locator('.pill').textContent(), 'Complete');
      assert.equal(await peer.locator('.file-own').textContent(), 'No', 'dupe/peer coverage is not our upload');
      assert.equal(await table.locator('[data-file="payload.0003.rar"] .pill').textContent(), 'Waiting');
      assert.equal(await table.locator('[data-file="payload.0004.rar"] .pill').textContent(), 'Missing');
      assert.equal(await table.locator('[data-file="payload.0619.rar"]').count(), 1, 'manifest not truncated to attempt-history cap');
      assert((await own.locator('.file-name').getAttribute('title')).includes('CRC32: C71C0011'), 'CRC retained in file tooltip');
      assert(await page.locator('body').evaluate(el => el.scrollWidth <= innerWidth + 1), 'no page-wide horizontal overflow');
      if (width >= 1366) {
        assert(await table.evaluate(el => el.getBoundingClientRect().width <= el.parentElement.clientWidth + 1), `file list fits ${width}px`);
        assert(await active.evaluate(el => [...el.querySelectorAll('td:not(.file-name)')].every(td => td.scrollWidth <= td.clientWidth + 1)), 'live values fit their columns');
      }
      await page.screenshot({ path: path.join(process.env.RACE_FILES_OUTPUT || '.', `race-files-${width}.png`) });
      await page.locator('.race-row').click();
      await table.waitFor({ state: 'hidden' });
      await page.locator('.race-row').click();
      await table.waitFor({ state: 'visible' });
      assert.equal(await page.locator('.race-unified-files tbody > tr').count(), 623, 'reopening preserves the same full list');
      await page.goto(new URL('/settings', page.url()).href);
      const transfersTab = page.locator('.editor-tabs button').filter({ hasText: 'Transfers' });
      // SSR can expose the tab before its Blazor circuit accepts clicks.
      for (let attempt = 0; attempt < 3; attempt++) {
        await transfersTab.click();
        try {
          await page.getByRole('heading', { name: 'Global Skiplist', exact: true }).waitFor({ timeout: 2000 });
          break;
        } catch (error) { if (attempt === 2) throw error; }
      }
      const skipPanel = page.locator('section.panel-card').filter({ has: page.getByRole('heading', { name: 'Global Skiplist', exact: true }) });
      const rowCount = await skipPanel.locator('.wfx-skip-row').count();
      await skipPanel.getByRole('button', { name: 'Add pattern' }).click();
      await page.waitForFunction(count => document.querySelectorAll('.wfx-skip-row').length === count + 1, rowCount);
      const rule = skipPanel.locator('.wfx-skip-row').last();
      const pattern = rule.locator('.wfx-skip-pattern');
      await pattern.fill('*.nfo');
      await rule.locator('select').selectOption('Unique');
      await skipPanel.locator('.wfx-skip-test input').fill('release.nfo');
      await skipPanel.getByText(/Unique by rule/).waitFor();
      await rule.getByTitle('Regex', { exact: true }).check();
      await pattern.fill('[');
      await skipPanel.getByText(/Invalid regex:/).waitFor();
      assert(await pattern.evaluate(el => el.getBoundingClientRect().width >= 100), `skiplist pattern remains editable at ${width}px`);
      assert(await page.locator('body').evaluate(el => el.scrollWidth <= innerWidth + 1), 'skiplist does not overflow the page');
      await page.screenshot({ path: path.join(process.env.RACE_FILES_OUTPUT || '.', `skiplist-${width}.png`) });
      // Deliberately leave settings unsaved: this fixture has no remote sites.
      console.log(`PASS: ${width}x${height}, one directly visible list, active first, all SFV files, own-upload evidence, compact layout`);
    }
    assert.deepEqual(errors, []);
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
