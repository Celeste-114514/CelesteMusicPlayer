// W13 接口边界回归：真实进程 + CDP，验证 IWebPlayerServices 改造后各网页命令仍可用。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13InterfaceRegression.cjs <exe路径>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const LOG = path.resolve(__dirname, '../_w13_interface_regression.json');

function wait(ms) { return new Promise(r => setTimeout(r, ms)); }

(async () => {
  if (!fs.existsSync(exe)) throw new Error('exe 不存在: ' + exe);
  const child = spawn(exe, ['--w7-background-test'], { detached: false, stdio: 'ignore' });

  let browser = null;
  for (let i = 0; i < 60; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(1000); }
  }
  if (!browser) throw new Error('CDP 连接失败');

  const out = { exe, steps: [] };
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

    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 90000 });

    async function probe(tag) {
      const r = await page.evaluate(async () => {
        const call = async (command, payload) => {
          try { return { ok: true, data: await CelesteTransport.request(command, payload) }; }
          catch (e) { return { ok: false, error: String(e && e.message || e) }; }
        };
        const summary = v => {
          if (v == null) return null;
          if (Array.isArray(v)) return { array: v.length };
          if (typeof v !== 'object') return typeof v;
          const keys = Object.keys(v);
          return { keys: keys.slice(0, 14), counts: keys.filter(k => Array.isArray(v[k])).map(k => k + ':' + v[k].length) };
        };
        const out = { library: window.CelesteShell.getLibrary().length };
        out.home = await call('home', { operation: 'get', offsetMinutes: 0, today: new Date().toISOString().slice(0, 10) });
        out.folders = await call('folders', { operation: 'roots' });
        out.sources = await call('sources', { operation: 'list' });
        out.settings = await call('appSettings', { operation: 'get' });
        out.dsp = await call('dsp', { operation: 'get' });
        out.audioChain = await call('audioChain', {});
        out.audioReadout = await call('audioReadout', {});
        out.tags = await call('tags', {});
        out.queue = await call('queue', { operation: 'get' });
        out.nowPlaying = await call('nowPlaying', {});
        out.diagnostics = await call('diagnostics', {});
        out.recovery = await call('recovery', { action: 'status' });
        out.diagnosticsExport = await call('diagnostics', { action: 'export' });
        out.more = await call('more', { operation: 'get' });
        const brief = {};
        for (const k of Object.keys(out)) {
          brief[k] = (k === 'library') ? out[k] : (out[k].ok ? { ok: true, shape: summary(out[k].data) } : { ok: false, error: out[k].error });
        }
        return brief;
      });
      out.steps.push({ tag, ...r });
      return r;
    }

    const first = await probe('cold');
    await page.reload();
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 90000 });
    const second = await probe('reload');
    out.jsErrors = jsErrors;

    // 关闭：走网页自己的窗口命令，验证正常退出路径（业务清理）
    await page.evaluate(() => { try { CelesteShell.send('window', { action: 'close' }); } catch { } });

    const fails = [];
    for (const step of out.steps) {
      for (const k of Object.keys(step)) {
        if (k === 'tag' || k === 'library') continue;
        if (step[k] && step[k].ok === false) fails.push(step.tag + '.' + k + ': ' + step[k].error);
      }
    }
    out.failures = fails;

    // 等进程自行退出
    let exited = false;
    for (let i = 0; i < 25; i++) {
      if (child.exitCode !== null) { exited = true; break; }
      await wait(400);
    }
    out.exitedAfterClose = exited;
    out.exitCode = child.exitCode;

    fs.writeFileSync(LOG, JSON.stringify(out, null, 2));
    console.log(JSON.stringify({
      library: out.steps.map(s => s.tag + '=' + s.library),
      failures: fails,
      jsErrors,
      exitedAfterClose: out.exitedAfterClose,
      exitCode: out.exitCode
    }, null, 2));
  } finally {
    try { await browser.close(); } catch { }
    if (child.exitCode === null) { try { child.kill(); } catch { } }
  }
  const bad = (out.failures || []).length || !out.exitedAfterClose;
  process.exit(bad ? 1 : 0);
})().catch(e => { console.error('FAIL', e); process.exit(2); });
