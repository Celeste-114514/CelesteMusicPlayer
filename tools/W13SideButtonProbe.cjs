// 鼠标侧键前进/后退探针（2026-10-31）
// 验证网页外壳的 goBack/goForward：合成 XButton1/XButton2 的 mouseup，看页面是否真的前后跳。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13SideButtonProbe.cjs <exe路径>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(__dirname, '../_w13_side_button.json');
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  if (!fs.existsSync(exe)) throw new Error('exe 不存在: ' + exe);
  const child = spawn(exe, ['--w7-background-test'], { detached: false, stdio: 'ignore' });

  let browser = null;
  for (let i = 0; i < 60; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(1000); }
  }
  if (!browser) throw new Error('CDP 连接失败');

  const out = { exe, steps: [], ok: false };
  try {
    let page = null;
    for (let i = 0; i < 90; i++) {
      page = browser.contexts()[0]?.pages().find(p => p.url().includes('celeste.internal'));
      if (page) break;
      await wait(1000);
    }
    if (!page) throw new Error('未找到主网页');

    const jsErrors = [];
    page.on('pageerror', e => jsErrors.push(e.message));

    await page.waitForFunction(() => !!window.CelesteShell, null, { timeout: 60000 });

    const result = await page.evaluate(async () => {
      const sleep = ms => new Promise(r => setTimeout(r, ms));
      const btn = (type, b) => window.dispatchEvent(new MouseEvent(type, { button: b, buttons: 0, bubbles: true, cancelable: true }));
      const pageNow = () => window.CelesteShell.getPage();
      const steps = [];

      // 起点：回到总览
      window.CelesteShell.navigate('overview', false);
      await sleep(150);

      // 走一串页面：总览 → 歌曲 → 专辑
      window.CelesteShell.navigate('songs');
      await sleep(150);
      window.CelesteShell.navigate('albums');
      await sleep(150);
      steps.push({ step: 'forward-nav', page: pageNow(), stacks: window.CelesteShell.getNavStacks() });

      // XButton1 = 后退
      btn('mousedown', 3); btn('mouseup', 3);
      await sleep(200);
      steps.push({ step: 'back-1', page: pageNow(), stacks: window.CelesteShell.getNavStacks() });

      btn('mousedown', 3); btn('mouseup', 3);
      await sleep(200);
      steps.push({ step: 'back-2', page: pageNow() });

      // XButton2 = 前进
      btn('mousedown', 4); btn('mouseup', 4);
      await sleep(200);
      steps.push({ step: 'forward-1', page: pageNow() });

      btn('mousedown', 4); btn('mouseup', 4);
      await sleep(200);
      steps.push({ step: 'forward-2', page: pageNow() });

      // 新导航后前进栈应被清空：再按前进不应变化
      window.CelesteShell.navigate('songs');
      await sleep(150);
      const before = pageNow();
      btn('mousedown', 4); btn('mouseup', 4);
      await sleep(200);
      steps.push({ step: 'forward-after-new-nav', from: before, page: pageNow() });

      return steps;
    });

    out.steps = result;
    out.jsErrors = jsErrors;

    const get = name => result.find(s => s.step === name)?.page;
    out.ok =
      get('forward-nav') === 'albums' &&
      get('back-1') === 'songs' &&
      get('back-2') === 'overview' &&
      get('forward-1') === 'songs' &&
      get('forward-2') === 'albums' &&
      result.find(s => s.step === 'forward-after-new-nav')?.from ===
        result.find(s => s.step === 'forward-after-new-nav')?.page &&
      jsErrors.length === 0;
  } catch (e) {
    out.error = String(e && e.message || e);
  } finally {
    try { await browser?.close(); } catch { }
    try { child.kill(); } catch { }
    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify(out, null, 2));
    process.exit(out.ok ? 0 : 1);
  }
})();
