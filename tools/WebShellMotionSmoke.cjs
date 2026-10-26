// 动效回归：模式/速度/幅度/回弹风格是否真的作用到 CSS 与 JS，外观页的「动效」卡是否画得出来。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellMotionSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

const VALUES = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'accent': '#e84865', 'accent-soft': '#fbe6ea',
  'on-accent': '#ffffff', 'hover': '#fcf3f6', 'pressed': '#f9e7ec', 'focus': '#e84865',
  'input-bg': '#ffffff', 'input-line': '#e2dcd9', 'menu-bg': '#ffffff', 'overlay': '#30252d4d',
  'shadow': '#43343126', 'text-scale': '1', 'line-height': '1.5',
  'motion-speed': '1.5', 'motion-strength': '0.5'
};
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');
const SNAPSHOT = {
  revision: 5, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: '', playerButtonCatalog: [], fonts: ['Segoe UI'],
  typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  motion: { mode: 'full', speed: 1.5, strength: 0.5, bounce: 'lively', speedMin: 0.6, speedMax: 1.6, speedStep: 0.05, strengthMin: 0, strengthMax: 1.25, strengthStep: 0.05 },
  numbers: []
};

(async () => {
  const browser = await chromium.launch({
    executablePath: path.join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)',
      'Microsoft/Edge/Application/msedge.exe'),
    headless: true
  });
  const errors = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
    page.on('pageerror', e => errors.push(e.message));
    await page.addInitScript(s => {
      const handlers = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage(message) {
          if (!message || !message.requestId) return;
          setTimeout(() => handlers.forEach(fn => fn({
            data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data: s }
          })), 5);
        },
        addEventListener(_, fn) { handlers.push(fn); }
      };
      window.__deliver = m => handlers.forEach(fn => fn({ data: m }));
    }, SNAPSHOT);
    await page.goto(URL, { waitUntil: 'load' });
    await page.evaluate(m => window.__deliver(m), {
      kind: 'celeste.theme', version: 1, revision: 5, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      light: { css, values: VALUES }, dark: { css, values: VALUES },
      motion: SNAPSHOT.motion
    });
    await page.waitForTimeout(400);

    // ① 模式与倍率都吃到了宿主给的值
    const cfg = await page.evaluate(() => {
      const root = document.documentElement;
      const s = getComputedStyle(root);
      return {
        mode: root.dataset.motion,
        scale: s.getPropertyValue('--motion-scale').trim(),
        shift: s.getPropertyValue('--motion-shift').trim(),
        speed: s.getPropertyValue('--motion-speed').trim(),
        strength: s.getPropertyValue('--motion-strength').trim(),
        bounce: window.CelesteMotion.getBounce(),
        state: { ...window.CelesteMotion.state }
      };
    });
    console.log('cfg=' + JSON.stringify(cfg));
    assert.equal(cfg.mode, 'full', '动效模式没生效');
    assert.equal(cfg.bounce, 'lively', '回弹风格没生效');
    // 速度 1.5 → 时长倍率 1/1.5 ≈ 0.667；幅度 0.5 → 位移倍率 0.5
    assert.ok(Math.abs(Number(cfg.scale) - 1 / 1.5) < 0.01, '速度没换算成时长倍率：' + cfg.scale);
    assert.ok(Math.abs(Number(cfg.shift) - 0.5) < 0.01, '幅度没生效：' + cfg.shift);

    // ② 模式切到 lite：位移归零、时长还在
    await page.evaluate(() => window.CelesteMotion.setMode('lite'));
    await page.waitForTimeout(150);
    const lite = await page.evaluate(() => ({
      mode: document.documentElement.dataset.motion,
      shift: getComputedStyle(document.documentElement).getPropertyValue('--motion-shift').trim()
    }));
    console.log('lite=' + JSON.stringify(lite));
    assert.equal(lite.mode, 'lite', '切到轻量模式没生效');
    assert.equal(Number(lite.shift), 0, '轻量模式还在做位移');

    // ③ 关闭：所有时长压到接近 0，但内容结构还在（不是把界面藏了）
    await page.evaluate(() => window.CelesteMotion.setMode('off'));
    await page.waitForTimeout(150);
    const off = await page.evaluate(() => ({
      mode: document.documentElement.dataset.motion,
      scale: getComputedStyle(document.documentElement).getPropertyValue('--motion-scale').trim(),
      pageVisible: !!document.querySelector('#songs-page')
    }));
    console.log('off=' + JSON.stringify(off));
    assert.equal(off.mode, 'off', '关闭模式没生效');
    assert.ok(Number(off.scale) < 0.01, '关闭模式时长没压下去：' + off.scale);
    assert.ok(off.pageVisible, '关闭动效把界面藏起来了');

    // ④ 外观页有「动效」这张卡，四个模式按钮 + 两个滑杆 + 三个回弹按钮
    const card = await page.evaluate(async () => {
      const host = document.createElement('div');
      document.body.append(host);
      await window.CelesteAppearance.mount(host);
      const found = [...host.querySelectorAll('.settings-card')].find(c => (c.querySelector('h2') || {}).textContent === '动效');
      if (!found) return null;
      return {
        modes: [...found.querySelectorAll('.mode-group:first-of-type .mode-option')].map(b => b.textContent),
        sliders: found.querySelectorAll('input[type=range]').length,
        groups: found.querySelectorAll('.mode-group').length,
        options: [...found.querySelectorAll('.mode-option')].map(b => b.textContent)
      };
    });
    console.log('card=' + JSON.stringify(card));
    assert.ok(card, '外观页没有「动效」卡片');
    assert.equal(card.sliders, 2, '动效卡应有速度与幅度两个滑杆');
    assert.ok(card.options.includes('跟随系统') && card.options.includes('关闭'), '模式按钮不全');
    assert.ok(card.options.includes('克制') && card.options.includes('灵动'), '回弹风格按钮不全');

    // ⑤ 页面转场走控制器：切页后新页有动画，而不是直接 hidden 切换
    await page.evaluate(() => window.CelesteShell.navigate('albums'));
    await page.waitForTimeout(60);
    const animating = await page.evaluate(() => document.getAnimations().length);
    console.log('animating=' + animating);
    assert.ok(animating > 0, '切页没有触发动画');

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('MOTION_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
