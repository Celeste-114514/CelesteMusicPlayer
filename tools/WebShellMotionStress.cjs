// 动效压力回归（方案 §8 第二批验收）：
//   开关 20 次 / 打开未完成就关闭再反开 / 快速跨页 20 次 / 焦点可恢复 / 减少动画实时切换
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellMotionStress.cjs
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
  'shadow': '#43343126', 'text-scale': '1', 'line-height': '1.5', 'motion-speed': '1', 'motion-strength': '1'
};
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');
const SNAPSHOT = {
  revision: 6, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: '', playerButtonCatalog: [], fonts: ['Segoe UI'],
  typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  motion: { mode: 'full', speed: 1, strength: 1, bounce: 'soft', categories: { nav: true, popup: true, list: true, control: true, theme: true, cover: true } },
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
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    page.on('pageerror', e => errors.push(e.message));
    await page.addInitScript(s => {
      // 系统「减少动画」偏好要在页面脚本之前就接管，
      // 否则 motion.js 已经把 matchMedia 的结果抓在手里了，事后换掉 window.matchMedia 也没用。
      window.__reduceMatches = false;
      const reduceListeners = [];
      const originMatchMedia = window.matchMedia.bind(window);
      window.matchMedia = query => {
        if (query.includes('reduced-motion')) {
          return {
            get matches() { return window.__reduceMatches; },
            media: query,
            addEventListener: (_, fn) => reduceListeners.push(fn),
            removeEventListener: () => {},
            addListener: fn => reduceListeners.push(fn),
            removeListener: () => {}
          };
        }
        return originMatchMedia(query);
      };
      window.__fireReduce = value => {
        window.__reduceMatches = value;
        reduceListeners.forEach(fn => fn({ matches: value }));
      };
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
      kind: 'celeste.theme', version: 1, revision: 6, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      light: { css, values: VALUES }, dark: { css, values: VALUES },
      motion: SNAPSHOT.motion
    });
    await page.waitForTimeout(300);

    // ① 开关 20 次：分类开关反复切，最终状态要落对，且不留残渣
    for (let i = 0; i < 20; i++) {
      await page.evaluate(i => {
        window.CelesteMotion.setOff(i % 2 ? ['nav'] : []);
        window.CelesteMotion.setMode(i % 3 ? 'full' : 'lite');
      }, i);
    }
    await page.waitForTimeout(200);
    const toggled = await page.evaluate(() => ({
      mode: document.documentElement.dataset.motion,
      off: window.CelesteMotion.getOff(),
      stray: document.querySelectorAll('#popup-layer > *').length,
      scrim: document.querySelectorAll('.ui-modal-scrim').length
    }));
    console.log('toggled=' + JSON.stringify(toggled));
    assert.deepEqual(toggled.off, ['nav'], '分类开关最终状态不对');
    assert.equal(toggled.stray, 0, '反复开关后浮层里有残留');
    assert.equal(toggled.scrim, 0, '反复开关后留下遮罩');

    // ② 打开未完成就关掉、马上反开：旧退场动画不许把刚打开的新浮层关掉
    await page.evaluate(() => window.CelesteMotion.setOff([]));
    await page.focus('#library-source');
    await page.keyboard.press('ArrowDown');
    await page.waitForTimeout(30);
    await page.keyboard.press('Escape');       // 刚开始退场
    await page.waitForTimeout(20);
    await page.keyboard.press('ArrowDown');     // 马上反开
    await page.waitForTimeout(400);             // 等旧动画走完
    const reopened = await page.evaluate(() => ({
      list: document.querySelectorAll('#popup-layer .ui-listbox').length,
      expanded: document.getElementById('library-source').getAttribute('aria-expanded')
    }));
    console.log('reopened=' + JSON.stringify(reopened));
    assert.equal(reopened.list, 1, '反开后浮层数量不对（旧的没拆或新的被拆了）');
    assert.equal(reopened.expanded, 'true', '反开后 aria-expanded 没恢复');

    // ③ 快速跨页 20 次：不串页、不闪白、不遗留遮罩
    const pages = ['songs', 'albums', 'artists', 'browse', 'settings', 'overview'];
    for (let i = 0; i < 20; i++) {
      await page.evaluate(name => window.CelesteShell.navigate(name), pages[i % pages.length]);
    }
    await page.waitForTimeout(600);
    const navigated = await page.evaluate(() => {
      const visible = [...document.querySelectorAll('.page')].filter(el => !el.hidden);
      return {
        visible: visible.length,
        ids: visible.map(el => el.id),
        transparent: visible.filter(el => Number(getComputedStyle(el).opacity) < 0.99).map(el => el.id),
        stray: document.querySelectorAll('#popup-layer > *').length,
        scrim: document.querySelectorAll('.ui-modal-scrim').length
      };
    });
    console.log('navigated=' + JSON.stringify(navigated));
    assert.equal(navigated.visible, 1, '跨页后同时可见了多个页面');
    assert.equal(navigated.transparent.length, 0, '有页面停在半透明（闪白）');
    assert.equal(navigated.stray, 0, '跨页后浮层有残留');
    assert.equal(navigated.scrim, 0, '跨页后留下遮罩');

    // ④ 焦点可恢复：浮层关掉后焦点回到触发它的控件
    await page.evaluate(() => window.CelesteShell.navigate('songs'));
    await page.waitForTimeout(200);
    await page.focus('#library-source');
    await page.keyboard.press('ArrowDown');
    await page.waitForTimeout(200);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(300);
    const focus = await page.evaluate(() => document.activeElement?.id);
    console.log('focus=' + focus);
    assert.equal(focus, 'library-source', '浮层关掉后焦点没回到 select');

    // ⑤ 减少动画实时切换：系统偏好变了，正在跑的动画要立刻落到终点，不能留下半透明的层
    await page.evaluate(() => window.__fireReduce(true));
    await page.focus('#library-source');
    await page.keyboard.press('ArrowDown');
    await page.waitForTimeout(60);
    await page.waitForTimeout(200);
    const reduced = await page.evaluate(() => ({
      mode: document.documentElement.dataset.motion,
      scale: getComputedStyle(document.documentElement).getPropertyValue('--motion-scale').trim(),
      shift: getComputedStyle(document.documentElement).getPropertyValue('--motion-shift').trim(),
      halfVisible: [...document.querySelectorAll('#popup-layer > *')].filter(el => {
        const o = Number(getComputedStyle(el).opacity);
        return o > 0.02 && o < 0.98;
      }).length
    }));
    console.log('reduced=' + JSON.stringify(reduced));
    assert.equal(reduced.mode, 'reduce', '系统减少动画没有实时生效（应优先于用户选的"完整"）');
    assert.ok(Number(reduced.scale) < 0.5, '时长倍率没压下去：' + reduced.scale);
    assert.equal(Number(reduced.shift), 0, '减少动画时还在做位移');
    assert.equal(reduced.halfVisible, 0, '有浮层停在半透明（还挡着点击）');

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('STRESS_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
