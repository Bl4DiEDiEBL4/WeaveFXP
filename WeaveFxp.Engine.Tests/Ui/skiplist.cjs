// Run against an isolated fixture containing the report's two rules, inert rules
// #3-16, a catch-all DENY #17 and ALLOW #18. Never point this at real settings.
const assert = require('node:assert/strict');
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'playwright');
const roots = '(?i)^(sample(s)?|proof(s)?|subs?|cover(s)?|(cd|dvd|dis(k|c))\\d+)$';

(async () => {
  for (const channel of (process.env.BROWSER_CHANNELS || 'chrome,msedge').split(',')) {
    const browser = await chromium.launch({ channel, headless: true });
    try {
      const page = await browser.newPage();
      const errors = [];
      page.on('pageerror', error => errors.push(error.message));
      for (const [width, height] of [[1920,1080],[1536,864],[1366,768],[1024,768],[390,844],[320,640]]) {
        await page.setViewportSize({ width, height });
        await page.goto(process.env.SKIPLIST_URL || 'http://127.0.0.1:18795/settings');
        for (let attempt = 0; attempt < 3; attempt++) {
          await page.locator('.editor-tabs button').filter({ hasText: 'Transfers' }).click();
          try { await page.getByRole('heading', { name: 'Global Skiplist', exact: true }).waitFor({ timeout: 2000 }); break; }
          catch (error) { if (attempt === 2) throw error; }
        }
        const panel = page.locator('.panel-card').filter({ has: page.getByRole('heading', { name: 'Global Skiplist', exact: true }) });
        const rows = panel.locator('.wfx-skip-row:not(.wfx-skip-head)');
        const numbers = Array.from({ length: 18 }, (_, i) => String(i + 1));
        assert.deepEqual(await rows.locator('.wfx-skip-number').allTextContents(), numbers);
        const allow = rows.nth(1);
        await allow.getByTitle('Applies to directories', { exact: true }).uncheck();
        const kind = panel.getByRole('combobox', { name: 'Entry type' });
        const name = panel.getByRole('textbox', { name: 'Name or path to test' });
        const verdict = panel.getByRole('status');
        await name.fill('COVER');
        for (const value of ['dir','file','dir','file','dir']) {
          await kind.selectOption(value);
          await verdict.filter({ hasText: value === 'dir' ? 'Skipped by rule #17' : 'Allowed by rule #2' }).waitFor();
          assert.equal(await kind.inputValue(), value);
          assert.equal(await kind.evaluate(el => el.selectedOptions[0]?.textContent), value === 'dir' ? 'Dir' : 'File',
            channel + ': selected type label remains visible after rerender');
        }
        await allow.getByTitle('Applies to directories', { exact: true }).check();
        await verdict.filter({ hasText: 'Allowed by rule #2' }).waitFor();
        await name.fill('/release/COVER');
        await verdict.filter({ hasText: 'Allowed by rule #2' }).waitFor();
        await name.fill('/release/Sample/clip.mkv');
        await kind.selectOption('file');
        await verdict.filter({ hasText: 'Skipped by rule #1' }).waitFor();
        await name.fill('COVER');
        await kind.selectOption('dir');
        await allow.getByTitle('Move up', { exact: true }).click();
        await verdict.filter({ hasText: 'Allowed by rule #1' }).waitFor();
        assert.deepEqual(await rows.locator('.wfx-skip-number').allTextContents(), numbers, 'numbers follow current display order');
        await rows.first().getByTitle('Move down', { exact: true }).click();
        await verdict.filter({ hasText: 'Allowed by rule #2' }).waitFor();
        const pattern = rows.nth(1).getByRole('textbox', { name: 'Pattern', exact: true });
        await pattern.fill('[');
        await panel.getByText(/Invalid regex:/).waitFor();
        await pattern.fill(roots);
        await verdict.filter({ hasText: 'Allowed by rule #2' }).waitFor();
        assert(await panel.locator('.wfx-skip').evaluate(el => el.scrollWidth <= el.clientWidth + 1), 'numbered rules fit the editor');
        assert(await page.locator('.settings-page').evaluate(el => el.scrollWidth <= el.clientWidth + 1), 'no page overflow');
        // Save only this isolated fixture, then verify that flags/actions survive reload.
        await panel.getByRole('button', { name: /Save settings/ }).click();
        await page.getByText('Settings saved', { exact: true }).waitFor();
        console.log('PASS ' + channel + ' ' + width + 'x' + height + ': 18 numbered rules, stable File/Dir labels, scope/paths/order/validation/save');
      }
      await page.reload();
      for(let attempt = 0; attempt < 3; attempt++) {
        await page.locator('.editor-tabs button').filter({ hasText: 'Transfers' }).click();
        try { await page.getByRole('heading', { name: 'Global Skiplist', exact: true }).waitFor({ timeout: 2000 }); break; }
        catch (error) { if(attempt === 2) throw error; }
      }
      assert.equal(await page.locator('.wfx-skip-number').count(), 18);
      assert(await page.locator('.wfx-skip-row').nth(2).getByTitle('Applies to directories', { exact: true }).isChecked());
      assert.deepEqual(errors, []);
    } finally { await browser.close(); }
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
