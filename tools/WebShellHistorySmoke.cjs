// 撤销/重做回归：改一处参数 → 撤销回到原值 → 重做再回到新值；按钮可用状态跟着变；
// 连续拖同一个滑杆要合并成一步（否则撤销一次只退一像素）。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellHistorySmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

const base = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'heading': '#1f1f24', 'muted': '#929097', 'on-muted': '#6f6871',
  'accent': '#e84865', 'accent-strong': '#d83b58', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'row': '#ffffff', 'row-hover': '#fcf3f6', 'row-active': '#f9e7ec', 'chip': '#f4eff3', 'focus': '#e84865',
  'hover': '#fcf3f6', 'pressed': '#f9e7ec', 'input-bg': '#ffffff', 'input-line': '#efedef',
  'menu-bg': '#ffffff', 'overlay': '#30252d4d', 'shadow': '#43343126',
  'nav-track': '#e6dfe4', 'nav-ink': '#929097', 'nav-size': '300px', 'nav-icon': '28px',
  'nav-ring-1': '150', 'nav-ring-2': '238',
  'cover-radius': '12px', 'cover-size': '62px', 'player-opacity': '0.88', 'player-blur': '0px',
  'player-ink': '#28282e', 'player-muted': '#929097',
  'player-height': '120px', 'player-gap': '24px', 'player-pad': '16px',
  'text-scale': '1', 'line-height': '1.5',
  'font-ui': '"Segoe UI"', 'font-cjk': '"Microsoft YaHei UI"', 'font-mono': '"Consolas"',
  'bg-mode': 'solid', 'bg-image': 'none', 'bg-fit': 'cover', 'bg-pos': 'center',
  'bg-blur': '0px', 'bg-brightness': '1', 'bg-scrim': '0.55',
  'bg-grad-1': '#fdf3f6', 'bg-grad-2': '#f2d9e2', 'bg-grad-3': '#e7ecf6',
  'radius': '22px', 'radius-sm': '10px', 'blur': '25px', 'panel-opacity': '1', 'sat': '1',
  'motion-speed': '1', 'motion-strength': '1', 'density': '1', 'disabled-opacity': '0.35'
};
const cssOf = values => Object.entries(values).map(([k, v]) => '--' + k + ':' + v).join(';');

const SNAPSHOT = {
  revision: 21, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], fonts: ['Segoe UI'], typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  playerButtons: 'left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings',
  playerButtonCatalog: [],
  navItems: 'ring1:songs,albums,artists,albumArtists,folders|ring2:favorites,ratings,mostPlayed,playlists,queue,recent',
  navItemCatalog: [],
  numbers: [{ id: 'radius', label: '圆角', min: 0, max: 34, step: 1, unit: 'px', value: 22 }],
  light: { css: cssOf(base), values: Object.assign({}, base) },
  dark: { css: cssOf(base), values: Object.assign({}, base) }
};

