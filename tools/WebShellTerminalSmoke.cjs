// 音频终端详情页冒烟：真的切成 terminal 主题，确认三区都在、相位图按真数据画、
// 没数据时只留空网格、退出终端主题时把采样订阅还回去。
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright-core');
const root = path.resolve(__dirname, '..');

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1426, height: 920 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const sent = [];
    await page.addInitScript(() => {
      const handlers = [];
      window.deliver = message => handlers.forEach(fn => fn({ data: { version: 1, ...message } }));
      window.__sent = [];
      window.chrome = { webview: {
        addEventListener: (_, fn) => handlers.push(fn),
        postMessage: message => {
          window.__sent.push(message.command + (message.enabled !== undefined ? ':' + message.enabled : ''));
          const reply = data => deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: true, data });
          if (message.command === 'theme') reply({ revision: 0, mode: 'dark', materialActive: false, light: { css: '', values: {} }, dark: { css: '', values: {} } });
          else if (message.command === 'nowPlaying') reply({ trackId: 'test', audio: { sampleRate: 48000, bitDepth: 24, kbps: 1411, channels: 2, fileSize: 52428800 }, lyricState: 'ready', currentIndex: 0, lines: [{ t: 0, text: '第一行' }, { t: 10, text: '第二行' }] });
          else if (message.command === 'nowPlayingVisual') {
            const n = 512, left = [], right = [];
            for (let i = 0; i < n; i++) { const v = Math.sin(i / 18); left.push(v * 0.7); right.push(v * 0.7); }
            reply({ sampleRate: 48000, available: true, samplesAvailable: false, samples: [], trackId: 'test',
              bands: Array.from({ length: 140 }, (_, i) => 0.2 + Math.sin(i / 6) ** 2 * 0.7),
              stereo: { available: true, trackId: 'test', generation: 1, sequence: 1, sampleRate: 48000, count: n, stage: 'post-dsp',
                left, right, rmsLeft: 0.4, rmsRight: 0.4, peakLeft: 0.8, peakRight: 0.8, correlation: 0.72 } });
          } else if (message.command === 'queue') reply({ revision: 3, total: 2, tracks: [{ id: 'test', index: 0, title: '第一首' }, { id: 'b', index: 1, title: '第二首' }] });
        }
      } };
      try { localStorage.setItem('celeste.now-playing.theme', 'terminal'); } catch {}
    });
    await page.goto(pathToFileURL(path.join(root, 'outputs/webview2-shell/index.html')).href);
    await page.waitForTimeout(400);

    // 打开详情页并让状态处于播放中（终端只有在真播放时才跑采样）
    await page.evaluate(() => {
      window.CelesteNowPlaying.open();
      deliver({ kind: 'celeste.state', state: { trackId: 'test', title: '测试曲', artist: 'T', album: 'A', duration: 130, position: 12, playing: true, quality: 'ALAC · 24bit/44.1kHz', source: '本地曲库', outputDevice: 'WASAPI · 独占' } });
    });
    await page.waitForTimeout(500);

    const theme = await page.evaluate(() => document.getElementById('now-playing').dataset.playerTheme);
    assert.equal(theme, 'terminal', '主题下拉应能切到 terminal');

    const visible = await page.locator('#np-terminal').isVisible();
    assert.ok(visible, '终端区应可见');

    // 三区仪表都必须在
    for (const id of ['term-stream', 'term-signal', 'term-file', 'term-device', 'np-terminal']) {
      assert.ok(await page.locator(`#${id}`).count() > 0, `缺少 ${id}`);
    }
    const columns = await page.evaluate(() => getComputedStyle(document.querySelector('.term-grid')).gridTemplateColumns.split(' ').length);
    assert.equal(columns, 3, '宽窗口下主区应为三栏');

    // 相位图：有真数据就必须画出轨迹
    const traces = await page.evaluate(() => document.querySelectorAll('#term-phase .ps-traces path').length);
    assert.ok(traces > 0, '有采样时相位图应画出轨迹');
    const stats = await page.textContent('#term-phase-stats');
    assert.ok(/ρ=0\.720/.test(stats), `相位读数应带相关系数，实际：${stats}`);

    // 频谱柱子、电平读数、歌词、队列
    assert.ok(await page.evaluate(() => document.querySelectorAll('#np-terminal .term-chart rect').length) > 0, '频谱应画出柱子');
    assert.ok(/dBFS/.test(await page.textContent('#term-levels')), '电平应给出 dBFS 读数');
    assert.ok((await page.textContent('#term-lyrics')).includes('第一行'), '歌词区应有内容');
    assert.ok((await page.textContent('#term-queue')).includes('第一首'), '队列区应列出真实顺序');

    // 播放控制条被搬进终端区底部（方案要求底部保留统一控制）
    const controlsInside = await page.evaluate(() => !!document.querySelector('#np-terminal .np-controls'));
    assert.ok(controlsInside, '底部播放控制应搬进终端区');

    // 订阅应已打开
    assert.ok(await page.evaluate(() => window.__sent.includes('stereoSampling:true')), '终端应订阅立体声采样');

    // 暂停 → 停止轮询并退订
    await page.evaluate(() => deliver({ kind: 'celeste.state', state: { trackId: 'test', title: '测试曲', duration: 130, position: 12, playing: false } }));
    await page.waitForTimeout(300);
    assert.ok(await page.evaluate(() => window.__sent.includes('stereoSampling:false')), '暂停/关闭应退订采样');

    // 切回默认主题：控制条要回原位
    await page.evaluate(() => { const s = document.getElementById('np-theme'); s.value = 'default'; s.dispatchEvent(new Event('change')); });
    await page.waitForTimeout(300);
    assert.equal(await page.evaluate(() => !!document.querySelector('#np-terminal .np-controls')), false, '切回默认主题后控制条应回到原处');

    // 窄窗口应先挪右栏再单列，不挤成小字仪表
    await page.evaluate(() => { const s = document.getElementById('np-theme'); s.value = 'terminal'; s.dispatchEvent(new Event('change')); });
    await page.setViewportSize({ width: 700, height: 900 });
    await page.waitForTimeout(250);
    assert.equal(await page.evaluate(() => getComputedStyle(document.querySelector('.term-grid')).gridTemplateColumns.split(' ').length), 1, '窄窗口应单列');

    assert.deepEqual(errors, [], '不应有页面报错');
    console.log('TERMINAL_SMOKE_OK');
  } finally { await browser.close(); }
})();
