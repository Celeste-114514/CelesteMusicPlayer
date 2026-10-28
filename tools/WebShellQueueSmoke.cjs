// 播放队列整页冒烟：独立页面（不再借用 browse）、真实队列顺序、当前条目标记、
// 搜索只在页内过滤且禁止拖动、定位当前、拖动改顺序带 revision 与身份、顺序按钮、
// 空队列、窄窗不横向溢出。
const assert = require('node:assert/strict'), path = require('node:path'), { pathToFileURL } = require('node:url');
let chromium; try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const TITLES = ['夜航西飞', '雨中的信号塔', 'Seventh Wave', '慢速快门', 'Aurora Line', '旧磁带', 'Harbor Lights', '空白页', 'Dust & Static'];
const ARTISTS = ['林澈', 'Northbound', 'Mono Lake', '林澈', 'Kite', '旧日电台', 'Northbound', 'Mono Lake', 'Kite'];
const DURATIONS = [215, 248, 301, 187, 264, 233, 279, 196, 322];

const tracks = TITLES.map((title, index) => ({
  id: 't' + index, index, title, artist: ARTISTS[index], album: '专辑 ' + index,
  duration: DURATIONS[index], format: index % 2 ? 'FLAC' : 'WAV', remote: false, coverUrl: ''
}));

