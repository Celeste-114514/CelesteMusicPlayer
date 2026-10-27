// 换主题的整页交叉淡变（方案 §6「外观」行、§8 第三批验收）：
//   ① 页面刚起来那次下发不淡变（没有"旧画面"可淡出，淡了就是开机闪一下）
//   ② 同模式同预设、只改数值（拖滑杆）→ 直接换，不做淡变
//   ③ 换模式 / 换预设 → 走一次 view transition，颜色最终确实变了
//   ④ 动效关掉 → 不做淡变（无障碍设置优先）
//   ⑤ 引擎不支持 view transition → 不报错，主题照样生效
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellThemeTransitionSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href;

const LIGHT = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'accent': '#e84865', 'accent-soft': '#fbe6ea',
  'on-accent': '#ffffff', 'hover': '#fcf3f6', 'pressed': '#f9e7ec', 'focus': '#e84865',
  'input-bg': '#ffffff', 'input-line': '#e2dcd9', 'menu-bg': '#ffffff', 'overlay': '#30252d4d',
  'shadow': '#43343126', 'text-scale': '1', 'line-height': '1.5'
};
const DARK = Object.assign({}, LIGHT, {
  'bg': '#141317', 'shell': '#1b1a1f', 'surface': '#222127', 'panel': '#26252b', 'panel-soft': '#2d2c33',
  'line': '#3a3942', 'ink': '#f1eff3', 'muted': '#a6a2ad', 'accent': '#f2677e', 'accent-soft': '#3a2430',
  'hover': '#2c2a32', 'pressed': '#352a31', 'focus': '#f2677e',
  'input-bg': '#2a2930', 'input-line': '#3a3942', 'menu-bg': '#24222a', 'overlay': '#00000080',
  'shadow': '#00000059'
});

const bundle = values => {
  const css = Object.entries(values).map(([k, v]) => '--' + k + ':' + v).join(';');
  return { css, values };
};
const theme = (mode, values, presetId, revision) => ({
  kind: 'celeste.theme', version: 1, revision, mode, resolved: mode,
  presetId, accentSource: 'theme', accentCustom: values.accent,
  light: bundle(values), dark: bundle(values),
  motion: { mode: 'full', speed: 1, strength: 1, bounce: 'soft', off: [] }
});

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    const errors = [];
    page.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });

    await page.addInitScript(() => {
      const handlers = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        addEventListener(_, fn) { handlers.push(fn); },
        postMessage() { /* 这个冒烟不需要宿主回包 */ }
      };
      window.__deliver = m => handlers.forEach(fn => fn({ data: m }));
      // 数一下 view transition 被用了几次；真引擎在就转发，不在就把回调直接跑掉
      window.__vt = 0;
      const real = document.startViewTransition ? document.startViewTransition.bind(document) : null;
      document.startViewTransition = callback => {
        window.__vt += 1;
        if (real) return real(callback);
        callback();
        return { finished: Promise.resolve(), ready: Promise.resolve(), updateCallbackDone: Promise.resolve(), skipTransition() {} };
      };
    });

    await page.goto(URL, { waitUntil: 'load' });

    // ① 首次下发：不淡变
    await page.evaluate(payload => window.__deliver(payload), theme('light', LIGHT, 'sakura', 1));
    await page.waitForTimeout(120);
    assert.equal(await page.evaluate(() => window.__vt), 0, '首次下发不该做淡变');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()), '#eae8e6');

    // ② 只改数值（拖滑杆）：直接换，不淡变
    const tweaked = Object.assign({}, LIGHT, { 'bg': '#f0eeec' });
    await page.evaluate(payload => window.__deliver(payload), theme('light', tweaked, 'sakura', 2));
    await page.waitForTimeout(120);
    assert.equal(await page.evaluate(() => window.__vt), 0, '只改数值不该整页淡变');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()), '#f0eeec');

    // ③ 换模式：走一次 view transition，颜色最终到位
    await page.evaluate(payload => window.__deliver(payload), theme('dark', DARK, 'sakura', 3));
    await page.waitForTimeout(400);
    assert.equal(await page.evaluate(() => window.__vt), 1, '换模式应该走一次 view transition');
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), 'dark');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()), '#141317');

    // 换预设（同模式）也要淡变
    await page.evaluate(payload => window.__deliver(payload), theme('dark', DARK, 'ink', 4));
    await page.waitForTimeout(400);
    assert.equal(await page.evaluate(() => window.__vt), 2, '换预设应该走一次 view transition');

    // ④ 动效关掉：不淡变，但颜色照样换
    await page.evaluate(() => CelesteMotion.setMode('off'));
    const cooler = Object.assign({}, DARK, { 'bg': '#101014' });
    await page.evaluate(payload => window.__deliver(payload), theme('dark', cooler, 'ink', 5));
    await page.waitForTimeout(200);
    assert.equal(await page.evaluate(() => window.__vt), 2, '动效关掉后不该再淡变');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()), '#101014',
      '不淡变不等于不生效');
    await page.evaluate(() => CelesteMotion.setMode('full'));

    // ⑤ 引擎不支持：不报错，主题照样生效
    await page.evaluate(() => { delete document.startViewTransition; });
    await page.evaluate(payload => window.__deliver(payload), theme('light', LIGHT, 'paper', 6));
    await page.waitForTimeout(200);
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), 'light');
    assert.equal(await page.evaluate(() => getComputedStyle(document.documentElement).getPropertyValue('--bg').trim()), '#eae8e6');

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: 首次不淡变 / 拖数值不淡变 / 换模式和换预设各淡变一次 / 关动效不淡变但生效 / 不支持时不报错');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
