// 加载态防闪烁 + 音频链路抽屉的收场（方案 §6「跨页公共状态」「音频链路抽屉」两行）：
//   ① DSP 模块读得快时不闪"读取中…"
//   ② 读得慢时才显示，而且只显示一次
//   ③ 音频链路抽屉：关掉马上再打开，旧的收起不许把新开的藏掉
//   ④ 关掉「弹窗」分类后，抽屉收起接近瞬时（时长真的走了令牌，而不是写死 240ms）
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellLoadingSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href;
const EDGE = 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe';

(async () => {
  const browser = await chromium.launch({ executablePath: EDGE, headless: true });
  try {
    // —— ① ② DSP 的"读取中…" ————————————————————————————————
    const dsp = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    const errors = [];
    dsp.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });
    await dsp.addInitScript(() => {
      window.__moduleDelay = 5;
      const sw = (name, value = true) => ({ name, kind: 'switch', value, enabled: true });
      const slider = (name, value = 0) => ({ name, kind: 'slider', value, min: -18, max: 18, step: 0.1, enabled: true });
      window.chrome = { webview: {
        addEventListener: (_, fn) => { window.__deliver = data => fn({ data }); },
        postMessage: message => {
          if (!message.requestId) return;
          let data = {};
          if (message.command === 'dsp') {
            if (message.operation === 'module') data = { controls: [sw('DspPowerComp'), slider('DspCompThresholdSlider', -12)], info: {}, active: true };
            else if (message.operation === 'modules') data = { modules: { eq: true } };
            else if (message.operation === 'bypass') data = { bypass: false };
            else if (message.operation === 'levels') data = { session: false };
            else if (message.operation === 'profiles') data = { profiles: [] };
            else if (message.operation === 'rack') data = { isDefault: true, modules: [] };
          }
          const wait = message.operation === 'module' ? window.__moduleDelay : 5;
          setTimeout(() => window.__deliver({ kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }), wait);
        }
      } };
    });

    await dsp.goto(URL, { waitUntil: 'load' });
    await dsp.evaluate(() => CelesteShell.navigate('dsp'));
    await dsp.waitForSelector('.dsp-chain-item');
    await dsp.waitForTimeout(250);

    await dsp.locator('.dsp-chain-item[data-module="comp"]').click();
    await dsp.waitForSelector('.dsp-row');
    await dsp.waitForTimeout(120);
    assert.equal(await dsp.locator('.dsp-module-body > p.dsp-row-hint').count(), 0, '快请求不留"读取中…"');

    // 慢请求：先藏着，过一会儿才出现
    await dsp.evaluate(() => { window.__moduleDelay = 600; });
    await dsp.locator('.dsp-chain-item[data-module="headroom"]').click();
    const early = await dsp.evaluate(() => {
      const hint = document.querySelector('.dsp-module-body > p.dsp-row-hint');
      return { exists: !!hint, hidden: hint ? hint.hidden : null };
    });
    assert.equal(early.exists, true, '慢请求的提示元素已经摆好');
    assert.equal(early.hidden, true, '但一开始是藏着的（防闪烁）');
    await dsp.waitForFunction(() => {
      const hint = document.querySelector('.dsp-module-body > p.dsp-row-hint');
      return hint && !hint.hidden;
    }, { timeout: 1200 });
    await dsp.waitForSelector('.dsp-row', { timeout: 2000 });
    assert.equal(await dsp.locator('.dsp-module-body > p.dsp-row-hint').count(), 0, '数据到了提示就撤');

    // —— ③ ④ 音频链路抽屉（浏览器预览，不需要宿主数据）————————————————
    const chain = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    chain.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });
    await chain.goto(URL, { waitUntil: 'load' });
    await chain.waitForTimeout(250);

    await chain.locator('#chain-button').click();
    await chain.waitForTimeout(60);
    assert.equal(await chain.evaluate(() => document.getElementById('chain-drawer').hidden), false, '抽屉打开');

    // 关掉之后马上再开：旧的收起不许把新开的藏掉
    await chain.locator('#chain-close').click();
    await chain.waitForTimeout(40);
    await chain.locator('#chain-button').click();
    await chain.waitForTimeout(600);
    assert.equal(await chain.evaluate(() => document.getElementById('chain-drawer').hidden), false, '重开后抽屉仍然可见');

    // 关掉「弹窗」分类 → 收起接近瞬时（不再是写死的 240ms）
    await chain.evaluate(() => CelesteMotion.setOff(['popup']));
    await chain.waitForTimeout(60);
    await chain.locator('#chain-close').click();
    await chain.waitForTimeout(150);
    assert.equal(await chain.evaluate(() => document.getElementById('chain-drawer').hidden), true,
      '关掉弹窗分类后 150ms 内就收起（原来要等 240ms）');
    await chain.evaluate(() => CelesteMotion.setOff([]));

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: 快请求不闪加载态 / 慢请求才显示 / 抽屉重开不被旧收起藏掉 / 分类关掉后收起瞬时');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
