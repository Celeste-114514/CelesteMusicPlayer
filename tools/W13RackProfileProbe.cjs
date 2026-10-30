// 机架顺序 / 总旁路 / 听音方案探针：真实进程 + CDP，迁移前后各跑一次逐字段对比。
// 只改设置、不播放。应用方案会写回各 store，因此把相关 json 全部备份并在结束还原。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13RackProfileProbe.cjs <exe> <out.json>
const fs = require('fs'), path = require('path'), { spawn } = require('child_process');
const { chromium } = require('playwright-core');

const wait = ms => new Promise(r => setTimeout(r, ms));
const CONFIG = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer');
// 听音方案会整套写回这些文件：dsp-rack / extra / eq / replaygain / room / simple-eq / appsettings(升频三件套)
const WATCHED = ['dsp-profiles.json', 'dsp-rack.json', 'dsp-extra.json', 'eq-curve.json',
  'replaygain.json', 'room-correction.json', 'simple-eq.json', 'appsettings.json'];

const readJson = name => {
  const p = path.join(CONFIG, name);
  if (!fs.existsSync(p)) return null;
  try { return JSON.parse(fs.readFileSync(p, 'utf8')); } catch { return 'unreadable'; }
};

// 只留与这刀有关的字段，避免把整份设置塞进对比文件
const snapshot = () => {
  const rack = readJson('dsp-rack.json') || {};
  const book = readJson('dsp-profiles.json') || {};
  const app = readJson('appsettings.json') || {};
  return {
    rackOrder: rack.RackOrder ?? null,
    rackCompThreshold: rack.Compressor?.ThresholdDb ?? null,
    activeProfile: book.ActiveProfile ?? null,
    profileCount: (book.Profiles || []).length,
    profileNames: (book.Profiles || []).map(p => p.Name),
    srcTargetHz: app.SrcTargetHz ?? null,
    srcQuality: app.SrcQuality ?? null,
    srcDither: app.SrcDither ?? null
  };
};

const saved = new Map(WATCHED.map(n => [path.join(CONFIG, n),
  fs.existsSync(path.join(CONFIG, n)) ? fs.readFileSync(path.join(CONFIG, n)) : null]));

async function open(exe) {
  const child = spawn(exe, ['--w7-background-test'], { stdio: 'ignore' });
  let browser;
  for (let i = 0; i < 120; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; }
    catch { await wait(500); }
  }
  if (!browser) { child.kill(); throw Error('CDP unavailable'); }
  let page;
  for (let i = 0; i < 90; i++) {
    page = browser.contexts()[0].pages().find(p => p.url().includes('celeste.internal'));
    if (page) break;
    await wait(500);
  }
  await page.waitForFunction(() => window.CelesteTransport && window.CelesteShell);
  await page.exposeFunction('snap', snapshot);
  return { child, browser, page };
}

async function close(child, browser, page) {
  try { if (page) await page.evaluate(() => CelesteShell.send('window', { action: 'close' })); }
  catch { /* 页面已关 */ }
  for (let i = 0; i < 40 && child.exitCode === null; i++) await wait(250);
  if (child.exitCode === null) { child.kill(); await new Promise(r => child.once('exit', r)); }
  await browser?.close();
  await wait(2500);
}

(async () => {
  const out = { main: null, restart: null };
  let s = await open(process.argv[2]);
  try {
    out.main = await s.page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const step = async (label, fn) => {
        let reply;
        try { reply = await fn(); }
        catch (e) { reply = { error: String((e && e.message) || e) }; }
        return { label, reply, rack: await call('rack'), profiles: await call('profiles'), disk: await window.snap() };
      };

      const data = {
        initial: { rack: await call('rack'), profiles: await call('profiles'), bypass: await call('bypass'), disk: await window.snap() },
        steps: []
      };
      const push = async (label, fn) => { data.steps.push(await step(label, fn)); };

      // 机架顺序：合法移动 → 越界 → 复位
      await push('rackMove 0/+1', () => call('rackMove', { index: 0, delta: 1 }));
      await push('rackMove 3/-1', () => call('rackMove', { index: 3, delta: -1 }));
      await push('rackMove 0/-1 (越界)', () => call('rackMove', { index: 0, delta: -1 }));
      await push('rackMove 7/+1 (越界)', () => call('rackMove', { index: 7, delta: 1 }));
      await push('rackMove 9/+1 (越界)', () => call('rackMove', { index: 9, delta: 1 }));
      await push('rackReset', () => call('rackReset'));

      // 总旁路：内存态，不落盘
      await push('setBypass true', () => call('setBypass', { value: true }));
      await push('bypass read', () => call('bypass'));
      await push('setBypass false', () => call('setBypass', { value: false }));

      // 听音方案：保存 → 改一处（应脱离）→ 应用 → 更新 → 删除
      await push('profileSave 探针方案A', () => call('profileSave', { name: '探针方案A' }));
      await push('改压缩器阈值 -24', () => call('setControl', { module: 'comp', name: 'DspCompThresholdSlider', value: -24 }));
      await push('profileApply 探针方案A', () => call('profileApply', { name: '探针方案A' }));
      await push('profileUpdate 探针方案A', () => call('profileUpdate', { name: '探针方案A' }));
      await push('profileDelete 探针方案A', () => call('profileDelete', { name: '探针方案A' }));
      return data;
    });

    await close(s.child, s.browser, s.page);

    // 重启：机架顺序 / 方案列表从盘上回来；旁路是内存态，必须为 false
    s = await open(process.argv[2]);
    out.restart = await s.page.evaluate(async () => ({
      rack: await CelesteTransport.request('dsp', { operation: 'rack' }),
      profiles: await CelesteTransport.request('dsp', { operation: 'profiles' }),
      bypass: await CelesteTransport.request('dsp', { operation: 'bypass' }),
      disk: await window.snap()
    }));
    await close(s.child, s.browser, s.page);

    fs.writeFileSync(process.argv[3], JSON.stringify(out, null, 2));
    console.log('WROTE rack/profile baseline', process.argv[3]);
  } finally {
    await s?.browser?.close().catch(() => {});
    for (const [f, bytes] of saved) {
      if (bytes) fs.writeFileSync(f, bytes);
      else if (fs.existsSync(f)) fs.unlinkSync(f);
    }
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
