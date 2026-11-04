const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright-core');

(async () => {
  const browser = await chromium.launch({ headless: true, channel: 'msedge' });
  try {
    const page = await browser.newPage({ viewport: { width: 1000, height: 600 } });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../outputs/webview2-shell/index.html')).href);
    const region = selector => page.locator(selector).first().evaluate(element =>
      getComputedStyle(element).getPropertyValue('app-region').trim());
    const windowState = (fullscreen, maximized = false) => page.evaluate(({ fullscreen, maximized }) => {
      document.dispatchEvent(new CustomEvent('celeste:message', {
        detail: { kind: 'celeste.window', fullscreen, maximized }
      }));
    }, { fullscreen, maximized });

    assert.equal(await region('.titlebar'), 'drag');
    for (const selector of ['.header-tools button', '.header-actions button', '.corner-toggle', '.corner-item', '[data-window="maximize"]']) {
      assert.equal(await region(selector), 'no-drag', `${selector} must stay interactive`);
    }
    await windowState(false, true);
    assert.equal(await region('.titlebar'), 'drag', 'Maximized titlebar must allow drag-to-restore');
    assert(await page.locator('body.is-maximized').count());

    await page.locator('#now-playing-open').evaluate(button => button.click());
    assert.equal(await region('.titlebar'), 'no-drag', 'Covered titlebar must not claim the player page');
    assert.equal(await region('.np-head'), 'drag');
    assert.equal(await region('#np-fullscreen'), 'no-drag');
    await windowState(true);
    assert.equal(await region('.np-head'), 'no-drag');
    assert.equal(await region('.titlebar'), 'no-drag');
    await windowState(false);
    assert.equal(await region('.np-head'), 'drag');
    console.log('PASS: production caption CSS, controls, maximized drag, player header and fullscreen');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
