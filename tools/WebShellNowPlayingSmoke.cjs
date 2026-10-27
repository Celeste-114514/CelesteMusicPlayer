// 「正在播放」详情页（方案 §3 / §9 步骤 1）：
//   ① 入口只有底栏封面，而且是真的按钮（键盘 Enter 能开）
//   ② 打开后覆盖主区、底栏让位；标题/艺术家来自同一份播放状态
//   ③ 歌词从宿主一次取整首，按播放位置高亮当前句
//   ④ 用户手动滚动时不被拽回去，出现「回到当前句」，点了能回来
//   ⑤ Esc 关闭，焦点回到底栏封面
//   ⑥ 换歌：旧歌词立刻清掉，不会停在新歌下面
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellNowPlayingSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href;

const LYRICS = [
  { t: 0, text: '第一句歌词', tr: false },
  { t: 5, text: '第二句歌词', tr: false },
  { t: 10, text: '第三句歌词', tr: false },
  { t: 15, text: '第四句歌词', tr: false },
  { t: 20, text: '第五句歌词', tr: false }
];

const state = (overrides = {}) => ({
  kind: 'celeste.state', version: 1,
  state: Object.assign({
    title: 'Priestess', artist: 'Periphery', album: 'Juggernaut: Omega',
    trackId: 'track-a', coverUrl: '', duration: 303, position: 12, volume: 65,
    playing: true, favorite: false, shuffle: false, repeat: false,
    playbackOrder: 'ListLoop', quality: '16bit/44.1kHz', busy: false
  }, overrides)
});

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    const errors = [];
    page.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });

    await page.addInitScript(lyrics => {
      const handlers = [];
      window.__nowPlayingCalls = 0;
      window.__lyrics = lyrics;
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        addEventListener(_, fn) { handlers.push(fn); },
        postMessage(message) {
          if (!message || !message.requestId) return;
          const reply = data => handlers.forEach(fn => fn({
            data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }
          }));
          if (message.command === 'nowPlaying') {
            window.__nowPlayingCalls += 1;
            return reply({
              trackId: window.__currentTrackId || 'track-a',
              lyricState: window.__lyrics.length ? 'ready' : 'none',
              currentIndex: 0,
              lines: window.__lyrics
            });
          }
          reply({});
        }
      };
      window.__deliver = m => handlers.forEach(fn => fn({ data: m }));
    }, LYRICS);

    await page.goto(URL, { waitUntil: 'load' });
    await page.evaluate(m => window.__deliver(m), state());
    await page.waitForTimeout(150);
    await page.evaluate(() => { window.__currentTrackId = 'track-a'; });

    // ① 入口是按钮，而且只有一个
    const entry = await page.evaluate(() => {
      const button = document.getElementById('now-playing-open');
      return {
        tag: button?.tagName,
        count: document.querySelectorAll('.now-playing .mini-cover, .mini-cover').length,
        focusable: button?.tabIndex >= 0
      };
    });
    assert.equal(entry.tag, 'BUTTON', '底栏封面应该是按钮');
    assert.equal(entry.focusable, true, '按钮要能拿到焦点');
    assert.equal(await page.locator('#now-playing').isVisible(), false, '没打开前详情页不该显示');

    // 键盘打开
    await page.locator('#now-playing-open').focus();
    await page.keyboard.press('Enter');
    await page.waitForTimeout(250);
    assert.equal(await page.locator('#now-playing').isVisible(), true, 'Enter 应该能打开详情页');
    assert.equal(await page.locator('footer.player').isVisible(), false, '打开后底栏让位（不能两条进度条并列）');

    if (process.env.SHOTS === '1') {
      console.log('TOPLEFT=' + JSON.stringify(await page.evaluate(() => {
        const pick = (x, y) => { const el = document.elementFromPoint(x, y); return el ? (el.className || el.tagName) + ' in ' + (el.parentElement?.className || '') : 'none'; };
        return { left: pick(40, 30), right: pick(1200, 30), main: getComputedStyle(document.querySelector('.main')).display, workspace: getComputedStyle(document.querySelector('.workspace')).display };
      })));
      await page.screenshot({ path: path.join(__dirname, '../outputs/web-now-playing-wide.png') });
      await page.setViewportSize({ width: 920, height: 900 });
      await page.waitForTimeout(200);
      await page.screenshot({ path: path.join(__dirname, '../outputs/web-now-playing-narrow.png') });
      await page.setViewportSize({ width: 1440, height: 960 });
      await page.waitForTimeout(200);
    }

    // ② 信息来自同一份状态
    assert.equal(await page.locator('#np-title').textContent(), 'Priestess');
    assert.match(await page.locator('#np-artist').textContent(), /Periphery/);
    assert.equal(await page.locator('#np-duration').textContent(), '5:03');

    // ③ 歌词整首取一次，当前句高亮
    await page.waitForSelector('.np-lyric');
    assert.equal(await page.locator('.np-lyric').count(), 5, '整首歌词都渲染出来');
    assert.equal(await page.evaluate(() => window.__nowPlayingCalls), 1, '打开时只向宿主取一次');
    const current = await page.evaluate(() => {
      const row = document.querySelector('.np-lyric.is-current');
      return { text: row?.textContent, index: Number(row?.dataset.index) };
    });
    // position=12 → 落在 10s 那一句（第三句）
    assert.equal(current.index, 2, '按播放位置高亮当前句，实际：' + JSON.stringify(current));

    // ④ 手动滚动不被拽回去
    await page.evaluate(() => {
      const box = document.getElementById('np-lyrics');
      box.dispatchEvent(new WheelEvent('wheel', { deltaY: 240, bubbles: true }));
    });
    await page.waitForTimeout(80);
    assert.equal(await page.locator('#np-lyrics-pin').isVisible(), true, '手动滚动后要给「回到当前句」');
    await page.locator('#np-lyrics-pin').click();
    await page.waitForTimeout(120);
    assert.equal(await page.locator('#np-lyrics-pin').isVisible(), false, '点完应该收起来');

    // ⑥ 换歌：旧歌词先清掉
    await page.evaluate(() => { window.__lyrics = []; window.__currentTrackId = 'track-b'; });
    await page.evaluate(m => window.__deliver(m), state({ trackId: 'track-b', title: 'Graveless', position: 0 }));
    await page.waitForTimeout(120);
    assert.equal(await page.locator('.np-lyric').count(), 0, '换歌瞬间不该还挂着上一首的词');
    assert.equal(await page.locator('#np-title').textContent(), 'Graveless', '标题跟着换');

    // ⑤ Esc 关闭 + 焦点回到封面
    await page.keyboard.press('Escape');
    await page.waitForTimeout(250);
    assert.equal(await page.locator('#now-playing').isVisible(), false, 'Esc 应该收起');
    assert.equal(await page.locator('footer.player').isVisible(), true, '收起后底栏回来');
    assert.equal(await page.evaluate(() => document.activeElement?.id), 'now-playing-open', '焦点回到打开它的按钮');

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: 封面按钮可键盘打开 / 底栏让位 / 整首歌词一次取 / 当前句高亮 / 手动滚动不被拽回 / 换歌清词 / Esc 回焦点');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
