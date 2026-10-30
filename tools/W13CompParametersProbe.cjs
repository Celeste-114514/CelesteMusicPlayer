// 压缩器参数基线探针：真实进程 + CDP，迁移前后各跑一次逐字段对比。
// 只改设置、不播放；备份并还原 dsp-rack.json / dsp-extra.json / eq-curve.json。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13CompParametersProbe.cjs <exe> <out.json>
const fs = require('fs'), path = require('path'), { spawn } = require('child_process');
const { chromium } = require('playwright-core');

const wait = ms => new Promise(r => setTimeout(r, ms));
const CONFIG = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer');
const RACK = path.join(CONFIG, 'dsp-rack.json');
const WATCHED = ['dsp-rack.json', 'dsp-extra.json', 'eq-curve.json'];

const readComp = () => {
  if (!fs.existsSync(RACK)) return null;
  try {
    const all = JSON.parse(fs.readFileSync(RACK, 'utf8'));
    return all.Compressor ?? null;
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
    await page.exposeFunction('readComp', readComp);

    const data = await page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const out = { initial: await call('get'), modules: await call('modules'),
        compModule: await call('module', { module: 'comp' }), steps: [] };

      // 顺序有意义：先给非默认值 → 电源往返 → 越界钳制 → 峰值表头开关（仍是旧适配）→ 收尾
      const steps = [
        ['DspCompToggle', { value: true }],
        ['DspCompThresholdSlider', { value: -30 }],
        ['DspCompRatioSlider', { value: 8 }],
        ['DspCompAttackSlider', { value: 5 }],
        ['DspCompReleaseSlider', { value: 200 }],
        ['DspCompKneeSlider', { value: 12 }],
        ['DspCompMakeupSlider', { value: 6 }],
        ['DspCompMixSlider', { value: 50 }],
        ['DspPowerComp', { value: false }],      // 关 → Enabled=false
        ['DspPowerComp', { value: true }],       // 开 → Enabled=true
        ['DspCompThresholdSlider', { value: -999 }],  // 下越界 → -60
        ['DspCompRatioSlider', { value: 999 }],       // 上越界 → 20
        ['DspCompMixSlider', { value: -5 }],          // 下越界 → 0
        ['DspCompKneeSlider', { value: -1 }],         // 下越界 → 0
        ['DspCompMakeupSlider', { value: 99 }],       // 上越界 → 24
        ['DspCompToggle', { value: false }],
        ['DspCompPeakToggle', { value: false }],      // 表头模式：不属于 DSP 参数，仍走旧适配
        ['DspCompPeakToggle', { value: true }],
        ['DspCompToggle', { value: true }],           // 收尾
        ['DspCompMixSlider', { value: 100 }],
        ['DspCompThresholdSlider', { value: -18 }],
        ['DspCompMakeupSlider', { value: 0 }]
      ];
      for (const [name, extra] of steps) {
        const reply = await call('setControl', { module: 'comp', name, ...extra });
        out.steps.push({ name, extra, reply,
          comp: await call('module', { module: 'comp' }),
          disk: await window.readComp() });
      }
      return out;
    });

    // 关闭重开：七个参数 + 开关必须回到盘中值
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
      await page.exposeFunction('readComp', readComp);
      data.restarted = await page.evaluate(async () => ({
        comp: await CelesteTransport.request('dsp', { operation: 'module', module: 'comp' }),
        disk: await window.readComp()
      }));
      fs.writeFileSync(process.argv[3], JSON.stringify(data, null, 2));
      console.log('WROTE COMP baseline/scenario + restart', process.argv[3]);
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
