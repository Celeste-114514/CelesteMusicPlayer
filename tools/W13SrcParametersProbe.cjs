// 采样率升频（SRC）参数基线探针：真实进程 + CDP，迁移前后各跑一次逐字段对比。
// 只改设置、不播放；会备份并还原 app-settings.json（整份，含 SrcTargetHz/SrcQuality/SrcDither）。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13SrcParametersProbe.cjs <exe> <out.json>
const fs = require('fs'), path = require('path'), { spawn } = require('child_process');
const { chromium } = require('playwright-core');

const wait = ms => new Promise(r => setTimeout(r, ms));
const CONFIG = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer');
const SETTINGS = path.join(CONFIG, 'app-settings.json');
const WATCHED = ['app-settings.json', 'eq-curve.json', 'dsp-extra.json'];

const readSrc = () => {
  if (!fs.existsSync(SETTINGS)) return null;
  try {
    const all = JSON.parse(fs.readFileSync(SETTINGS, 'utf8'));
    return { SrcTargetHz: all.SrcTargetHz, SrcQuality: all.SrcQuality, SrcDither: all.SrcDither };
  } catch { return 'unreadable'; }
};

(async () => {
  const saved = new Map(WATCHED.map(n => [path.join(CONFIG, n),
    fs.existsSync(path.join(CONFIG, n)) ? fs.readFileSync(path.join(CONFIG, n)) : null]));
  const child = spawn(process.argv[2], ['--w7-background-test'], { stdio: 'ignore' });
  let browser;
  try {
    for (let i = 0; i < 60; i++) {
      try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(500); }
    }
    if (!browser) throw Error('CDP unavailable');
    let page;
    for (let i = 0; i < 90; i++) {
      page = browser.contexts()[0].pages().find(p => p.url().includes('celeste.internal'));
      if (page) break;
      await wait(500);
    }
    await page.waitForFunction(() => window.CelesteTransport && window.CelesteShell);
    await page.exposeFunction('readSrc', readSrc);

    const data = await page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const out = { initial: await call('get'), modules: await call('modules'),
        srcModule: await call('module', { module: 'src' }), steps: [] };

      // 顺序有意义：先给非默认值 → 电源往返（应回到原档）→ 直接选「关闭」再由电源开 → 越界值
      const steps = [
        ['SrcRateCombo', { index: 4 }],          // 96 kHz
        ['SrcQualityCombo', { index: 2 }],       // 透明
        ['SrcDitherCombo', { index: 3 }],        // NS-5
        ['DspPowerSrc', { value: false }],       // 关 → 0，记忆 96k
        ['DspPowerSrc', { value: true }],        // 开 → 回到 96k
        ['SrcRateCombo', { index: 0 }],          // 直接选「关闭」
        ['DspPowerSrc', { value: true }],        // 再由电源开 → 仍应回到 96k
        ['SrcRateCombo', { index: 6 }],          // 192 kHz
        ['DspPowerSrc', { value: false }],
        ['DspPowerSrc', { value: true }],        // 回到 192k
        ['SrcRateCombo', { index: 999 }],        // 越界下标 → 忽略
        ['SrcRateCombo', { index: -1 }],         // 负数下标 → 忽略
        ['SrcQualityCombo', { index: 9 }],       // 越界下标 → 忽略
        ['SrcDitherCombo', { index: 9 }],
        ['SrcQualityCombo', { index: 1 }],       // 收尾：均衡
        ['SrcDitherCombo', { index: 0 }],        // 收尾：关闭
        ['SrcRateCombo', { index: 4 }]           // 收尾：96 kHz，供重启比对
      ];
      for (const [name, extra] of steps) {
        const reply = await call('setControl', { module: 'src', name, ...extra });
        out.steps.push({ name, extra, reply,
          src: await call('module', { module: 'src' }),
          disk: await window.readSrc() });
      }
      return out;
    });

    // 关闭重开：目标采样率/质量/抖动必须回到盘中值
    await page.evaluate(() => CelesteShell.send('window', { action: 'close' }));
    for (let i = 0; i < 40 && child.exitCode === null; i++) await wait(250);
    await browser.close(); browser = null;
    if (child.exitCode === null) { child.kill(); await new Promise(r => child.once('exit', r)); }
    await wait(500);

    const child2 = spawn(process.argv[2], ['--w7-background-test'], { stdio: 'ignore' });
    try {
      for (let i = 0; i < 60; i++) {
        try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(500); }
      }
      for (let i = 0; i < 90; i++) {
        page = browser.contexts()[0].pages().find(p => p.url().includes('celeste.internal'));
        if (page) break;
        await wait(500);
      }
      await page.waitForFunction(() => window.CelesteTransport && window.CelesteShell);
      // exposeFunction 不跟随页面：新页面要重新挂桥
      await page.exposeFunction('readSrc', readSrc);
      data.restarted = await page.evaluate(async () => ({
        src: await CelesteTransport.request('dsp', { operation: 'module', module: 'src' }),
        disk: await window.readSrc()
      }));
      fs.writeFileSync(process.argv[3], JSON.stringify(data, null, 2));
      console.log('WROTE SRC baseline/scenario + restart', process.argv[3]);
      await page.evaluate(() => CelesteShell.send('window', { action: 'close' }));
      for (let i = 0; i < 40 && child2.exitCode === null; i++) await wait(250);
    } finally {
      await browser?.close();
      if (child2.exitCode === null) { child2.kill(); await new Promise(r => child2.once('exit', r)); }
    }
  } finally {
    await browser?.close();
    if (child.exitCode === null) { child.kill(); await new Promise(r => child.once('exit', r)); }
    for (const [f, bytes] of saved) {
      if (bytes) fs.writeFileSync(f, bytes);
      else if (fs.existsSync(f)) fs.unlinkSync(f);
    }
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
