// 响度 · ReplayGain 参数基线探针：真实进程 + CDP，改前/改后各跑一次逐字段对比。
// 会备份并还原 replaygain.json；会出声吗？不会 —— 只改设置，不播放。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13RgParametersProbe.cjs <exe> <out.json>
const fs = require('fs'), path = require('path'), { spawn } = require('child_process');
const { chromium } = require('playwright-core');

const wait = ms => new Promise(r => setTimeout(r, ms));
const CONFIG = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer');
const WATCHED = ['replaygain.json', 'eq-curve.json', 'dsp-extra.json'];

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
    await page.exposeFunction('readRg', () => {
      const f = path.join(CONFIG, 'replaygain.json');
      return fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : null;
    });

    const data = await page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const out = { initial: await call('get'), modules: await call('modules'),
        rgModule: await call('module', { module: 'rg' }), steps: [] };

      // 顺序有意义：先给非默认值，再切换/关开，最后试越界值
      const steps = [
        ['AudioFxRgPreampSlider', { value: 3.5 }],
        ['AudioFxRgModeCombo', { index: 1 }],          // Track
        ['AudioFxRgModeCombo', { index: 2 }],          // Album
        ['AudioFxRgPreventClippingToggle', { value: false }],
        ['DspPowerRg', { value: false }],              // 关 → Off，记忆 Album
        ['DspPowerRg', { value: true }],               // 开 → 恢复 Album
        ['AudioFxRgModeCombo', { index: 0 }],          // 组合框直接选「关闭」（电源跟着灭）
        ['DspPowerRg', { value: true }],               // 再由电源开 → 仍应恢复 Album
        ['AudioFxRgPreampSlider', { value: 100 }],     // 越界 → 12
        ['AudioFxRgPreampSlider', { value: -100 }],    // 越界 → -12
        ['AudioFxRgModeCombo', { index: 999 }],        // 无效下标 → 忽略
        ['AudioFxRgModeCombo', { index: -1 }],         // 负数下标 → 忽略
        ['AudioFxRgPreventClippingToggle', { value: true }],
        ['AudioFxRgModeCombo', { index: 1 }],          // 收尾 Track，供重启比对
        ['AudioFxRgPreampSlider', { value: 1.5 }]      // 收尾前级，供重启比对
      ];
      for (const [name, extra] of steps) {
        const reply = await call('setControl', { module: 'rg', name, ...extra });
        out.steps.push({ name, extra, reply,
          rg: await call('module', { module: 'rg' }),
          disk: await window.readRg() });
      }
      return out;
    });

    // 关闭重开：模式/前级/防削波必须回到盘中值
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
      // 新页面对象要重新挂一次桥（exposeFunction 不跟随页面）
      await page.exposeFunction('readRg', () => {
        const f = path.join(CONFIG, 'replaygain.json');
        return fs.existsSync(f) ? JSON.parse(fs.readFileSync(f, 'utf8')) : null;
      });
      data.restarted = await page.evaluate(async () => ({
        rg: await CelesteTransport.request('dsp', { operation: 'module', module: 'rg' }),
        disk: await window.readRg()
      }));
      fs.writeFileSync(process.argv[3], JSON.stringify(data, null, 2));
      console.log('WROTE RG baseline/scenario + restart', process.argv[3]);
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
