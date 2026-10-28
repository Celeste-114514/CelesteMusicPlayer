const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const { chromium } = require('playwright-core');
const root = path.resolve(__dirname, '..');
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'celeste-formal-'));
const host = spawn('dotnet', [path.join(__dirname, 'AppearanceRegression/bin/Release/net10.0/AppearanceRegression.dll'), temp]);
const pending = [];
readline.createInterface({ input: host.stdout }).on('line', line => {
  const item = pending.shift(), result = JSON.parse(line);
  result.ok ? item.resolve(result.data) : item.reject(new Error(result.error));
});
const rpc = message => new Promise((resolve, reject) => { pending.push({ resolve, reject }); host.stdin.write(JSON.stringify(message) + '\n'); });
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1426, height: 920 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.exposeFunction('__appearanceHost', rpc);
    await page.addInitScript(() => {
      const handlers = []; window.deliver = message => handlers.forEach(fn => fn({ data: { version: 1, ...message } }));
      window.chrome = { webview: {
        addEventListener: (_, fn) => handlers.push(fn),
        postMessage: async message => {
          if (message.command === 'theme') {
            try { deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: true, data: await __appearanceHost(message) }); }
            catch (error) { deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: false, error: error.message }); }
          } else if (message.command === 'nowPlaying') deliver({ kind: 'celeste.reply', requestId: message.requestId, ok: true, data: { trackId: 'test', lines: [{ t: 0, text: '歌词' }] } });
        }
      } };
      localStorage.removeItem('celeste.now-playing.theme');
    });
    await page.goto(pathToFileURL(path.join(root, 'outputs/webview2-shell/index.html')).href);
    await page.evaluate(() => {
      deliver({ kind: 'celeste.state', state: { trackId: 'test', title: 'Coldemark', artist: 'Bulb', album: 'Archives: Volume 5', duration: 130, position: 20, quality: 'ALAC · 24bit/44.1kHz', playing: false } });
      deliver({ kind: 'celeste.waveform', trackId: 'test', peaks: Array.from({ length: 1024 }, (_, i) => .1 + Math.sin(i * .07) ** 2 * .8) });
    });
    // 底栏波形有入场绘制：先等画布连续两帧一致再取样。
    // 不等的话第一次取样会拿到“画到一半”的画面，后三次是终态，比较结果会被误读成“缩放改了柱形”。
    await page.waitForFunction(() => {
      const canvas = document.getElementById('progress-waveform');
      if (!canvas || !canvas.width) return false;
      const data = canvas.getContext('2d').getImageData(0, 0, canvas.width, canvas.height).data;
      let sum = 0;
      for (let i = 0; i < data.length; i += 19) sum = (sum * 31 + data[i]) % 1000000007;
      const stable = sum !== 0 && window.__waveSum === sum;
      window.__waveSum = sum;
      return stable;
    }, null, { timeout: 5000 });
    const pixels = [];
    for (const width of [1426, 1920, 920, 720]) {
      await page.setViewportSize({ width, height: 920 }); await page.waitForTimeout(120);
      const box = await page.locator('#waveform-seek').boundingBox();
      assert.equal(box.width, 480, `底栏波形宽度固定 ${width}`); assert.equal(box.height, 34);
      pixels.push(await page.locator('#progress-waveform').evaluate(canvas => Array.from(canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data)));
    }
    assert.ok(pixels.every(value => value.length === pixels[0].length && value.every((channel,i)=>Math.abs(channel-pixels[0][i])<=2)), '缩放窗口不改变柱形尺寸、数量或采样分组');
    const runLengths=[];let previous=false,count=0;
    for(let x=0;x<480;x++){
      const filled=pixels[0][(17*480+x)*4+3]>128;
      if(filled===previous)count++;else{if(count)runLengths.push([previous,count]);previous=filled;count=1;}
    }
    assert.ok(runLengths.filter(([filled])=>filled).every(([,width])=>width===3),'每根波形线3px');
    assert.ok(runLengths.slice(1,-1).filter(([filled])=>!filled).every(([,width])=>width===2),'柱间留2px间距');
    await page.setViewportSize({ width: 1426, height: 920 });
    async function apply(message) {
      const data = await rpc(message);
      await page.evaluate(data => { deliver({ kind: 'celeste.theme', mode: data.resolved, presetId: data.presetId, light: data.light, dark: data.dark, revision: data.revision }); }, data);
      await page.waitForTimeout(150);
      return data;
    }
    for (const [id, material] of [['windows-fluent', 'windows'], ['apple-silver', 'apple']]) {
      await apply({ operation: 'select', presetId: id });
      assert.equal(await page.locator('html').getAttribute('data-material'), material);
      const shellBox=await page.locator('.shell').boundingBox(); assert.equal(shellBox.x,0); assert.equal(shellBox.y,0); assert.equal(shellBox.width,1426); assert.equal(shellBox.height,920,'外壳贴满窗口，不再嵌套留白');
      const backdrop = await page.locator('#transport-dialog').evaluate(e => getComputedStyle(e).backdropFilter);
      assert.match(backdrop, /blur\(/);
      await page.screenshot({ path: path.join(root, `outputs/formal-${material}-light.png`) });
      await page.click('#now-playing-open');
      for (const height of [920, 720]) {
        await page.setViewportSize({ width: 1426, height });
        await page.locator('#np-play').hover(); await page.waitForTimeout(180);
        const left = await page.locator('.np-left').boundingBox(), play = await page.locator('#np-play').boundingBox();
        assert.equal(await page.locator('.np-left').evaluate(e => getComputedStyle(e).overflowY), 'hidden');
        assert.ok(play.y + play.height <= left.y + left.height + 2, `悬停播放键仍完整可见 ${material} ${height}`);
      }
      await page.setViewportSize({ width: 920, height: 920 });
      assert.equal(await page.locator('#np-lyrics').isVisible(), true);
      await page.keyboard.press('Escape'); await page.waitForTimeout(250);
      await page.setViewportSize({ width: 1426, height: 920 });
      await apply({ operation: 'mode', mode: 'dark' });
      await page.screenshot({ path: path.join(root, `outputs/formal-${material}-dark.png`) });
      const reduced = await apply({ operation: 'preview', changes: { 'light:material-transparency': '0', 'dark:material-transparency': '0' } });
      assert.equal(await page.locator('html').getAttribute('data-reduced-transparency'), 'true');
      assert.equal(await page.locator('#transport-dialog').evaluate(e => getComputedStyle(e).backdropFilter), 'none');
      assert.equal(reduced.dark.values['material-transparency'], '0');
      await apply({ operation: 'cancel' });
      await apply({ operation: 'mode', mode: 'light' });
    }
    await apply({ operation: 'select', presetId: 'windows-fluent' });
    await apply({ operation: 'commit' });
    const saved = await apply({ operation: 'savePreset', name: 'Windows glass copy' });
    assert.equal(saved.light.values['material-style'], '1', '保存个人预设保留材质类型');
    await apply({ operation: 'select', presetId: 'apple-silver' });
    await apply({ operation: 'cancel' });
    assert.equal(await page.locator('html').getAttribute('data-material'), 'windows', '取消恢复先前已保存材质');
    assert.equal(await page.locator('#native-view').isVisible(), false, '不再提供原 UI 入口');
    assert.deepEqual(errors, []);
    console.log('PASS: fixed waveform pixels / Windows & Apple four appearances / frosted layers / hover without left scrolling / reduced transparency / saved preset material / cancel restoration / legacy entry hidden');
  } finally { await browser.close(); host.stdin.end(); }
})().catch(error => { console.error(error); host.kill(); process.exitCode = 1; });
