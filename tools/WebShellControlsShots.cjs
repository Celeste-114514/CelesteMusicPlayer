// 统一控件的视觉留档：浅色/深色各截几张，看外观有没有被改坏。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellControlsShots.cjs
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';
const DIR = process.argv[3] || 'C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell';

const VALUES = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'accent': '#e84865', 'accent-soft': '#fbe6ea',
  'on-accent': '#ffffff', 'hover': '#fcf3f6', 'pressed': '#f9e7ec', 'focus': '#e84865',
  'input-bg': '#ffffff', 'input-line': '#e2dcd9', 'menu-bg': '#ffffff', 'overlay': '#30252d4d',
  'shadow': '#43343126', 'text-scale': '1', 'line-height': '1.5'
};
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');
const SNAPSHOT = {
  revision: 3, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: '', playerButtonCatalog: [], fonts: ['Segoe UI'],
  typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  numbers: []
};

(async () => {
  const browser = await chromium.launch({
    executablePath: path.join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)',
      'Microsoft/Edge/Application/msedge.exe'),
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
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
      kind: 'celeste.theme', version: 1, revision: 3, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      light: { css, values: VALUES }, dark: { css, values: VALUES }
    });
    await page.waitForTimeout(400);

    // 排序下拉展开的样子
    await page.evaluate(() => document.querySelector('[data-page="songs"]')?.click());
    await page.waitForTimeout(400);
    await page.click('#songs-sort');
    await page.waitForTimeout(350);
    await page.screenshot({ path: path.join(DIR, 'shot-select-open-light.png') });
    await page.keyboard.press('Escape');
    await page.waitForTimeout(200);

    // 设置页
    await page.evaluate(() => document.querySelector('[data-page="settings"]')?.click());
    await page.waitForTimeout(500);
    await page.screenshot({ path: path.join(DIR, 'shot-settings-light.png') });

    // 深色
    await page.evaluate(() => {
      document.documentElement.dataset.theme = 'dark';
      document.documentElement.style.setProperty('--bg', '#1b1a1e');
      document.documentElement.style.setProperty('--shell', '#232227');
      document.documentElement.style.setProperty('--surface', '#26252a');
      document.documentElement.style.setProperty('--panel', '#2c2b31');
      document.documentElement.style.setProperty('--panel-soft', '#333138');
      document.documentElement.style.setProperty('--line', '#3a3940');
      document.documentElement.style.setProperty('--ink', '#ece9ef');
      document.documentElement.style.setProperty('--muted', '#9b98a3');
      document.documentElement.style.setProperty('--accent-soft', '#3a232b');
      document.documentElement.style.setProperty('--hover', '#332f35');
      document.documentElement.style.setProperty('--pressed', '#3b3138');
      document.documentElement.style.setProperty('--input-bg', '#2b2a30');
      document.documentElement.style.setProperty('--input-line', '#43424a');
      document.documentElement.style.setProperty('--menu-bg', '#2b2a30');
    });
    await page.waitForTimeout(250);
    await page.screenshot({ path: path.join(DIR, 'shot-settings-dark.png') });
    await page.evaluate(() => document.querySelector('[data-page="songs"]')?.click());
    await page.waitForTimeout(350);
    await page.click('#songs-sort');
    await page.waitForTimeout(350);
    await page.screenshot({ path: path.join(DIR, 'shot-select-open-dark.png') });
    await page.keyboard.press('Escape');

    // 外观页的「动效」卡
    await page.evaluate(async () => {
      const host = document.createElement('div');
      host.id = 'appearance-host';
      host.style.cssText = 'position:fixed;inset:0;overflow:auto;background:var(--bg);z-index:99999;padding:16px';
      document.body.append(host);
      await window.CelesteAppearance.mount(host);
      const cards = [...host.querySelectorAll('.settings-card')];
      const motion = cards.find(c => (c.querySelector('h2') || {}).textContent === '动效');
      if (motion) motion.scrollIntoView({ block: 'center' });
    });
    await page.waitForTimeout(400);
    await page.screenshot({ path: path.join(DIR, 'shot-motion-card.png') });

    // 轮盘展开：合并 CSS 规则后几何不能变
    await page.evaluate(() => { document.getElementById('appearance-host')?.remove(); });
    await page.waitForTimeout(200);
    await page.evaluate(() => document.querySelector('.corner-toggle')?.click());
    await page.waitForTimeout(600);
    await page.screenshot({ path: path.join(DIR, 'shot-orbit-open.png') });
    console.log('SHOTS_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
