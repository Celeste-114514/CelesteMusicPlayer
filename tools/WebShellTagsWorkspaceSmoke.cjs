// Full workspace, never a scroll-dependent subset. Uses a fake host; no real tags are written.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require('playwright-core');
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      const handlers = [];
      window.commands = []; window.partialTags = false;
      const tracks = Array.from({ length: 4031 }, (_, index) => ({ id: 'tag-' + index, title: 'Song ' + String(index).padStart(4, '0'), artist: 'Artist', album: 'Album',
        file: String(index).padStart(4, '0') + '.flac', genre: 'Genre', year: '2026', track: String(index + 1), duration: '1:00', format: 'FLAC', remote: false }));
      window.chrome = { webview: {
        addEventListener: (_, handler) => handlers.push(handler),
        postMessage: message => {
          window.commands.push(message);
          if (message.command !== 'tags' || !message.requestId) return;
          let data, delay = 5;
          if (message.operation === 'list') {
            let rows = tracks.filter(track => message.scopeIds.includes(track.id) && (!message.query || track.title.includes(message.query)));
            for (const rule of [...(message.sortRules || [])].reverse()) rows.sort((a, b) => String(a[rule.key] || '').localeCompare(String(b[rule.key] || '')) * (rule.descending ? -1 : 1));
            if (message.query === 'Song 000') delay = 200;
            data = { total: rows.length, offset: 0, rows: window.partialTags ? rows.slice(0, 200) : rows.slice(0, message.all ? 20000 : message.limit) };
          } else if (message.operation === 'read') data = { tracks: message.ids.map(id => ({ ...tracks.find(track => track.id === id), ok: true, comment: 'Full metadata' })) };
          else if (message.operation === 'write') data = { results: message.rows.map(row => ({ id: row.id, ok: true })) };
          setTimeout(() => handlers.forEach(handler => handler({ data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data } })), delay);
        }
      } };
    });
    await page.goto('file:///' + path.resolve(__dirname, '../outputs/webview2-shell/index.html').replaceAll('\\', '/'));
    await page.evaluate(async () => { await window.CelesteTags.open(Array.from({ length: 4031 }, (_, i) => 'tag-' + i)); });
    assert.equal(await page.locator('.tag-table').getAttribute('aria-rowcount'), '4032');
    assert(await page.locator('.tag-table tbody tr[data-id]').count() < 150);
    assert.equal(await page.getByRole('button', { name: '载入更多', exact: true }).count(), 0);
    assert((await page.locator('.tags-total').textContent()).includes('4,031'));
    const listRequests = await page.evaluate(() => commands.filter(command => command.command === 'tags' && command.operation === 'list'));
    assert(listRequests.every(command => command.all === true && command.offset === 0 && command.scopeIds.length === 4031));
    // Full table selection includes the final row without a scroll.
    await page.getByRole('checkbox', { name: '全选当前完整结果' }).uncheck();
    await page.getByRole('checkbox', { name: '全选当前完整结果' }).check();
    assert((await page.locator('.tags-selection').textContent()).includes('4031'));
    await page.getByRole('button', { name: '排序…', exact: true }).click();
    await page.getByRole('button', { name: '添加规则', exact: true }).click();
    await page.getByRole('combobox', { name: '第 1 排序方向' }).selectOption('desc');
    await page.getByRole('button', { name: '应用排序', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.tag-table tbody tr[data-id]')?.dataset.id === 'tag-4030');
    assert.equal(await page.locator('.tag-table').getAttribute('aria-rowcount'), '4032');
    assert(await page.locator('.tag-table tbody tr[data-id]').count() < 150);
    await page.locator('.tag-table-wrap').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await page.waitForFunction(() => [...document.querySelectorAll('.tag-table tbody tr[data-id]')].at(-1)?.dataset.id === 'tag-0');
    assert.equal(await page.evaluate(() => commands.filter(command => command.command === 'tags' && command.operation === 'list').length), listRequests.length + 1, 'scroll must never fetch another list page');
    const renderedHeight = await page.locator('.tag-table tbody tr[data-id]').first().evaluate(el => ({ actual: el.getBoundingClientRect().height, expected: parseFloat(getComputedStyle(el.closest('table')).getPropertyValue('--tag-row-height')) }));
    assert(Math.abs(renderedHeight.actual - renderedHeight.expected) < 1.1, 'virtual row geometry must match actual cells');
    await page.locator('.tag-table-wrap').evaluate(el => { el.scrollTop = 0; });
    // Numbering computes one global order, then honors the host's write cap with bounded messages.
    await page.getByRole('button', { name: '排序编号', exact: true }).click();
    await page.getByRole('button', { name: '开始编号（4031）', exact: true }).click();
    await page.waitForFunction(() => commands.filter(command => command.command === 'tags' && command.operation === 'write').reduce((n, command) => n + command.rows.length, 0) === 4031);
    const writes = await page.evaluate(() => commands.filter(command => command.command === 'tags' && command.operation === 'write').flatMap(command => command.rows));
    assert.equal(writes[0].id, 'tag-4030'); assert.equal(writes[0].fields.track, '1');
    assert.equal(writes.at(-1).id, 'tag-0'); assert.equal(writes.at(-1).fields.track, '4031');
    assert(writes.every(row => row.fields.trackCount === '4031'));
    await page.waitForFunction(() => document.querySelector('.tags-page').getAttribute('aria-busy') !== 'true');
    // Recycling visual rows must preserve unsaved text and its dirty marker.
    const firstTitle = page.locator('.tag-table tbody tr[data-id="tag-4030"] [data-field="title"]');
    await page.locator('.tag-table tbody tr[data-id="tag-4030"]').click();
    await firstTitle.click();
    await firstTitle.fill('Unsaved recycled title'); await firstTitle.press('Tab');
    await page.locator('.tag-table-wrap').evaluate(el => { el.scrollTop = el.scrollHeight; });
    await page.waitForFunction(() => [...document.querySelectorAll('.tag-table tbody tr[data-id]')].at(-1)?.dataset.id === 'tag-0');
    await page.locator('.tag-table-wrap').evaluate(el => { el.scrollTop = 0; });
    await page.waitForFunction(() => document.querySelector('.tag-table tbody tr[data-id="tag-4030"] [data-field="title"]')?.textContent === 'Unsaved recycled title');
    assert(await firstTitle.evaluate(el => el.classList.contains('edited')));
    // A truncated reply must not silently replace a previously complete table.
    await page.evaluate(() => { window.partialTags = true; });
    await page.getByRole('button', { name: '排序…', exact: true }).click();
    await page.getByRole('button', { name: '应用排序', exact: true }).click();
    await page.waitForFunction(() => document.getElementById('toast').textContent.includes('未完整返回'));
    assert.equal(await page.locator('.tag-table').getAttribute('aria-rowcount'), '4032');
    assert(await page.locator('.tag-table tbody tr[data-id]').count() < 150);
    // Last search wins, even when the old complete collection request finishes later.
    await page.evaluate(() => { window.partialTags = false; });
    const search = page.getByPlaceholder('搜索标题 / 艺术家 / 专辑 / 文件名');
    await search.fill('Song 000'); await page.waitForTimeout(280);
    await search.fill('Song 4030'); await page.waitForTimeout(550);
    assert.equal(await page.locator('.tag-table tbody tr[data-id]').count(), 1);
    assert.equal(await page.locator('.tag-table tbody tr[data-id]').first().getAttribute('data-id'), 'tag-4030');
    assert.deepEqual(errors, []);
    console.log('PASS 4031 complete model / bounded DOM / no load-more / select-all / global sort / bounded global numbering / recycled edit preservation / truncated-reply preservation / stale-search protection');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
