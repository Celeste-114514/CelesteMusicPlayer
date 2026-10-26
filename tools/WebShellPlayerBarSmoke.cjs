// 底栏（播放状态栏）回归冒烟：三围变量 / 文字与图标色 / 按钮显隐与顺序 / 播放键居中。
// 跑法（need Playwright；本机借用 Edge，不需要下载 chromium）：
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellPlayerBarSmoke.cjs http://127.0.0.1:8811/index.html
const assert = require('node:assert/strict');
const path = require('node:path');

// 只装了 playwright-core 也能跑（它就有 chromium.launch，只是不带浏览器下载）
let chromium;
try { ({ chromium } = require('playwright')); }
catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

// 宿主会算好的那一套变量。这里手写一份，重点在底栏那 5 个 + 三个三围。
const VALUES = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'row-active': '#fbe6ea',
  'accent': '#e84865', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'cover-radius': '12px', 'cover-size': '62px',
  'player-opacity': '0.88', 'player-blur': '0px',
  // ↓ 本轮新增：底栏文字两支 + 三围
  'player-ink': '#1b6f9c', 'player-muted': '#c2185b',
  'player-height': '150px', 'player-gap': '40px', 'player-pad': '24px'
};
const css = Object.entries(VALUES).map(([k, v]) => '--' + k + ':' + v).join(';');

// 故意打乱 + 收起两个：favorite 收起，previous 提前，tools 里 lyrics 收起、volume 提到最前
const BUTTONS = 'left:previous,!favorite,playback-order,seek-back|right:queue,seek-forward,next,transport-more|tools:volume,!lyrics,audio-settings';

const CATALOG = [
  { id: 'favorite', label: '喜欢', group: 'left', core: false },
  { id: 'playback-order', label: '播放顺序', group: 'left', core: false },
  { id: 'previous', label: '上一首', group: 'left', core: true },
  { id: 'seek-back', label: '快退 5 秒', group: 'left', core: false },
  { id: 'seek-forward', label: '快进 5 秒', group: 'right', core: false },
  { id: 'next', label: '下一首', group: 'right', core: true },
  { id: 'queue', label: '播放队列', group: 'right', core: false },
  { id: 'transport-more', label: '更多功能', group: 'right', core: false },
  { id: 'lyrics', label: '桌面歌词', group: 'tools', core: false },
  { id: 'volume', label: '音量', group: 'tools', core: false },
  { id: 'audio-settings', label: '音频设置', group: 'tools', core: false }
];

