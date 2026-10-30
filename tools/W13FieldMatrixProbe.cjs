// 声场 + 矩阵参数基线探针：真实进程 + CDP，迁移前后各跑一次逐字段对比。
// 只改设置、不播放；备份并还原 dsp-rack.json / dsp-extra.json / eq-curve.json。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13FieldMatrixProbe.cjs <exe> <out.json>
const fs = require('fs'), path = require('path'), { spawn } = require('child_process');
const { chromium } = require('playwright-core');

const wait = ms => new Promise(r => setTimeout(r, ms));
const CONFIG = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer');
const RACK = path.join(CONFIG, 'dsp-rack.json');
const WATCHED = ['dsp-rack.json', 'dsp-extra.json', 'eq-curve.json'];

const readRack = () => {
  if (!fs.existsSync(RACK)) return null;
  try {
    const all = JSON.parse(fs.readFileSync(RACK, 'utf8'));
    return { field: all.StereoField ?? null, matrix: all.Matrix ?? null };
  } catch { return 'unreadable'; }
};

const moduleOf = name => (/^DspPowerMatrix|^DspMatrix/.test(name) ? 'matrix' : 'stereo');

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
    await page.exposeFunction('readRack', readRack);

    const data = await page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const moduleOf = name => (/^DspPowerMatrix|^DspMatrix/.test(name) ? 'matrix' : 'stereo');
      const out = { initial: await call('get'), modules: await call('modules'),
        stereoModule: await call('module', { module: 'stereo' }),
        matrixModule: await call('module', { module: 'matrix' }), steps: [] };

      // 先给非默认值 → 电源往返 → 越界钳制 → 收尾回默认（供重启比对）
      const steps = [
        ['DspFieldToggle', { value: true }],
        ['DspFieldWidthSlider', { value: 130 }],
        ['DspFieldCenterSlider', { value: -3.5 }],
        ['DspFieldSideSlider', { value: 4.5 }],
        ['DspPowerField', { value: false }],
        ['DspPowerField', { value: true }],
        ['DspFieldWidthSlider', { value: 999 }],     // 上越界 → 200
        ['DspFieldCenterSlider', { value: -99 }],    // 下越界 → -18
        ['DspFieldSideSlider', { value: 99 }],       // 上越界 → 18
        ['DspFieldWidthSlider', { value: 100 }],
        ['DspFieldCenterSlider', { value: 0 }],
        ['DspFieldSideSlider', { value: 0 }],
        ['DspMatrixToggle', { value: true }],
        ['DspMatrixLlSlider', { value: 0 }],
        ['DspMatrixRlSlider', { value: 1 }],
        ['DspMatrixLrSlider', { value: 1 }],
        ['DspMatrixRrSlider', { value: 0 }],
        ['DspPowerMatrix', { value: false }],
        ['DspPowerMatrix', { value: true }],
        ['DspMatrixLlSlider', { value: -9 }],        // 下越界 → -2
        ['DspMatrixRlSlider', { value: 9 }],         // 上越界 → 2
        ['DspMatrixLlSlider', { value: 1 }],
        ['DspMatrixRlSlider', { value: 0 }],
        ['DspMatrixLrSlider', { value: 0 }],
        ['DspMatrixRrSlider', { value: 1 }],
        ['DspMatrixToggle', { value: false }],
        ['DspMatrixToggle', { value: true }]
      ];
      for (const [name, extra] of steps) {
        const reply = await call('setControl', { module: moduleOf(name), name, ...extra });
        out.steps.push({ name, extra, reply,
          stereo: await call('module', { module: 'stereo' }),
          matrix: await call('module', { module: 'matrix' }),
          disk: await window.readRack() });
      }
      return out;
    });

    // 关闭重开：两组参数必须回到盘中值
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
      await page.exposeFunction('readRack', readRack);
      data.restarted = await page.evaluate(async () => ({
        stereo: await CelesteTransport.request('dsp', { operation: 'module', module: 'stereo' }),
        matrix: await CelesteTransport.request('dsp', { operation: 'module', module: 'matrix' }),
        disk: await window.readRack()
      }));
      fs.writeFileSync(process.argv[3], JSON.stringify(data, null, 2));
      console.log('WROTE FIELD+MATRIX baseline/scenario + restart', process.argv[3]);
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
