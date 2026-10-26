// 字体与排版回归：字号缩放真的改变计算后的 font-size、字体族真的落到 body、
// 外观页的「字体与排版」卡片能画出来（3 个字体下拉 + 2 个滑杆）。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellTypographySmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

const base = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'row-active': '#fbe6ea',
  'accent': '#e84865', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'cover-radius': '12px', 'cover-size': '62px', 'player-opacity': '0.88', 'player-blur': '0px',
  'player-ink': '#28282e', 'player-muted': '#929097',
  'player-height': '120px', 'player-gap': '24px', 'player-pad': '16px'
};
// 缩放拉大 25%，主字体换成 Georgia —— 两个都必须是肉眼能看出来的
const VALUES = Object.assign({}, base, {
  'text-scale': '1.25', 'line-height': '1.8',
  'font-ui': '"Georgia"', 'font-cjk': '"SimSun"', 'font-mono': '"Consolas"'
});
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');

const SNAPSHOT = {
  revision: 5, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: 'left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings',
  playerButtonCatalog: [],
  fonts: ['Segoe UI', 'Georgia', 'SimSun', 'Consolas', 'Microsoft YaHei'],
  typography: { fontUi: 'Georgia', fontCjk: 'SimSun', fontMono: 'Consolas' },
  numbers: [
    { id: 'radius', label: '圆角', min: 0, max: 34, step: 1, unit: 'px', value: 22 },
    { id: 'text-scale', label: '字号整体缩放', min: 0.8, max: 1.4, step: 0.01, unit: '×', value: 1.25 },
    { id: 'line-height', label: '行高', min: 1.2, max: 2, step: 0.05, unit: '×', value: 1.8 }
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
          let data = s;
          if (message.command === 'theme' && message.operation === 'typography') {
            data = Object.assign({}, s, { typography: Object.assign({}, s.typography, message) });
          }
          setTimeout(() => handlers.forEach(fn => fn({
            data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }
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
      light: { css, values: VALUES }, dark: { css, values: VALUES }
    });
    await page.waitForTimeout(350);

    const shown = await page.evaluate(() => {
      const size = el => parseFloat(getComputedStyle(el).fontSize);
      const family = el => getComputedStyle(el).fontFamily;
      return {
        scaleVar: getComputedStyle(document.documentElement).getPropertyValue('--text-scale').trim(),
        titleSize: size(document.getElementById('track-title')),   // 14px × 1.25
        elapsedSize: size(document.getElementById('elapsed')),     // 11px × 1.25
        bodyFamily: family(document.body),
        elapsedFamily: family(document.getElementById('elapsed')),
        bodyLineHeight: getComputedStyle(document.body).lineHeight
      };
    });
    console.log(JSON.stringify(shown, null, 2));

    assert.equal(shown.scaleVar, '1.25', '--text-scale 没写进根元素');
    assert.ok(Math.abs(shown.titleSize - 17.5) < 0.6, '歌名字号没跟着缩放：' + shown.titleSize);
    assert.ok(Math.abs(shown.elapsedSize - 13.75) < 0.6, '时间字号没跟着缩放：' + shown.elapsedSize);
    assert.match(shown.bodyFamily, /Georgia/, '主字体没落到 body：' + shown.bodyFamily);
    assert.match(shown.elapsedFamily, /Consolas/, '等宽字体没落到时间上：' + shown.elapsedFamily);

    // 外观页：字体与排版卡片
    const card = await page.evaluate(async () => {
      const host = document.createElement('div');
      host.id = 'smoke-host';
      host.style.cssText = 'position:fixed;inset:0;overflow:auto;background:var(--bg);z-index:99999;padding:12px';
      document.body.append(host);
      await window.CelesteAppearance.mount(host);
      const target = [...host.querySelectorAll('.settings-card')]
        .find(el => (el.querySelector('h2') || {}).textContent === '字体与排版');
      if (!target) return null;
      return {
        selects: target.querySelectorAll('select').length,
        sliders: target.querySelectorAll('input[type=range]').length,
        sample: target.querySelector('.type-sample') ? target.querySelector('.type-sample').textContent.slice(0, 12) : '',
        selected: [...target.querySelectorAll('select')].map(s => s.value)
      };
    });
    console.log('card=' + JSON.stringify(card));
    assert.ok(card, '没找到「字体与排版」卡片');
    assert.equal(card.selects, 3, '字体下拉不是 3 个');
    assert.equal(card.sliders, 2, '排版滑杆不是 2 个');
    assert.deepEqual(card.selected, ['Georgia', 'SimSun', 'Consolas'], '下拉没显示当前字体');
    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('TYPOGRAPHY_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
