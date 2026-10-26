// 半圆导航回归：项目显隐/换圈/顺序、半径改完 SVG 引导弧跟着重画、图标与整体尺寸生效。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellNavSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

// 半径故意改成非默认值（内 120 / 外 210），能对上才算真的重算了几何
const VALUES = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'row-active': '#fbe6ea',
  'accent': '#e84865', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'hover': '#fcf3f6', 'pressed': '#f9e7ec', 'focus': '#e84865',
  'input-bg': '#ffffff', 'input-line': '#efedef', 'menu-bg': '#ffffff',
  'overlay': '#30252d4d', 'shadow': '#43343126',
  'nav-track': '#334455', 'nav-ink': '#667788',
  'nav-size': '380px', 'nav-icon': '36px', 'nav-ring-1': '120', 'nav-ring-2': '210',
  'cover-radius': '12px', 'cover-size': '62px', 'player-opacity': '0.88', 'player-blur': '0px',
  'player-ink': '#28282e', 'player-muted': '#929097',
  'player-height': '120px', 'player-gap': '24px', 'player-pad': '16px',
  'text-scale': '1', 'line-height': '1.5',
  'font-ui': '"Segoe UI"', 'font-cjk': '"Microsoft YaHei UI"', 'font-mono': '"Consolas"'
};
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');

// 内圈：收掉「文件夹」，把「专辑艺人」提前；外圈：把「最近播放」提到第一，收掉「评分」
const NAV = 'ring1:albumArtists,songs,albums,artists,!folders|ring2:recent,favorites,!ratings,mostPlayed,playlists,queue';

const SNAPSHOT = {
  revision: 11, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: 'left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings',
  playerButtonCatalog: [], fonts: ['Segoe UI'], typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  navItems: NAV,
  navItemCatalog: [
    { id: 'songs', label: '歌曲', group: 'ring1' }, { id: 'albums', label: '专辑', group: 'ring1' },
    { id: 'artists', label: '艺术家', group: 'ring1' }, { id: 'albumArtists', label: '专辑艺人', group: 'ring1' },
    { id: 'folders', label: '文件夹', group: 'ring1' }, { id: 'favorites', label: '喜欢的音乐', group: 'ring2' },
    { id: 'ratings', label: '评分', group: 'ring2' }, { id: 'mostPlayed', label: '播放最多', group: 'ring2' },
    { id: 'playlists', label: '播放列表', group: 'ring2' }, { id: 'queue', label: '播放队列', group: 'ring2' },
    { id: 'recent', label: '最近播放', group: 'ring2' }
  ],
  numbers: [
    { id: 'nav-size', label: '导航整体尺寸', min: 240, max: 420, step: 2, unit: 'px', value: 380 },
    { id: 'nav-icon', label: '导航图标大小', min: 20, max: 40, step: 1, unit: 'px', value: 36 },
    { id: 'nav-ring-1', label: '内圈半径', min: 100, max: 200, step: 2, unit: '', value: 120 },
    { id: 'nav-ring-2', label: '外圈半径', min: 180, max: 270, step: 2, unit: '', value: 210 }
  ]
};

