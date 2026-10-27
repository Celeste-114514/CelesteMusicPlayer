// 背景层回归：三种模式真的换到 #bg-layer 上、外壳真的变透明、封面模式跟随当前曲目，
// 以及外观页「背景」卡片控件齐全。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellBackgroundSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';

const base = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'row-active': '#fbe6ea',
  'accent': '#e84865', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'hover': '#fcf3f6', 'pressed': '#f9e7ec', 'focus': '#e84865',
  'input-bg': '#ffffff', 'input-line': '#efedef', 'menu-bg': '#ffffff',
  'overlay': '#30252d4d', 'shadow': '#43343126',
  'nav-track': '#e6dfe4', 'nav-ink': '#929097', 'nav-size': '300px', 'nav-icon': '28px',
  'nav-ring-1': '150', 'nav-ring-2': '238',
  'cover-radius': '12px', 'cover-size': '62px', 'player-opacity': '0.88', 'player-blur': '0px',
  'player-ink': '#28282e', 'player-muted': '#929097',
  'player-height': '120px', 'player-gap': '24px', 'player-pad': '16px',
  'text-scale': '1', 'line-height': '1.5',
  'font-ui': '"Segoe UI"', 'font-cjk': '"Microsoft YaHei UI"', 'font-mono': '"Consolas"',
  // 背景：渐变三色给三个撞不上的值，模糊/亮度/遮罩给非默认值
  'bg-grad-1': '#ff0000', 'bg-grad-2': '#00ff00', 'bg-grad-3': '#0000ff',
  'bg-blur': '12px', 'bg-brightness': '0.8', 'bg-scrim': '0.4'
};
const cssOf = values => Object.entries(values).map(([k, v]) => '--' + k + ':' + v).join(';');

const SNAPSHOT = {
  revision: 13, draft: false, mode: 'light', resolved: 'light',
  accentSource: 'theme', accentCustom: '#e84865', presetId: 'sakura', overrideCount: 0,
  presets: [], fonts: ['Segoe UI'], typography: { fontUi: 'Segoe UI', fontCjk: 'Microsoft YaHei UI', fontMono: 'Consolas' },
  playerButtons: 'left:favorite,playback-order,previous,seek-back|right:seek-forward,next,queue,transport-more|tools:lyrics,volume,audio-settings',
  playerButtonCatalog: [], navItems: 'ring1:songs,albums,artists,albumArtists,folders|ring2:favorites,ratings,mostPlayed,playlists,queue,recent',
  navItemCatalog: [],
  numbers: [
    { id: 'bg-blur', label: '背景模糊', min: 0, max: 40, step: 1, unit: 'px', value: 12 },
    { id: 'bg-brightness', label: '背景亮度', min: 0.4, max: 1.4, step: 0.05, unit: '×', value: 0.8 },
    { id: 'bg-scrim', label: '背景遮罩', min: 0, max: 0.95, step: 0.05, unit: '×', value: 0.4 }
  ]
};
// 真实宿主每次都会带两套算好的 bundle；桩数据也得给，否则外观页取不到当前值
const PAGE_VALUES = Object.assign({}, base, { 'bg-mode': 'cover', 'bg-image': 'none' });
SNAPSHOT.light = SNAPSHOT.dark = { css: cssOf(PAGE_VALUES), values: PAGE_VALUES };