const SNAPSHOT = {
  revision: 3, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'blush',
  overrideCount: 0, presets: [],
  light: { css, values: VALUES }, dark: { css, values: VALUES },
  playerButtons: BUTTONS, playerButtonCatalog: CATALOG,
  numbers: [
    { id: 'player-opacity', label: '底栏不透明度', min: 0.5, max: 1, step: 0.02, unit: '×', value: 0.88 },
    { id: 'player-height', label: '底栏高度', min: 88, max: 180, step: 1, unit: 'px', value: 150 },
    { id: 'player-gap', label: '底栏区块间距', min: 8, max: 56, step: 1, unit: 'px', value: 40 },
    { id: 'player-pad', label: '底栏上下留白', min: 6, max: 40, step: 1, unit: 'px', value: 24 }
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
    const page = await browser.newPage({ viewport: { width: 1600, height: 1000 } });
    page.on('pageerror', error => errors.push(error.message));

    // 假宿主：只实现本轮会碰到的命令，其余一律回 ok，免得页面报错干扰判断
    await page.addInitScript(snapshot => {
      window.__commands = [];
      const handlers = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage(message) {
          window.__commands.push(message);
          if (!message || !message.requestId) return;
          let data = {};
          if (message.command === 'theme') {
            if (message.operation === 'playerButtons') data = Object.assign({}, snapshot, { playerButtons: message.value });
            else data = snapshot;
          }
          const reply = { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data };
          setTimeout(() => handlers.forEach(fn => fn({ data: reply })), 5);
        },
        addEventListener(_, fn) { handlers.push(fn); }
      };
      window.__deliver = message => handlers.forEach(fn => fn({ data: message }));
    }, SNAPSHOT);

    await page.goto(URL, { waitUntil: 'load' });

    // 宿主 ready 后立刻推一次主题（真实链路就是这条：celeste.theme 带 playerButtons）
    await page.evaluate(payload => window.__deliver(payload), {
      kind: 'celeste.theme', version: 1, revision: 3, mode: 'light',
      presetId: 'blush', accentSource: 'theme', accentCustom: '#e84865',
      playerButtons: BUTTONS,
      light: { css, values: VALUES }, dark: { css, values: VALUES }
    });
    await page.waitForTimeout(400);

    const report = await page.evaluate(() => {
      const bar = document.querySelector('.player');
      const cs = getComputedStyle(bar);
      const box = bar.getBoundingClientRect();
      const play = document.getElementById('play').getBoundingClientRect();
      const wrap = document.querySelector('.transport-buttons').getBoundingClientRect();
      const ids = sel => [...document.querySelectorAll(sel + ' > *')].map(el => el.id + (el.hidden ? '(hidden)' : ''));
      const colorOf = el => getComputedStyle(el).color;
      return {
        height: box.height,
        gap: cs.columnGap,
        padTop: cs.paddingTop,
        padBottom: cs.paddingBottom,
        playCenterOffset: Math.abs((play.left + play.right) / 2 - (wrap.left + wrap.right) / 2),
        left: ids('.tb-left'),
        right: ids('.tb-right'),
        tools: [...document.querySelectorAll('.player-tools > *')]
          .map(el => (el.id || el.className) + (el.hidden ? '(hidden)' : '') + '|order=' + (el.style.order || '-')),
        titleColor: colorOf(document.getElementById('track-title')),
        artistColor: colorOf(document.getElementById('track-artist')),
        elapsedColor: colorOf(document.getElementById('elapsed')),
        iconColor: colorOf(document.getElementById('previous')),
        volumeIconColor: getComputedStyle(document.querySelector('.volume-icon')).color
      };
    });

    console.log(JSON.stringify(report, null, 2));

    // 三围：高度/间距/上下留白都真的走变量
    assert.equal(Math.round(report.height), 150, '底栏高度没跟着 --player-height');
    assert.equal(report.gap, '40px', '底栏区块间距没跟着 --player-gap');
    assert.equal(report.padTop, '24px', '底栏上留白没跟着 --player-pad');
    assert.equal(report.padBottom, '24px', '底栏下留白没跟着 --player-pad');

    // 颜色：字/图标吃 --player-ink / --player-muted
    assert.equal(report.titleColor, 'rgb(27, 111, 156)', '歌名没跟 --player-ink');
    assert.equal(report.iconColor, 'rgb(27, 111, 156)', '按钮图标没跟 --player-ink');
    assert.equal(report.volumeIconColor, 'rgb(27, 111, 156)', '音量图标没跟 --player-ink');
    assert.equal(report.artistColor, 'rgb(194, 24, 91)', '歌手行没跟 --player-muted');
    assert.equal(report.elapsedColor, 'rgb(194, 24, 91)', '时间没跟 --player-muted');

    // 顺序与显隐
    assert.deepEqual(report.left, ['previous', 'favorite(hidden)', 'playback-order', 'seek-back'],
      '左簇顺序/收起不对');
    assert.deepEqual(report.right, ['queue', 'seek-forward', 'next', 'transport-more'],
      '右簇顺序不对');
    // 工具区靠 flex order 排，DOM 顺序不变 —— 要按 order 判，不能按下标
    const toolOrder = id => {
      const hit = report.tools.find(text => text.startsWith(id + '|') || text.startsWith(id + '('));
      return hit ? Number((hit.match(/order=(\d+)/) || [])[1]) : NaN;
    };
    assert.equal(toolOrder('volume-group'), 1, '音量组没排到工具区第一位');
    assert.equal(toolOrder('lyrics'), 2, '歌词按钮的排序号不对');
    assert.equal(toolOrder('audio-settings'), 3, '音频设置的排序号不对');
    assert.match(report.tools.join(','), /lyrics\(hidden\)/, '歌词按钮没被收起');

    // 收了一个按钮，播放键仍然在正中
    assert.ok(report.playCenterOffset < 2, '收起按钮后播放键偏了 ' + report.playCenterOffset + 'px');

    // 再验一次"改一下立刻存"：调 playerButtons 操作，页面应就地重排
    await page.evaluate(() => window.CelestePlayerLayout.apply('left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings'));
    await page.waitForTimeout(100);
    const restored = await page.evaluate(() => [...document.querySelectorAll('.tb-left > *')].map(el => el.id));
    assert.deepEqual(restored, ['favorite', 'playback-order', 'previous', 'seek-back'], '恢复默认构成失败');

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('PLAYERBAR_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('页面异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
