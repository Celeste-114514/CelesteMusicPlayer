// 交互状态语义回归：悬停/按下/输入框/浮层/遮罩/禁用 是否真的吃独立变量，
// 以及外观页「按钮与输入框」「弹窗与浮层」两张卡是否画出来。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellStateSmoke.cjs
const assert = require('node:assert/strict');
  // 颜色比较别用字符串相等：Chromium 在过渡进行中会把不透明色序列化成 `rgba(r, g, b, 1)`，
  // 过渡结束后才是 `rgb(r, g, b)`。按通道比，顺带容忍 ±1 的取整误差。
  const sameColor = (actual, expected) => {
    const parse = value => (String(value).match(/[\d.]+/g) || []).slice(0, 3).map(Number);
    const a = parse(actual), b = parse(expected);
    return a.length === 3 && b.length === 3 && a.every((channel, index) => Math.abs(channel - b[index]) <= 1);
  };

const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

// 每个语义色给一个绝不可能撞上的值，量出来对不上就说明没接上
const VALUES = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'row-active': '#fbe6ea',
  'accent': '#e84865', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'hover': '#112233', 'pressed': '#445566', 'focus': '#778899',
  'input-bg': '#aabbcc', 'input-line': '#ddeeff',
  'menu-bg': '#123456', 'overlay': '#00000080', 'shadow': '#65432140',
  'cover-radius': '12px', 'cover-size': '62px', 'player-opacity': '0.88', 'player-blur': '0px',
  'player-ink': '#28282e', 'player-muted': '#929097',
  'player-height': '120px', 'player-gap': '24px', 'player-pad': '16px',
  'text-scale': '1', 'line-height': '1.5',
  'font-ui': '"Segoe UI"', 'font-cjk': '"Microsoft YaHei UI"', 'font-mono': '"Consolas"'
};
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');

const SNAPSHOT = {
  revision: 9, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: 'left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings',
  playerButtonCatalog: [], fonts: ['Segoe UI'], typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  numbers: [
    { id: 'disabled-opacity', label: '禁用态透明度', min: 0.15, max: 0.7, step: 0.05, unit: '×', value: 0.35 },
    { id: 'text-scale', label: '字号整体缩放', min: 0.8, max: 1.4, step: 0.01, unit: '×', value: 1 },
    { id: 'line-height', label: '行高', min: 1.2, max: 2, step: 0.05, unit: '×', value: 1.5 }
  ]
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
      kind: 'celeste.theme', version: 1, revision: 9, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      light: { css, values: VALUES }, dark: { css, values: VALUES }
    });
    await page.waitForTimeout(350);

    const idle = await page.evaluate(() => {
      const s = el => getComputedStyle(el);
      return {
        searchBg: s(document.querySelector('.search')).backgroundColor,
        searchLine: s(document.querySelector('.search')).borderTopColor,
        menuBg: s(document.getElementById('order-menu')).backgroundColor,
        menuShadow: s(document.getElementById('order-menu')).boxShadow,
        disabledOpacity: s(document.getElementById('back')).opacity,
        focusVar: s(document.documentElement).getPropertyValue('--focus').trim(),
        overlayVar: s(document.documentElement).getPropertyValue('--overlay').trim()
      };
    });
    console.log('idle=' + JSON.stringify(idle));

    assert.equal(idle.searchBg, 'rgb(170, 187, 204)', '搜索框底色没吃 --input-bg');
    assert.equal(idle.searchLine, 'rgb(221, 238, 255)', '搜索框边框没吃 --input-line');
    assert.equal(idle.menuBg, 'rgb(18, 52, 86)', '播放顺序菜单底色没吃 --menu-bg');
    assert.match(idle.menuShadow, /101, 67, 33/, '菜单投影没吃 --shadow');
    assert.equal(idle.focusVar, '#778899', '--focus 没写进根元素');
    assert.equal(idle.overlayVar, '#00000080', '--overlay 没写进根元素');

    // 悬停：真把鼠标移到按钮上再量
    await page.hover('#favorite');
    await page.waitForTimeout(200);
    const hoverBg = await page.evaluate(() => getComputedStyle(document.getElementById('favorite')).backgroundColor);
    console.log('hoverBg=' + hoverBg);
    assert.ok(sameColor(hoverBg, 'rgb(17, 34, 51)'), '悬停底色没吃 --hover：' + hoverBg);

    // 按下：按住不放再量
    const box = await page.locator('#favorite').boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.down();
    // 按钮有 .2s 的背景过渡，按下去立刻量会拿到中间色 —— 等过渡走完再读
    await page.waitForTimeout(400);
    const activeBg = await page.evaluate(() => getComputedStyle(document.getElementById('favorite')).backgroundColor);
    await page.mouse.up();
    console.log('activeBg=' + activeBg);
    assert.ok(sameColor(activeBg, 'rgb(68, 85, 102)'), '按下底色没吃 --pressed：' + activeBg);

    // 卡片
    const cards = await page.evaluate(async () => {
      const host = document.createElement('div');
      host.id = 'smoke-host';
      host.style.cssText = 'position:fixed;inset:0;overflow:auto;background:var(--bg);z-index:99999;padding:12px';
      document.body.append(host);
      await window.CelesteAppearance.mount(host);
      return [...host.querySelectorAll('.settings-card')].map(card => {
        const title = (card.querySelector('h2') || {}).textContent || '';
        return {
          title,
          colors: [...card.querySelectorAll('input[type=color]')].length,
          sliders: [...card.querySelectorAll('input[type=range]')].length,
          labels: [...card.querySelectorAll('.set-label')].map(el => el.textContent)
        };
      });
    });
    const btn = cards.find(c => c.title === '按钮与输入框');
    const layer = cards.find(c => c.title === '弹窗与浮层');
    console.log('btnCard=' + JSON.stringify(btn));
    console.log('layerCard=' + JSON.stringify(layer));
    assert.ok(btn, '没有「按钮与输入框」卡片');
    assert.equal(btn.colors, 5, '按钮卡颜色行不是 5 个');
    assert.equal(btn.sliders, 1, '按钮卡应有 1 个禁用透明度滑杆');
    assert.ok(layer, '没有「弹窗与浮层」卡片');
    assert.equal(layer.colors, 3, '浮层卡颜色行不是 3 个');
    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('STATE_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