function queuePayload(offset = 0) {
  const total = DURATIONS.reduce((sum, value) => sum + value, 0);
  const rest = DURATIONS.slice(3).reduce((sum, value) => sum + value, 0);
  return {
    revision: 7, total: tracks.length, offset, currentIndex: 2, order: 'ListLoop',
    summary: { totalSeconds: total, remainingSeconds: rest, hasCurrent: true },
    previous: { kind: 'track', index: 1, title: tracks[1].title, artist: tracks[1].artist, album: tracks[1].album, duration: tracks[1].duration, coverUrl: '' },
    next: { kind: 'track', index: 3, title: tracks[3].title, artist: tracks[3].artist, album: tracks[3].album, duration: tracks[3].duration, coverUrl: '' },
    tracks: tracks.slice(offset, offset + 200)
  };
}

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } }), errors = [];
    page.on('pageerror', error => errors.push(error.message));

    // 宿主替身：记录网页发出的命令，队列 get 返回分页数据
    await page.addInitScript(list => {
      window.commands = [];
      window.__queueTracks = list;
      window.queuePayload = (offset = 0) => {
        const total = list.reduce((sum, item) => sum + item.duration, 0);
        const rest = list.slice(3).reduce((sum, item) => sum + item.duration, 0);
        return {
          revision: 7, total: list.length, offset, currentIndex: 2, order: 'ListLoop',
          summary: { totalSeconds: total, remainingSeconds: rest, hasCurrent: true },
          previous: { kind: 'track', index: 1, title: list[1].title, artist: list[1].artist, album: list[1].album, duration: list[1].duration, coverUrl: '' },
          next: { kind: 'track', index: 3, title: list[3].title, artist: list[3].artist, album: list[3].album, duration: list[3].duration, coverUrl: '' },
          tracks: list.slice(offset, offset + 200)
        };
      };
      window.chrome ||= {};
      window.chrome.webview = {
        addEventListener: (_, callback) => { window.deliver = data => callback({ data }); },
        postMessage: message => {
          window.commands.push(message);
          if (!message.requestId) return;
          let data = { message: 'ok' };
          if (message.command === 'queue') {
            if (message.operation === 'get') data = window.queuePayload(message.offset || 0);
            if (message.operation === 'move') window.moved = message;
            if (message.operation === 'play') window.played = message;
          }
          setTimeout(() => window.deliver({ kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }), 5);
        }
      };
    }, tracks);

    await page.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href);
    await page.evaluate(() => {
      deliver({ kind: 'celeste.state', version: 1, state: {
        trackId: 't2', title: 'Seventh Wave', artist: 'Mono Lake', album: '专辑 2', duration: 301, position: 78.4,
        playing: true, favorite: true, playbackOrder: 'ListLoop', quality: 'FLAC · 24bit/44.1kHz · 1411kbps',
        source: 'LOCAL', coverUrl: '', volume: 62, volumeEnabled: true, desktopLyrics: false } });
    });

    const go = async name => {
      await page.evaluate(name => CelesteShell.navigate(name), name);
      await page.waitForFunction(() => document.getAnimations().filter(a => a.playState === 'running').length === 0).catch(() => { });
      await page.waitForTimeout(120);
    };

    // ① 独立页面：队列走 #queue-page，不再借用 #browse-page
    await go('queue');
    assert.equal(await page.locator('#queue-page').isVisible(), true, '队列页应可见');
    assert.equal(await page.locator('#browse-page').isVisible(), false, '队列不应再渲染在 browse 页里');
    assert.deepEqual(await page.locator('#breadcrumb button').allTextContents(), ['主页', '播放队列']);

    // ② 列表、摘要、当前条目、下一首
    await page.waitForSelector('#queue-list .queue-row');
    assert.equal(await page.locator('#queue-list .queue-row').count(), 9);
    assert.equal(await page.locator('#queue-summary').textContent(), '共 9 首 · 总时长 37 分钟 · 之后还有 25 分钟');
    assert.equal(await page.locator('#queue-list .queue-row[aria-current="true"]').getAttribute('data-queue-index'), '2');
    assert.equal(await page.locator('#queue-now-title').textContent(), 'Seventh Wave');
    assert.equal(await page.locator('#queue-now-quality').textContent(), 'FLAC · 24bit/44.1kHz · 1411kbps');
    assert.equal(await page.locator('#queue-next-title').textContent(), '慢速快门');
    assert.equal(await page.locator('#queue-prev-title').textContent(), '雨中的信号塔');
    assert.equal(await page.locator('#queue-prev-meta').textContent(), 'Northbound · 4:08');
    assert.equal(await page.locator('#queue-order [aria-pressed="true"]').getAttribute('data-order'), 'ListLoop');
    assert.equal(await page.locator('#queue-more').isVisible(), false);
    // 位置：上一首在正在播放左边，下一首在右边（两块对称）
    const band = await page.evaluate(() => {
      const box = id => document.getElementById(id).getBoundingClientRect();
      return { prev: box('queue-prev-card').right, cover: box('queue-now-cover').left, copy: box('queue-now-title').left, next: box('queue-next-card').left };
    });
    assert.ok(band.prev < band.cover, '上一首卡片应在正在播放的左边');
    assert.ok(band.next > band.copy, '下一首卡片应在正在播放的右边');

    // ②b 点上一首：交给宿主切歌，网页不自己算"上一首是谁"
    await page.locator('#queue-prev-card').click();
    await page.waitForTimeout(700);
    assert.equal(await page.evaluate(() => window.commands.filter(m => m.command === 'previous').length), 1, '点上一首应发一条 previous');

    // ③ 搜索只在页内过滤，且过滤时不给拖
    await page.fill('#queue-search', '林澈');
    await page.waitForTimeout(120);
    assert.equal(await page.locator('#queue-list .queue-row').count(), 2);
    assert.equal(await page.locator('#queue-filter-note').textContent(), '匹配 2 首 · 清除搜索后可调整顺序');
    assert.equal(await page.locator('#queue-list').evaluate(node => node.classList.contains('is-filtered')), true);
    assert.equal(await page.locator('#queue-list .queue-row').first().evaluate(node => getComputedStyle(node.querySelector('.queue-grip')).visibility), 'hidden', '过滤时握柄应收起');
    // 过滤时按住拖：不该出现跟手浮层，也不该给宿主发 move
    const filteredBox = await page.locator('#queue-list .queue-row').first().boundingBox();
    await page.mouse.move(filteredBox.x + 120, filteredBox.y + filteredBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(filteredBox.x + 120, filteredBox.y + filteredBox.height + 40, { steps: 6 });
    assert.equal(await page.locator('.queue-ghost').count(), 0, '搜索过滤时不该能拖');
    await page.mouse.up();
    assert.equal(await page.evaluate(() => window.moved), undefined, '搜索过滤时不该发 move');
    await page.fill('#queue-search', '');
    await page.waitForTimeout(120);
    assert.equal(await page.locator('#queue-list .queue-row').count(), 9);

    // ④ 定位当前：滚动到当前条目并高亮一下
    await page.evaluate(() => { document.querySelector('.main').scrollTop = 0; });
    await page.locator('#queue-locate').click();
    await page.waitForTimeout(500);
    const located = page.locator('#queue-list .queue-row.is-located');
    assert.equal(await located.count(), 1);
    assert.equal(await located.getAttribute('data-queue-index'), '2');

    // ⑤ 拖动改顺序：带上 revision 与身份，第 6 行拖到第 2 行
    // 先把要拖的那一行滚到列表中间：底栏会盖住最下面，落点必须真的在可视区里
    await page.locator('.queue-row[data-queue-index="5"]').evaluate(node => node.scrollIntoView({ block: 'center' }));
    await page.waitForTimeout(120);
    const fromBox = await page.locator('.queue-row[data-queue-index="5"]').boundingBox();
    const toBox = await page.locator('.queue-row[data-queue-index="1"]').boundingBox();
    await page.mouse.move(fromBox.x + 120, fromBox.y + fromBox.height / 2);
    await page.mouse.down();
    // 走过 4px 阈值才算开始拖：先挪一点点，浮层应出现
    await page.mouse.move(fromBox.x + 120, fromBox.y + fromBox.height / 2 - 10, { steps: 3 });
    assert.equal(await page.locator('.queue-ghost').count(), 1, '拖动中应有一份跟手的浮层');
    assert.equal(await page.locator('.queue-row.is-dragging').getAttribute('data-queue-index'), '5');
    await page.mouse.move(toBox.x + 120, toBox.y + 6, { steps: 10 });
    assert.equal(await page.locator('.queue-row.drop-before, .queue-row.drop-after').count(), 1, '拖动中应有一条落点横线');
    await page.mouse.up();
    await page.waitForTimeout(300);
    assert.equal(await page.locator('.queue-ghost').count(), 0, '松手后跟手浮层要收掉');
    assert.equal(await page.locator('.queue-row.is-dragging').count(), 0, '松手后源行的淡化要收掉');
    const moved = await page.evaluate(() => window.moved);
    assert.ok(moved, '拖动应发出 queue move');
    assert.equal(moved.from, 5);
    assert.equal(moved.to, 1);
    assert.equal(moved.trackId, 't5');
    assert.equal(moved.revision, 7);

    // ⑤b 只是单击（没越过阈值）：不该发 move
    await page.evaluate(() => { window.moved = undefined; });
    await page.locator('.queue-row[data-queue-index="4"]').evaluate(node => node.scrollIntoView({ block: 'center' }));
    await page.waitForTimeout(120);
    const clickBox = await page.locator('.queue-row[data-queue-index="4"]').boundingBox();
    await page.mouse.move(clickBox.x + 120, clickBox.y + clickBox.height / 2);
    await page.mouse.down();
    await page.mouse.move(clickBox.x + 122, clickBox.y + clickBox.height / 2 + 1);
    await page.mouse.up();
    await page.waitForTimeout(150);
    assert.equal(await page.evaluate(() => window.moved), undefined, '轻轻一点不该改变队列顺序');

    // ⑥ 顺序按钮：只该发一条命令（shell.js 已统一管 [data-order]，队列页不再重复挂监听）
    await page.locator('#queue-order [data-order="Random"]').click();
    await page.waitForTimeout(80);
    const orderCommands = await page.evaluate(() => window.commands.filter(m => m.command === 'setPlaybackOrder'));
    assert.equal(orderCommands.length, 1, `一次点击发出了 ${orderCommands.length} 条 setPlaybackOrder`);
    assert.equal(orderCommands.at(-1).order, 'Random');

    // ⑦ 窄窗不横向溢出，卡片不压列表
    for (const width of [1440, 1080, 860]) {
      await page.setViewportSize({ width, height: 720 });
      await page.waitForTimeout(200);
      const fit = await page.evaluate(() => {
        const main = document.querySelector('.main'), card = document.querySelector('.queue-now').getBoundingClientRect();
        const list = document.getElementById('queue-list').getBoundingClientRect();
        return { overflow: main.scrollWidth - main.clientWidth, listTop: Math.round(list.top - main.getBoundingClientRect().top), cardBottom: Math.round(card.bottom - main.getBoundingClientRect().top) };
      });
      assert.ok(fit.overflow <= 0, `窗口 ${width}px 出现横向滚动：${fit.overflow}`);
      assert.ok(fit.listTop >= fit.cardBottom, `窗口 ${width}px 卡片压住列表`);
    }

    // ⑧ 上一首的边界：顺序播放停在第一首，明确说"没有上一首"，且点了不发命令
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => {
      window.commands.length = 0;
      window.queuePayload = (offset = 0) => ({
        revision: 9, total: 9, offset, currentIndex: 0, order: 'Sequential',
        summary: { totalSeconds: 0, remainingSeconds: 0, hasCurrent: true },
        previous: { kind: 'none' }, next: { kind: 'track', index: 1, title: '雨中的信号塔', artist: 'Northbound', duration: 248, coverUrl: '' },
        tracks: window.__queueTracks.slice(offset, offset + 200)
      });
    });
    await go('songs'); await go('queue');
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#queue-prev-card').isVisible(), true);
    assert.equal(await page.locator('#queue-prev-title').textContent(), '没有上一首');
    assert.equal(await page.locator('#queue-prev-meta').textContent(), '已经是队列的第一首');
    assert.equal(await page.locator('#queue-prev-card').getAttribute('aria-disabled'), 'true');
    await page.locator('#queue-prev-card').click({ force: true });   // aria-disabled 会被 Playwright 当成不可点，这里就是要验证"点了也不发命令"

    await page.waitForTimeout(120);
    assert.equal(await page.evaluate(() => window.commands.filter(m => m.command === 'previous').length), 0, '没有上一首时不该发 previous');

    // ⑨ 空队列
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => {
      window.queuePayload = () => ({ revision: 8, total: 0, offset: 0, currentIndex: -1, order: 'ListLoop', summary: { totalSeconds: 0, remainingSeconds: 0, hasCurrent: false }, next: { kind: 'unknown' }, tracks: [] });
    });
    await go('songs'); await go('queue');
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#queue-summary').textContent(), '0 首');
    assert.equal(await page.locator('.queue-empty strong').textContent(), '播放队列是空的');
    assert.equal(await page.locator('#queue-list .queue-row').count(), 0);
    assert.equal(await page.locator('#queue-prev-card').isVisible(), false, '没在播时上一首整块收掉');
    assert.equal(await page.locator('#queue-next-card').isVisible(), false, '没在播时下一首整块收掉');

    assert.deepEqual(errors, []);
    console.log('PASS: 队列独立页面、真实顺序与摘要、当前条目、上一首/下一首对称预览（上一首点击切歌、第一首时明确没有）、页内搜索（过滤时禁拖）、定位当前、指针拖动（浮层/落点线/单击不改序）带 revision 与身份、顺序按钮、窄窗不溢出、空队列。');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
