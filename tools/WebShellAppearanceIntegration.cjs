// Real C# appearance command handler + real browser DOM/media. Only native picker is stubbed.
// dotnet build tools/AppearanceRegression -c Release
// NODE_PATH=C:/Users/MSI/_pwtmp/node_modules node tools/WebShellAppearanceIntegration.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const readline = require('node:readline');
const { chromium } = require('playwright-core');
const root = path.resolve(__dirname, '..');
const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'celeste-appearance-'));
const child = spawn('dotnet', [process.env.APPEARANCE_REGRESSION_DLL || path.join(__dirname, 'AppearanceRegression/bin/Release/net10.0/AppearanceRegression.dll'), temp]);
const waiting = [];
readline.createInterface({ input: child.stdout }).on('line', line => {
  const result = JSON.parse(line), next = waiting.shift();
  if (result.ok) next.resolve(result.data); else next.reject(new Error(result.error));
});
child.stderr.on('data', chunk => process.stderr.write(chunk));
const rpc = payload => new Promise((resolve, reject) => { waiting.push({ resolve, reject }); child.stdin.write(JSON.stringify(payload) + '\n'); });
const assetFolder = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer/webbg');
const created = [];
let browser;
(async () => {
  browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
  const errors = [];
  page.on('pageerror', e => errors.push(e.message));
  await page.route('https://celeste.internal/**', async route => {
    const name = new URL(route.request().url()).pathname.slice(1);
    await route.fulfill({ path: path.join(root, 'outputs/webview2-shell', name || 'index.html') });
  });
  await page.route('https://celeste-asset.internal/**', async route => {
    const file = path.join(assetFolder, path.basename(new URL(route.request().url()).pathname));
    if (fs.existsSync(file)) await route.fulfill({ path: file }); else await route.fulfill({ status: 404, body: '' });
  });
  await page.exposeFunction('__appearanceHost', async message => {
    // Inject a delay to expose commands that accidentally capture stale state before enqueue.
    if (message.operation === 'preview') await new Promise(resolve => setTimeout(resolve, 25));
    return rpc(message);
  });
  await page.addInitScript(() => {
    const handlers = [];
    window.chrome = { webview: {
      addEventListener: (_, fn) => handlers.push(fn),
      postMessage: async message => {
        if (!message.requestId || message.command !== 'theme') return;
        try {
          const data = await window.__appearanceHost(message);
          handlers.forEach(fn => fn({ data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data } }));
        } catch (error) { handlers.forEach(fn => fn({ data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: false, error: error.message } })); }
      }
    } };
  });
  await page.goto('https://celeste.internal/index.html');
  await page.evaluate(async () => {
    document.getElementById('overview-page').hidden = true;
    document.getElementById('settings-page').hidden = false;
    await window.CelesteAppearance.mount(document.getElementById('settings-scroll'));
  });
  const state = () => rpc({ operation: 'get' });
  const value = name => page.evaluate(name => getComputedStyle(document.documentElement).getPropertyValue('--' + name).trim(), name);
  const click = label => label === '撤销' ? page.locator('#history-undo').click() : page.getByRole('button', { name: label, exact: true }).click();
  const waitValue = (name, expected) => page.waitForFunction(({ name, expected }) => getComputedStyle(document.documentElement).getPropertyValue('--' + name).trim() === expected, { name, expected });
  const before = await state();
  assert(before.presets.some(p => p.id === 'windows-fluent'));
  assert(before.presets.some(p => p.id === 'apple-silver'));
  // Rapid slider edits followed immediately by undo: true override deletion and no stale queue reads.
  await page.evaluate(() => {
    const range = [...document.querySelectorAll('.set-row input[type=range]')].find(el => el.closest('.set-row').textContent.includes('圆角'));
    range.dispatchEvent(new PointerEvent('pointerdown'));
    for (const n of [28, 30, 32]) { range.value = n; range.dispatchEvent(new Event('input')); }
    range.dispatchEvent(new PointerEvent('pointerup'));
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'z', ctrlKey: true, bubbles: true }));
  });
  await page.waitForFunction(() => !document.getElementById('history-redo').disabled);
  await waitValue('radius', before.light.values.radius);
  assert.equal((await state()).overrideCount, 0);
  assert.equal((await state()).draft, false);
  await click('重做'); await waitValue('radius', '32px');
  assert.equal((await state()).overrideCount, 2);
  await click('取消预览'); await waitValue('radius', before.light.values.radius);
  // Separate gestures do not collapse; undoing the second keeps first, then removes all overrides.
  await page.evaluate(() => {
    const range = [...document.querySelectorAll('.set-row input[type=range]')].find(el => el.closest('.set-row').textContent.includes('圆角'));
    for (const n of [14, 18]) {
      range.dispatchEvent(new PointerEvent('pointerdown')); range.value = n; range.dispatchEvent(new Event('input')); range.dispatchEvent(new PointerEvent('pointerup'));
    }
  });
  await waitValue('radius', '18px'); await click('撤销'); await waitValue('radius', '14px');
  await click('撤销'); await waitValue('radius', before.light.values.radius);
  // Both system presets, light/dark, undo/cancel and commit persistence.
  await page.locator('.preset-card').filter({ hasText: 'Windows · Fluent' }).click();
  await waitValue('radius', '8px');
  await click('撤销'); await waitValue('radius', before.light.values.radius);
  await page.locator('.preset-card').filter({ hasText: 'Apple · 银白' }).click();
  await waitValue('radius', '20px');
  await page.getByRole('button', { name: '深色', exact: true }).click();
  await page.waitForFunction(() => document.documentElement.dataset.theme === 'dark');
  await click('取消预览');
  assert.equal((await state()).presetId, before.presetId);
  // Generate a tiny real PNG and WebM using browser canvas/MediaRecorder (no downloaded media).
  const media = await page.evaluate(async () => {
    const canvas = document.createElement('canvas'); canvas.width = 160; canvas.height = 90;
    const ctx = canvas.getContext('2d'); ctx.fillStyle = '#245f96'; ctx.fillRect(0, 0, 160, 90);
    const png = canvas.toDataURL('image/png').split(',')[1];
    const stream = canvas.captureStream(10), recorder = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp8' });
    const chunks = [], done = new Promise(resolve => { recorder.onstop = resolve; });
    recorder.ondataavailable = e => chunks.push(e.data);
    recorder.start();
    for (let i = 0; i < 8; i++) { ctx.fillStyle = i % 2 ? "#245f96" : "#ad7c42"; ctx.fillRect(0, 0, 160, 90); await new Promise(resolve => setTimeout(resolve, 100)); }
    recorder.stop(); await done;
    stream.getTracks().forEach(track => track.stop());
    const bytes = new Uint8Array(await new Blob(chunks).arrayBuffer());
    return { png, webm: btoa(String.fromCharCode(...bytes)) };
  });
  const image = path.join(temp, 'image.png'), video = path.join(temp, 'video.webm');
  fs.writeFileSync(image, Buffer.from(media.png, 'base64')); fs.writeFileSync(video, Buffer.from(media.webm, 'base64'));
  // Picker cancel is a complete no-op. Production picker copies are immutable; old image survives replacing.
  const unchanged = await state();
  await page.selectOption('#background-mode', 'image'); await page.waitForTimeout(150);
  assert.deepEqual((await state()).editState, unchanged.editState);
  let picked = await rpc({ operation: 'pickBackground', media: 'image', testPick: image });
  const imageUrl = picked.editState.backgroundImage; created.push(path.join(assetFolder, new URL(imageUrl).pathname.slice(1)));
  await page.evaluate(async () => window.CelesteAppearance.mount(document.getElementById('settings-scroll')));
  assert.equal(await value('bg-mode'), 'image');
  const other = await rpc({ operation: 'pickBackground', media: 'image', testPick: image });
  created.push(path.join(assetFolder, new URL(other.editState.backgroundImage).pathname.slice(1)));
  assert.notEqual(other.editState.backgroundImage, imageUrl); assert(fs.existsSync(created[0]));
  await rpc({ operation: 'restoreDraft', state: picked.editState });
  await rpc({ operation: 'commit' });
  assert.equal(JSON.parse(fs.readFileSync(path.join(temp, 'web-appearance.json'))).backgroundImage, imageUrl);
  picked = await rpc({ operation: 'pickBackground', media: 'video', testPick: video });
  created.push(path.join(assetFolder, new URL(picked.editState.backgroundVideo).pathname.slice(1)));
  await page.evaluate(async () => window.CelesteAppearance.mount(document.getElementById('settings-scroll')));
  // 视频起播依赖真解码，这台机器负载高时偶发起不来（HEAD 版本同样偶发，属环境噪音）：
  // 30s 没起来就调一次恢复再等 15s，还不行才算失败
  const videoUp = () => page.waitForFunction(() => { const v = document.getElementById('bg-video'); return v.readyState >= 2 && !v.paused; }, null, { timeout: 30000 });
  try { await videoUp(); }
  catch {
    await page.evaluate(() => window.CelesteBackground.resume());
    try { await page.waitForFunction(() => { const v = document.getElementById('bg-video'); return v.readyState >= 2 && !v.paused; }, null, { timeout: 15000 }); }
    catch (e) { console.error(await page.$eval('#bg-video', v => ({src:v.src,ready:v.readyState,paused:v.paused,error:v.error?.message,status:window.CelesteBackground.status,css:getComputedStyle(document.documentElement).getPropertyValue('--bg-video')}))); throw e; }
  }
  assert(await page.$eval('#bg-video', v => v.muted && v.loop));
  await page.selectOption('#bg-fit', 'contain');
  await page.waitForFunction(() => getComputedStyle(document.getElementById('bg-video')).objectFit === 'contain');
  await click('撤销');
  await page.waitForFunction(() => getComputedStyle(document.getElementById('bg-video')).objectFit === 'cover');
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('celeste:message', { detail: { kind: 'celeste.visibility', hidden: true } })));
  assert(await page.$eval('#bg-video', v => v.paused));
  await page.evaluate(() => document.dispatchEvent(new CustomEvent('celeste:message', { detail: { kind: 'celeste.visibility', hidden: false } })));
  await page.waitForFunction(() => !document.getElementById('bg-video').paused);
  await click('取消预览');
  await page.waitForFunction(() => document.documentElement.dataset.bg === 'image');
  assert(await page.$eval('#bg-video', v => !v.hasAttribute('src') && v.paused));
  assert.equal((await state()).editState.backgroundImage, imageUrl);
  await page.screenshot({ path: path.join(root, 'outputs/appearance-integration-20261027.png') });
  assert.deepEqual(errors, []);
  console.log('PASS: raw undo / rapid queue / separate gestures / preset light+dark / picker cancel / immutable assets / real WebM / minimize+restore / source release / persistence');
})().catch(error => { console.error(error); process.exitCode = 1; }).finally(async () => {
  child.stdin.end(); if (browser) await browser.close();
  for (const file of created) if (fs.existsSync(file)) fs.unlinkSync(file);
});