const CX = 300, CY = 10, W = 600, H = 290, START = 12, SPAN = 156;
const expected = (radius, index, count) => {
  const t = count <= 1 ? 0.5 : index / (count - 1);
  const angle = (START + t * SPAN) * Math.PI / 180;
  return {
    left: (CX - Math.cos(angle) * radius) / W * 100,
    top: (CY + Math.sin(angle) * radius) / H * 100
  };
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
          if (message.command === 'theme' && message.operation === 'navItems') {
            data = Object.assign({}, s, { navItems: message.value });
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
      kind: 'celeste.theme', version: 1, revision: 11, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      navItems: NAV,
      light: { css, values: VALUES }, dark: { css, values: VALUES }
    });
    // 轮盘项目有 .2s 的颜色过渡，等它走完再量，否则会读到中间色
    await page.waitForTimeout(700);

    const shown = await page.evaluate(() => {
      const orbit = document.querySelector('.corner-orbit');
      const items = [...document.querySelectorAll('.corner-item')].map(el => ({
        page: el.dataset.page,
        left: parseFloat(el.style.left),
        top: parseFloat(el.style.top),
        hidden: el.hidden,
        ring: el.classList.contains('ring-1') ? 'ring-1' : 'ring-2',
        width: Math.round(el.getBoundingClientRect().width),
        color: getComputedStyle(el).color
      }));
      return {
        orbitWidth: Math.round(orbit.getBoundingClientRect().width),
        items,
        arcs: {
          core: document.querySelector('.quarter-core').getAttribute('d'),
          inner: document.querySelector('.quarter-inner').getAttribute('d'),
          edge: document.querySelector('.quarter-edge').getAttribute('d'),
          fill: document.querySelector('.quarter-fill').getAttribute('d')
        },
        trackStroke: getComputedStyle(document.querySelector('.quarter-edge')).stroke
      };
    });

    console.log('orbitWidth=' + shown.orbitWidth + ' trackStroke=' + shown.trackStroke);
    console.log('arcs=' + JSON.stringify(shown.arcs));

    // 整体尺寸与图标大小
    assert.equal(shown.orbitWidth, 380, '轮盘宽度没跟着 --nav-size');
    const visible = shown.items.filter(i => !i.hidden);
    assert.equal(visible.length, 4 + 5, '可见项目数不对（内圈 4 + 外圈 5）');
    assert.ok(visible.every(i => i.width === 36 || i.width === 33), '图标尺寸没跟着 --nav-icon：' +
      [...new Set(visible.map(i => i.width))].join(','));
    assert.equal(visible[0].color, 'rgb(102, 119, 136)', '普通项目色没吃 --nav-ink');

    // 显隐
    const hiddenPages = shown.items.filter(i => i.hidden).map(i => i.page).sort();
    assert.deepEqual(hiddenPages, ['folders', 'ratings'], '收起状态不对：' + hiddenPages.join(','));

    // 换圈 + 顺序 + 几何：按期望公式逐个对
    // DOM 顺序是建按钮时的顺序，跟视觉顺序无关。
    // 弧从 12°（左）扫到 168°（右），x 随角度单调增大 —— 所以按 left 由小到大排就是弧上的先后。
    // （别用 top 排：12° 和 168° 的 sin 相同，是两端低、中间高。）
    const along = list => list.slice().sort((a, b) => a.left - b.left);
    const ring1 = along(shown.items.filter(i => i.ring === 'ring-1' && !i.hidden));
    const ring2 = along(shown.items.filter(i => i.ring === 'ring-2' && !i.hidden));
    assert.deepEqual(ring1.map(i => i.page), ['albumArtists', 'songs', 'albums', 'artists'], '内圈顺序不对');
    assert.deepEqual(ring2.map(i => i.page), ['recent', 'favorites', 'mostPlayed', 'playlists', 'queue'], '外圈顺序不对');

    const near = (a, b) => Math.abs(a - b) < 0.35;
    ring1.forEach((item, index) => {
      const want = expected(120, index, ring1.length);
      assert.ok(near(item.left, want.left) && near(item.top, want.top),
        item.page + ' 位置不对：得到 ' + item.left + '/' + item.top + '，应为 ' + want.left + '/' + want.top);
    });
    ring2.forEach((item, index) => {
      const want = expected(210, index, ring2.length);
      assert.ok(near(item.left, want.left) && near(item.top, want.top), item.page + ' 位置不对');
    });

    // 引导弧：核心 120-45=75、内 165、外 max(185, 210+42)=252
    assert.equal(shown.arcs.core, 'M225 10A75 75 0 0 0 375 10', '核心弧没重画');
    assert.equal(shown.arcs.inner, 'M135 10A165 165 0 0 0 465 10', '内圈弧没重画');
    assert.equal(shown.arcs.edge, 'M48 10A252 252 0 0 0 552 10', '外圈弧没重画');
    assert.equal(shown.arcs.fill, 'M48 10H552A252 252 0 0 1 48 10Z', '填充弧没重画');

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('NAV_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