(async () => {
  const browser = await chromium.launch({
    executablePath: path.join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)',
      'Microsoft/Edge/Application/msedge.exe'),
    headless: true
  });
  const errors = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
    page.on('pageerror', e => errors.push(e.message));

    // 假宿主：真正实现 preview（把 changes 合进变量表再回快照），撤销才有东西可对比
    await page.addInitScript(s => {
      const handlers = [];
      const state = { values: Object.assign({}, s.light.values), overrides: {} };
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage(message) {
          if (!message || !message.requestId) return;
          // 每一份快照都要带 editState：撤销/重做靠它对比前后，
          // 少了它网页会认为"什么都没改"，撤销按钮就一直灰着
          let data = Object.assign({}, s, { editState: Object.assign({}, s.editState || {}, { overrides: Object.assign({}, state.overrides) }) });
          if (message.command === 'theme' && message.operation === 'preview') {
            Object.entries(message.changes || {}).forEach(([key, value]) => {
              const split = key.indexOf(':');
              if (split > 0 && key.slice(0, split) === 'light') {
                state.values[key.slice(split + 1)] = value;
                state.overrides[key] = value;
              }
            });
            const values = Object.assign({}, state.values);
            data = Object.assign({}, s, {
              light: { css: Object.keys(values).map(k => '--' + k + ':' + values[k]).join(';'), values },
              // 撤销/重做靠 editState 对比前后快照，假宿主也得回这一份，否则网页认为"什么都没改"
              editState: Object.assign({}, s.editState || {}, { overrides: Object.assign({}, state.overrides) })
            });
          }
          if (message.command === 'theme' && message.operation === 'restoreDraft') {
            // 撤销/重做走 restoreDraft：整份覆盖表换过去（撤回去时要把新加的那条拿掉）
            const overrides = (message.state && message.state.overrides) || {};
            state.overrides = Object.assign({}, overrides);
            state.values = Object.assign({}, s.light.values);
            Object.entries(state.overrides).forEach(([key, value]) => {
              const split = key.indexOf(':');
              if (split > 0 && key.slice(0, split) === 'light') state.values[key.slice(split + 1)] = value;
            });
            const values = Object.assign({}, state.values);
            data = Object.assign({}, s, {
              light: { css: Object.keys(values).map(k => '--' + k + ':' + values[k]).join(';'), values },
              editState: { overrides: Object.assign({}, state.overrides) }
            });
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
    await page.evaluate(m => document.dispatchEvent(new CustomEvent('celeste:message', { detail: m })), {
      kind: 'celeste.theme', version: 1, revision: 21, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      light: { css: cssOf(base), values: Object.assign({}, base) },
      dark: { css: cssOf(base), values: Object.assign({}, base) },
      editState: { overrides: {} }
    });
    await page.waitForTimeout(200);

    // 快捷键的处理器只在设置页可见时接管（真实场景里就是这么用），所以先把它露出来
    await page.evaluate(() => { document.getElementById('settings-page').hidden = false; });
    await page.evaluate(async () => {
      const host = document.createElement('div');
      host.id = 'smoke-host';
      host.style.cssText = 'position:fixed;inset:0;overflow:auto;background:var(--bg);z-index:99999;padding:12px';
      document.body.append(host);
      await window.CelesteAppearance.mount(host);
    });
    await page.waitForTimeout(200);

    const radius = () => page.evaluate(() =>
      getComputedStyle(document.documentElement).getPropertyValue('--radius').trim());
    const buttons = () => page.evaluate(() => ({
      undo: document.getElementById('history-undo').disabled,
      redo: document.getElementById('history-redo').disabled
    }));

    assert.deepEqual(await buttons(), { undo: true, redo: true }, '一开始两个按钮都该是禁用的');

    // 找到「圆角」那一行的滑杆，拖到 30
    const slider = page.locator('#smoke-host .set-row', { hasText: '圆角' }).locator('input[type=range]').first();
    await slider.evaluate(el => {
      el.value = '30';
      el.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await page.waitForTimeout(300);
    console.log('afterChange radius=' + await radius() + ' buttons=' + JSON.stringify(await buttons()));
    assert.equal(await radius(), '30px', '预览没把 --radius 改成 30px');
    assert.deepEqual(await buttons(), { undo: false, redo: true }, '改完应该能撤销、不能重做');

    // 同一行再拖一次（模拟连续拖动），应合并成一步
    await slider.evaluate(el => {
      el.value = '15';
      el.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await page.waitForTimeout(300);
    assert.equal(await radius(), '15px', '第二次拖动没生效');

    // 撤销一次就该回到最初那个值，而不是回到 30
    await page.click('#history-undo');
    await page.waitForTimeout(300);
    console.log('afterUndo radius=' + await radius() + ' buttons=' + JSON.stringify(await buttons()));
    assert.equal(await radius(), '22px', '撤销没回到最初值（说明连续拖动没合并）');
    assert.deepEqual(await buttons(), { undo: true, redo: false }, '撤销后应该能重做');

    // 重做回到最后一次的值
    await page.click('#history-redo');
    await page.waitForTimeout(300);
    console.log('afterRedo radius=' + await radius());
    assert.equal(await radius(), '15px', '重做没回到第二次拖动的值');

    // 快捷键：Ctrl+Z
    await page.keyboard.press('Control+z');
    await page.waitForTimeout(300);
    assert.equal(await radius(), '22px', 'Ctrl+Z 没生效');

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('HISTORY_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
