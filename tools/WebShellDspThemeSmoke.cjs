// DSP 页必须跟随外观（浅色/深色）——这条是用户报的 bug：整页白板贴在深色外壳上。
//   ① 深色主题下，DSP 页里不该再有"不透明且很亮"的色块
//      （唯一例外是声道偏差表上的白色指针 .dsp-channel-bias-meter i，那是读数不是面板）
//   ② EQ 曲线容器跟着走：深色下不是白底，浅色下是浅底
//   ③ 切回浅色后模块面板恢复浅色，不会被深色规则粘住
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellDspThemeSmoke.cjs
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

// 深色下允许保持亮色的东西：都是"读数"，不是面板
const ALLOWED = ['.dsp-channel-bias-meter > i'];

const themeFor = (mode, values) => {
  const css = Object.entries(values).map(([k, v]) => '--' + k + ':' + v).join(';');
  return {
    kind: 'celeste.theme', version: 1, revision: 9, mode, resolved: mode,
    presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
    light: { css, values }, dark: { css, values }
  };
};

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
        postMessage(message) {
          if (!message || !message.requestId) return;
          const reply = data => handlers.forEach(fn => fn({
            data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }
          }));
          if (message.command !== 'dsp') return reply({});
          if (message.operation === 'get') {
            return reply({ enabled: true, preampDb: 0, presetId: 'flat', userPresets: [], undo: true, redo: false,
              bands: [200, 500, 1000, 2000, 4000].map(freq => ({ freq, gain: 0, q: 1, type: 'Peaking', enabled: true })) });
          }
          if (message.operation === 'module') return reply({ controls: [], info: {}, active: true });
          if (message.operation === 'spectrum') return reply({ available: false, bands: [] });
          if (message.operation === 'modules') return reply({ modules: { eq: true } });
          if (message.operation === 'levels') return reply({ session: true, peakDb: -6, chain: '44.1 → 48 kHz' });
          if (message.operation === 'bypass') return reply({ bypass: false });
          if (message.operation === 'rack') return reply({ isDefault: true, modules: [{ id: 0, index: 0, name: 'EQ' }, { id: 1, index: 1, name: '卷积' }] });
          if (message.operation === 'profiles') return reply({ profiles: [] });
          reply({});
        }
      };
      window.__deliver = m => handlers.forEach(fn => fn({ data: m }));
    });

    await page.goto(URL, { waitUntil: 'load' });
    await page.evaluate(() => CelesteShell.navigate('dsp'));

    /** 走一遍所有模块，收集"不透明且亮度 > 0.6"的元素。 */
    const collectLight = async () => {
      const found = new Map();
      for (const id of ['eq', 'headroom', 'comp', 'src', 'channel', 'stereo', 'matrix', 'fir', 'safety', 'profiles', 'rack']) {
        await page.locator(`.dsp-chain-item[data-module="${id}"]`).click();
        await page.waitForTimeout(260);
        const rows = await page.evaluate(() => {
          const luma = value => {
            const m = /rgba?\(([^)]+)\)/.exec(value || '');
            if (!m) return null;
            const [r, g, b, a = 1] = m[1].split(',').map(Number);
            if (a < 0.5) return null;
            return (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255;
          };
          const out = [];
          document.querySelectorAll('#dsp-page *').forEach(node => {
            const style = getComputedStyle(node);
            const value = luma(style.backgroundColor);
            if (value === null || value < 0.6) return;
            const own = node.className && typeof node.className === 'string' && node.className.trim()
              ? '.' + node.className.trim().split(/\s+/).join('.')
              : node.tagName.toLowerCase();
            // 带上父级，免得把"某个读数指针"和"任意无类元素"混成一个键
            const parent = node.parentElement;
            const parentKey = parent && typeof parent.className === 'string' && parent.className.trim()
              ? '.' + parent.className.trim().split(/\s+/)[0]
              : (parent ? parent.tagName.toLowerCase() : '');
            out.push({ cls: parentKey + ' > ' + own, bg: style.backgroundColor });
          });
          return out;
        });
        rows.forEach(row => { if (!found.has(row.cls)) found.set(row.cls, row); });
      }
      return [...found.values()];
    };

    // ① 深色
    await page.evaluate(payload => window.__deliver(payload), themeFor('dark', DARK));
    await page.waitForTimeout(300);
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), 'dark', '主题没切到深色');
    const bright = (await collectLight()).filter(row => !ALLOWED.includes(row.cls));
    assert.deepEqual(bright, [], '深色下仍有浅色面板：' + JSON.stringify(bright));

    // ② EQ 曲线容器跟着主题走
    await page.locator('.dsp-chain-item[data-module="eq"]').click();
    await page.waitForTimeout(260);
    const darkCurve = await page.evaluate(() => getComputedStyle(document.querySelector('.eq-curve-wrap')).backgroundColor);
    assert.ok(!/rgb\(255, 255, 255\)/.test(darkCurve), '深色下 EQ 曲线区还是白底：' + darkCurve);

    // ③ 切回浅色：模块面板恢复浅色，不被深色规则粘住
    await page.evaluate(payload => window.__deliver(payload), themeFor('light', LIGHT));
    await page.waitForTimeout(300);
    const lightPanel = await page.evaluate(() => {
      const style = getComputedStyle(document.querySelector('.dsp-module'));
      const m = /rgba?\(([^)]+)\)/.exec(style.backgroundColor);
      const [r, g, b] = m[1].split(',').map(Number);
      return { bg: style.backgroundColor, luma: (0.2126 * r + 0.7152 * g + 0.0722 * b) / 255 };
    });
    assert.ok(lightPanel.luma > 0.85, '切回浅色后模块面板不是浅色：' + lightPanel.bg);

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: DSP 深色下无白块 / EQ 曲线区随主题 / 切回浅色正常');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
