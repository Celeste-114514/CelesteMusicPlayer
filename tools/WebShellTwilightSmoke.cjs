const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright-core');
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1496, height: 900 } });
    page.setDefaultTimeout(7000);
    const errors = [];
    page.on('pageerror', e => errors.push(e.message));
    await page.route('https://celeste-cover.internal/**', route => route.fulfill({ contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="768" height="768"><rect width="768" height="768" fill="#2b3947"/><circle cx="384" cy="330" r="180" fill="#d89972"/><path d="M0 600L250 330 500 680 768 420V768H0" fill="#728b82"/></svg>' }));
    await page.addInitScript(() => {
      localStorage.removeItem('celeste.now-playing.theme');
      const listeners = []; window.calls = []; window.revision = 1;
      window.queueItems = ['a', 'b', 'c']; window.currentQueueIndex = 1;
      window.deliver = data => listeners.forEach(fn => fn({ data: { version: 1, ...data } }));
      window.playback = { trackId: 'b', title: 'よりよい未来', artist: 'ATLAS GAME MUSIC', album: 'Vengeance · Original Soundtrack', source: 'LOCAL', quality: 'ALAC · 16-bit / 44.1 kHz · 537 kbps', duration: 112, position: 12, playing: true, coverUrl: 'https://celeste-cover.internal/cover/b' };
      window.chrome = { webview: {
        addEventListener: (_, fn) => listeners.push(fn),
        postMessage(message) {
          calls.push(message);
          if (message.command === 'window') { deliver({ kind: 'celeste.window', fullscreen: message.action === 'fullscreen' && message.value }); return; }
          if(message.command==='waveform')deliver({kind:'celeste.waveform',trackId:message.trackId,peaks:Array.from({length:512},(_,i)=>.15+Math.sin(i*.04)**2*.7)});
          if (!message.requestId) return;
          const reply = data => deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: true, data });
          if (message.command === 'nowPlaying') return reply({ trackId: 'b', audio:{sampleRate:44100,bitDepth:16,kbps:537,channels:2,fileSize:7800000}, lines: [{ t: 0, text: '测试歌词' }, { t: 8, text: '下一句歌词' }] });
          if (message.command === 'nowPlayingVisual') return reply({ trackId: 'b', available: true, samplesAvailable: true,
            bands: Array.from({ length: 140 }, (_, i) => .1 + Math.sin(i / 4) ** 2 * .65), samples: Array.from({ length: 256 }, (_, i) => Math.sin(i / 4) * .55) });
          if (message.command === 'queue') {
            if (message.operation === 'move') {
              if (message.revision !== revision) return deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: false, error: '播放队列已变化，请刷新后重新拖动。' });
              const playing = queueItems[currentQueueIndex];
              queueItems.splice(message.to, 0, queueItems.splice(message.from, 1)[0]);
              currentQueueIndex = queueItems.indexOf(playing); revision++;
            }
            return reply({ revision, total: queueItems.length, offset: 0, currentIndex: currentQueueIndex,
              tracks: queueItems.map((id, index) => ({ id, index, title: '歌曲 ' + id, artist: '测试艺术家', duration: 112 })) });
          }
          reply({});
        }
      } };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../outputs/webview2-shell/index.html')).href);
    await page.evaluate(() => deliver({ kind: 'celeste.state', state: playback }));
    const toggle = await page.locator('#orbit-toggle').boundingBox();
    const shell = await page.locator('.shell').boundingBox();
    assert.ok(toggle.y - shell.y < 36, '轮盘贴近整个窗口上界');
    await page.click('#orbit-toggle');
    await page.click('#queue');
    await page.waitForSelector('.web-queue-row');
    await page.locator('.web-queue-row').first().dragTo(page.locator('.web-queue-row').last(), { targetPosition: { x: 80, y: 48 } });
    await page.waitForTimeout(300);
    assert.deepEqual(await page.evaluate(() => queueItems), ['b', 'c', 'a'], '拖动真的发出重排请求');
    assert.equal(await page.locator('.web-queue-row[aria-current=true] strong').textContent(), '歌曲 b');
    await page.locator('.web-queue-row').first().press('Alt+ArrowDown');
    await page.waitForTimeout(150);
    assert.deepEqual(await page.evaluate(() => queueItems), ['c', 'b', 'a'], '键盘也可调整顺序');
    assert.equal(await page.locator('.web-queue-row[aria-current=true] strong').textContent(), '歌曲 b');
    await page.evaluate(() => revision++);
    await page.locator('.web-queue-row').first().press('Alt+ArrowDown');
    await page.waitForTimeout(150);
    assert.match(await page.locator('#transport-panel-status').textContent(), /已变化/);
    assert.deepEqual(await page.evaluate(() => queueItems), ['c', 'b', 'a'], '旧队列版本不能错误重排');
    await page.keyboard.press('Escape'); await page.waitForTimeout(250);
    await page.click('#now-playing-open');
    await page.selectOption('#np-theme', 'instrument');
    await page.waitForTimeout(350);
    assert.equal(await page.locator('#now-playing').getAttribute('data-player-theme'), 'instrument');
    assert.ok((await page.locator('.np-spectrum-signal').getAttribute('d')).includes('L'), '真实采样回复绘制频谱');
    assert.match(await page.locator('#np-visual-level').textContent(), /dBFS/);
    assert.equal(await page.locator('#np-fact-rate').textContent(), '44.1 kHz');
    assert.ok((await page.locator('.np-scope-signal').getAttribute('d')).length>100, '整曲波形实际绘制');
    const renderFps=await page.evaluate(()=>new Promise(resolve=>{
      let changes=0;const start=performance.now();
      const observer=new MutationObserver(records=>changes+=records.length);observer.observe(document.querySelector('.np-spectrum-signal'),{attributes:true,attributeFilter:['d']});
      setTimeout(()=>{observer.disconnect();resolve(changes*1000/(performance.now()-start));},800);
    }));
    assert.ok(renderFps>=24&&renderFps<=75,'频谱独立渲染且限制在约60fps'); console.log('Instrument render FPS: '+renderFps.toFixed(1));
    assert.equal(await page.locator('#np-theme option[value=instrument]').textContent(),'音频仪表');
    for (const width of [1496, 920, 720]) {
      await page.setViewportSize({ width, height: 900 }); await page.waitForTimeout(100);
      const chart = await page.locator('#np-spectrum').boundingBox(), cover = await page.locator('#np-cover').boundingBox();
      assert.ok(chart.width > 280 && chart.height > 80 && chart.x + chart.width <= width, `频谱可读且不越界 ${width}`);
      assert.ok(cover.width > 100 && cover.x + cover.width < chart.x, '封面与频谱互不覆盖');
      if (width === 1496) await page.screenshot({ path: path.resolve(__dirname, '../outputs/twilight-player-light.png') });
    }
    await page.setViewportSize({ width: 1496, height: 900 });
    await page.evaluate(() => document.documentElement.dataset.theme = 'dark');
    await page.screenshot({ path: path.resolve(__dirname, '../outputs/twilight-player-dark.png') });
    await page.click('#np-fullscreen');
    assert.equal(await page.locator('#np-fullscreen').getAttribute('aria-pressed'), 'true');
    await page.keyboard.press('Escape');
    assert.equal(await page.locator('#np-fullscreen').getAttribute('aria-pressed'), 'false');
    assert.equal(await page.locator('#now-playing').isVisible(), true, '先退全屏，详情保留');
    await page.keyboard.press('F11');
    assert.equal(await page.locator('#np-fullscreen').getAttribute('aria-pressed'), 'true');
    await page.keyboard.press('F11');
    await page.selectOption('#np-theme', 'default');
    await page.waitForTimeout(150);
    const stopped = await page.evaluate(() => calls.filter(c => c.command === 'nowPlayingVisual').length);
    await page.waitForTimeout(300);
    assert.equal(await page.evaluate(() => calls.filter(c => c.command === 'nowPlayingVisual').length), stopped, '离开仪表主题停止取样');
    assert.equal(await page.locator('#np-lyrics').isVisible(), true, '默认歌词页保留');
    await page.selectOption('#np-theme', 'instrument');
    await page.evaluate(() => deliver({ kind: 'celeste.state', state: { ...playback, playing: false } }));
    await page.waitForTimeout(100);
    assert.ok((await page.locator('.np-spectrum-signal').getAttribute('d')).length > 20, '暂停保留频谱画面');
    const paused = await page.evaluate(() => calls.filter(c => c.command === 'nowPlayingVisual').length);
    await page.waitForTimeout(250);
    assert.equal(await page.evaluate(() => calls.filter(c => c.command === 'nowPlayingVisual').length), paused);
    assert.deepEqual(errors, []);
    console.log('PASS: 轮盘位置 / 真实拖放与键盘队列排序 / 旧版本拒绝 / 当前播放保留 / 音频仪表三宽度与深浅色 / 频谱波形 / 主题切换停采样 / 暂停保留画面 / 全屏 F11 Esc');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