(async () => {
  const browser = await chromium.launch({
    executablePath: path.join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)',
      'Microsoft/Edge/Application/msedge.exe'),
    headless: true
  });
  const errors = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
    page.on('pageerror', e => errors.push(e.message));
    await page.addInitScript(s => {
      const handlers = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage(message) {
          if (!message || !message.requestId) return;
          let data = s;
          // 改背景那几个操作：把请求原样记进返回的快照，方便脚本继续往下走
          if (message.command === 'theme' && message.operation === 'background') {
            data = Object.assign({}, s, { __lastBackground: message });
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

    const pushTheme = async mode => {
      const values = Object.assign({}, base, { 'bg-mode': mode, 'bg-image': 'none' });
      await page.evaluate(payload => window.__deliver(payload), {
        kind: 'celeste.theme', version: 1, revision: 13, mode: 'light',
        presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
        light: { css: cssOf(values), values }, dark: { css: cssOf(values), values }
      });
      await page.waitForTimeout(250);
    };

    const read = () => page.evaluate(() => {
      const layer = document.getElementById('bg-layer');
      const before = getComputedStyle(layer, '::before');
      const after = getComputedStyle(layer, '::after');
      return {
        dataBg: document.documentElement.dataset.bg,
        shellBg: getComputedStyle(document.querySelector('.shell')).backgroundColor,
        beforeImage: before.backgroundImage,
        beforeSize: before.backgroundSize,
        blur: before.filter,
        scrim: after.opacity,
        afterBg: after.backgroundColor,
        zIndex: before.zIndex,
        layerZ: getComputedStyle(layer).zIndex,
        titlebarZ: getComputedStyle(document.querySelector('.titlebar')).zIndex
      };
    });

    // 1) 纯色：不画东西，外壳保持实心
    await pushTheme('solid');
    const solid = await read();
    console.log('solid=' + JSON.stringify(solid));
    assert.equal(solid.dataBg, 'solid', '纯色模式 data-bg 不对');
    assert.equal(solid.beforeImage, 'none', '纯色模式不该画背景图');
    assert.ok(!/rgba\(0, 0, 0, 0\)/.test(solid.shellBg), '纯色模式外壳应该保持实心底色');

    // 2) 渐变：画三色渐变，外壳变透明
    await pushTheme('gradient');
    const gradient = await read();
    console.log('gradient=' + JSON.stringify(gradient));
    assert.equal(gradient.dataBg, 'gradient', '渐变模式 data-bg 不对');
    assert.match(gradient.beforeImage, /linear-gradient/, '渐变没画到 ::before 上：' + gradient.beforeImage);
    assert.match(gradient.beforeImage, /255, 0, 0/, '渐变第一色没生效');
    assert.equal(gradient.shellBg, 'rgba(0, 0, 0, 0)', '渐变模式外壳应该透明');
    assert.match(gradient.blur, /blur\(12px\)/, '背景模糊没生效');
    assert.equal(gradient.scrim, '0.4', '遮罩浓度没生效');

    // 3) 图片：用宿主给的虚拟主机地址
    const imageValues = Object.assign({}, base, {
      'bg-mode': 'image',
      'bg-image': 'url("https://celeste-asset.internal/background.png?v=1")'
    });
    await page.evaluate(payload => window.__deliver(payload), {
      kind: 'celeste.theme', version: 1, revision: 13, mode: 'light',
      presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
      light: { css: cssOf(imageValues), values: imageValues },
      dark: { css: cssOf(imageValues), values: imageValues }
    });
    await page.waitForTimeout(250);
    const image = await read();
    console.log('image=' + JSON.stringify(image));
    assert.equal(image.dataBg, 'image', '图片模式 data-bg 不对');
    assert.match(image.beforeImage, /celeste-asset\.internal/, '图片模式没吃宿主给的地址：' + image.beforeImage);

    // 4) 封面：地址跟着播放状态走，不在主题变量里
    await page.evaluate(() => window.__deliver({
      kind: 'celeste.state', version: 1,
      state: { trackId: 't1', title: 'X', artist: 'Y', album: 'Z', coverUrl: 'https://celeste-cover.internal/cover/t1?size=480' }
    }));
    await pushTheme('cover');
    const cover = await read();
    console.log('cover=' + JSON.stringify(cover));
    assert.equal(cover.dataBg, 'cover', '封面模式 data-bg 不对');
    assert.match(cover.beforeImage, /celeste-cover\.internal/, '封面模式没用当前曲目的封面：' + cover.beforeImage);

    // 层级：背景 0、标题栏 1，否则标题栏会被图盖住
    assert.equal(cover.layerZ, '0', '背景层 z-index 应为 0');
    assert.equal(cover.titlebarZ, '1', '标题栏应被抬到背景之上');

    // 5) 换背景类型：旧的那层先留在上面淡出，不是"啪"一下换掉
    const pushMode = async mode => {
      const values = Object.assign({}, base, { 'bg-mode': mode, 'bg-image': 'none' });
      await page.evaluate(payload => window.__deliver(payload), {
        kind: 'celeste.theme', version: 1, revision: 13, mode: 'light',
        presetId: 'sakura', accentSource: 'theme', accentCustom: '#e84865',
        light: { css: cssOf(values), values }, dark: { css: cssOf(values), values }
      });
    };
    await pushMode('gradient');
    await page.waitForTimeout(420);          // 停在渐变上，并让上一层的淡出走完
    await pushMode('image');
    const ghostState = await page.evaluate(() => {
      const ghosts = [...document.querySelectorAll('#bg-layer .bg-ghost')];
      return {
        count: ghosts.length,
        isGradient: ghosts.some(node => /linear-gradient/.test(node.style.backgroundImage)),
        fading: ghosts.some(node => node.classList.contains('is-out'))
      };
    });
    console.log('ghost=' + JSON.stringify(ghostState));
    assert.ok(ghostState.count >= 1, '换背景类型时应该有一层旧背景在淡出');
    assert.equal(ghostState.isGradient, true, '淡出的那层应该还是切换前的渐变');
    await page.waitForTimeout(700);
    assert.equal(await page.evaluate(() => document.querySelectorAll('#bg-layer .bg-ghost').length), 0,
      '淡出结束后不该残留叠层（会挡住底下的新背景）');

    // 外观页「背景」卡片
    const card = await page.evaluate(async () => {
      const host = document.createElement('div');
      host.id = 'smoke-host';
      host.style.cssText = 'position:fixed;inset:0;overflow:auto;background:var(--bg);z-index:99999;padding:12px';
      document.body.append(host);
      await window.CelesteAppearance.mount(host);
      const target = [...host.querySelectorAll('.settings-card')]
        .find(el => /背景/.test((el.querySelector('h2') || {}).textContent || ''));
      if (!target) return null;
      return {
        selects: [...target.querySelectorAll('select')].map(s => s.value),
        colors: target.querySelectorAll('input[type=color]').length,
        sliders: target.querySelectorAll('input[type=range]').length,
        buttons: [...target.querySelectorAll('button')].map(b => b.textContent)
      };
    });
    console.log('card=' + JSON.stringify(card));
    assert.ok(card, '没找到「背景」卡片');
    assert.equal(card.colors, 3, '渐变三色应有 3 个取色器');
    assert.equal(card.sliders, 3, '应有模糊/亮度/遮罩 3 个滑杆');
    assert.deepEqual(card.selects.slice(0, 1), ['cover'], '背景类型下拉没显示当前值');
    assert.ok(card.buttons.some(t => t.indexOf('选择图片') >= 0), '缺少「选择图片」按钮');

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('BACKGROUND_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
