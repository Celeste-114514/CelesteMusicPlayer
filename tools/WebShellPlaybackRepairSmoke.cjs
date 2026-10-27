const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright-core');

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1426, height: 920 } });
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    page.setDefaultTimeout(6000);
    await page.route('https://celeste-cover.internal/**', route => route.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="768" height="768"><rect width="768" height="768" fill="#cbd8cf"/><circle cx="384" cy="384" r="210" fill="#6f897c"/></svg>' }));
    await page.addInitScript(() => {
      const handlers = [];
      window.commands = [];
      window.deliver = data => handlers.forEach(fn => fn({ data: { version: 1, ...data } }));
      window.chrome = { webview: {
        addEventListener: (_, fn) => handlers.push(fn),
        postMessage(message) {
          window.commands.push(message);
          if (message.command === 'nowPlaying') deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: true, data: {
            trackId: 'b', lines: Array.from({ length: 80 }, (_, i) => ({ t: i * 3, text: `第 ${i + 1} 句歌词，可选择复制与跳转`, tr: false }))
          } });
        }
      } };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../outputs/webview2-shell/index.html')).href);
    await page.evaluate(() => {
      deliver({ kind: 'celeste.library', phase: 'begin', revision: 1, total: 2 });
      deliver({ kind: 'celeste.library', phase: 'batch', revision: 1, tracks: ['a', 'b'].map(id => ({ id, title: `歌曲 ${id}`, artist: '艺术家', album: '专辑', duration: 240, chips: [], coverUrl: '' })) });
      deliver({ kind: 'celeste.library', phase: 'end', revision: 1 });
      deliver({ kind: 'celeste.state', state: { trackId: 'a', title: '歌曲 a', artist: '艺术家', album: '专辑', duration: 240, position: 32, playing: true } });
      CelesteShell.playTrack('b');
    });
    assert.equal(await page.locator('#track-title').textContent(), '歌曲 b', '选歌同一帧显示信息');
    assert.match(await page.locator('#track-quality').textContent(), /准备/);
    assert.equal(await page.locator('#play').getAttribute('aria-pressed'), 'false', '引擎确认前不冒充已播放');
    await page.evaluate(() => deliver({ kind: 'celeste.state', state: { trackId: 'a', title: '歌曲 a', playing: true, busy: true } }));
    assert.equal(await page.locator('#track-title').textContent(), '歌曲 b', '迟到的旧歌曲快照不能闪回');
    await page.evaluate(() => deliver({ kind: 'celeste.state', state: { trackId: 'b', title: '歌曲 b', playing: true, position: 0, duration: 240, coverUrl: 'https://celeste-cover.internal/cover/b' } }));
    const before = await page.locator('.workspace').boundingBox();
    await page.click('#chain-button');
    await page.waitForTimeout(300);
    assert.deepEqual(await page.locator('.workspace').boundingBox(), before, '音频链路不能挤占主界面');
    assert.equal(await page.locator('#chain-drawer').evaluate(e => getComputedStyle(e).position), 'fixed');
    await page.screenshot({ path: path.resolve(__dirname, '../outputs/playback-repair-chain.png') });
    await page.click('#chain-close');
    await page.waitForTimeout(300);
    const mini = await page.locator('.player .now-playing').evaluate(e => ({ direction: getComputedStyle(e).flexDirection, border: getComputedStyle(e).borderTopWidth }));
    assert.equal(mini.direction, 'row', '底栏封面和信息恢复横排');
    assert.equal(mini.border, '0px', '底栏歌曲区不能套详情卡片边框');
    await page.click('#now-playing-open');
    await page.waitForTimeout(350);
    assert.match(await page.locator('#np-cover img').getAttribute('src'), /size=768/);
    assert.equal(await page.locator('#np-play use').getAttribute('href'), '#i-pause');
    for (const width of [1426, 920, 720]) {
      await page.setViewportSize({ width, height: 920 });
      await page.waitForTimeout(100);
      const detail = await page.locator('#now-playing').boundingBox();
      const shell = await page.locator('.shell').boundingBox();
      assert.ok(Math.abs(detail.y - shell.y) < 2 && detail.height > shell.height - 4, '详情页覆盖整个外壳');
      const lyric = await page.locator('#np-lyrics').evaluate(e => ({ height: e.clientHeight, scroll: e.scrollHeight, select: getComputedStyle(e).userSelect }));
      assert.ok(lyric.height > 150 && lyric.scroll > lyric.height, `歌词形成独立滚动区域 ${width}`);
      assert.equal(lyric.select, 'text');
    }
    await page.setViewportSize({ width: 1426, height: 920 });
    await page.locator('#np-lyrics').hover();
    await page.mouse.wheel(0, 500);
    await page.waitForTimeout(250);
    assert.ok(await page.locator('#np-lyrics').evaluate(e => e.scrollTop) > 100, '真实滚轮能滚动');
    await page.locator('.np-lyric').nth(0).evaluate(e => e.scrollIntoView({ block: 'center', behavior: 'instant' }));
    await page.locator('.np-lyric').nth(0).click();
    assert.ok(await page.evaluate(() => commands.some(c => c.command === 'seek' && c.seconds === 0)), '第一句 0 秒也能跳转');
    await page.locator('.np-lyric').nth(1).evaluate(e => {
      const range = document.createRange(); range.selectNodeContents(e);
      getSelection().removeAllRanges(); getSelection().addRange(range);
      e.click();
    });
    assert.ok(!(await page.evaluate(() => commands.some(c => c.command === 'seek' && c.seconds === 3))), '选择文字不能误跳进度');
    await page.evaluate(() => getSelection().removeAllRanges());
    await page.locator('.np-lyric').nth(1).click();
    assert.ok(await page.evaluate(() => commands.some(c => c.command === 'seek' && c.seconds === 3)));
    await page.screenshot({ path: path.resolve(__dirname, '../outputs/playback-repair-detail.png') });
    await page.evaluate(() => { CelesteNowPlaying.close(); CelesteNowPlaying.open(); });
    await page.waitForTimeout(400);
    assert.equal(await page.locator('#now-playing').isVisible(), true, '旧退场动画不能关掉重开的详情');
    await page.evaluate(() => { CelesteMotion.setMode('off'); CelesteNowPlaying.close(); CelesteNowPlaying.open(); });
    assert.equal(await page.locator('#now-playing').isVisible(), true, '关闭动画模式仍可开关');
    assert.deepEqual(errors, []);
    console.log('PASS: 即时选歌 / 旧状态隔离 / 底栏恢复 / 链路悬浮 / 全屏详情 / 768px 封面 / 三种宽度滚动 / 选择歌词与跳转 / 动画重入和关闭模式');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
