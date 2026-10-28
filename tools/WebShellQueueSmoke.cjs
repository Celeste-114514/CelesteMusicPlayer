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
      window.queuePayload = (offset = 0) => {
        const total = list.reduce((sum, item) => sum + item.duration, 0);
        const rest = list.slice(3).reduce((sum, item) => sum + item.duration, 0);
        return {
          revision: 7, total: list.length, offset, currentIndex: 2, order: 'ListLoop',
          summary: { totalSeconds: total, remainingSeconds: rest, hasCurrent: true },
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
    assert.equal(await page.locator('#queue-order [aria-pressed="true"]').getAttribute('data-order'), 'ListLoop');
    assert.equal(await page.locator('#queue-more').isVisible(), false);

    // ③ 搜索只在页内过滤，且过滤时不给拖
    await page.fill('#queue-search', '林澈');
    await page.waitForTimeout(120);
    assert.equal(await page.locator('#queue-list .queue-row').count(), 2);
    assert.equal(await page.locator('#queue-filter-note').textContent(), '匹配 2 首 · 清除搜索后可调整顺序');
    assert.equal(await page.locator('#queue-list').evaluate(node => node.classList.contains('is-filtered')), true);
    assert.equal(await page.locator('#queue-list .queue-row').first().evaluate(node => node.draggable), false);
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
    await page.evaluate(() => { document.querySelector('.main').scrollTop = 0; });
    await page.dragAndDrop('.queue-row[data-queue-index="5"]', '.queue-row[data-queue-index="1"]', { targetPosition: { x: 40, y: 4 } });
    await page.waitForTimeout(300);
    const moved = await page.evaluate(() => window.moved);
    assert.ok(moved, '拖动应发出 queue move');
    assert.equal(moved.from, 5);
    assert.equal(moved.to, 1);
    assert.equal(moved.trackId, 't5');
    assert.equal(moved.revision, 7);

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

    // ⑧ 空队列
    await page.setViewportSize({ width: 1440, height: 900 });
    await page.evaluate(() => {
      window.queuePayload = () => ({ revision: 8, total: 0, offset: 0, currentIndex: -1, order: 'ListLoop', summary: { totalSeconds: 0, remainingSeconds: 0, hasCurrent: false }, next: { kind: 'unknown' }, tracks: [] });
    });
    await go('songs'); await go('queue');
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#queue-summary').textContent(), '0 首');
    assert.equal(await page.locator('.queue-empty strong').textContent(), '播放队列是空的');
    assert.equal(await page.locator('#queue-list .queue-row').count(), 0);

    assert.deepEqual(errors, []);
    console.log('PASS: 队列独立页面、真实顺序与摘要、当前条目、下一首预览、页内搜索（过滤时禁拖）、定位当前、拖动 move 带 revision/身份、顺序按钮、窄窗不溢出、空队列。');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
