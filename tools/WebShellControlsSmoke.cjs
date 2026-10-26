// 统一控件回归：下拉（原地增强）、键盘操作、动态选项、禁用态、共享模态。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellControlsSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = process.argv[2] || 'file:///C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell/index.html';
const SHOT_DIR = process.argv[3] || 'C:/Users/MSI/source/repos/CelesteMusicPlayer/outputs/webview2-shell';

const VALUES = {
  'bg': '#eae8e6', 'shell': '#fcfbfa', 'surface': '#ffffff', 'panel': '#f7f5f4', 'panel-soft': '#f1ecea',
  'line': '#e2dcd9', 'ink': '#28282e', 'muted': '#929097', 'row-active': '#fbe6ea',
  'accent': '#e84865', 'accent-soft': '#fbe6ea', 'on-accent': '#ffffff',
  'hover': '#112233', 'pressed': '#445566', 'focus': '#778899',
  'input-bg': '#aabbcc', 'input-line': '#ddeeff',
  'menu-bg': '#123456', 'overlay': '#00000080', 'shadow': '#65432140',
  'text-scale': '1', 'line-height': '1.5'
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
  const errors = [];
  try {
    const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
    page.on('pageerror', e => errors.push(e.message));
    page.on('console', msg => { if (msg.type() === 'error') errors.push('console: ' + msg.text()); });
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

    // ① 静态下拉全部接进统一控件
    const statics = await page.evaluate(() => {
      const all = [...document.querySelectorAll('select')];
      return {
        total: all.length,
        enhanced: all.filter(s => s.classList.contains('ui-select')).length,
        hidden: all.filter(s => s.classList.contains('ui-select-native')).length,
        ids: all.map(s => s.id || '(动态)').slice(0, 12)
      };
    });
    console.log('statics=' + JSON.stringify(statics));
    assert.ok(statics.total > 0, '页面上一个下拉都没有');
    assert.equal(statics.enhanced, statics.total, '有下拉没接进统一控件');
    assert.equal(statics.hidden, 0, '还有下拉被藏起来（旧做法残留）');

    // ② 鼠标点开：选项层是我们自己的 listbox，不是系统下拉
    await page.click('#library-source');
    await page.waitForTimeout(200);
    const opened = await page.evaluate(() => {
      const list = document.querySelector('#popup-layer .ui-listbox');
      const select = document.getElementById('library-source');
      return {
        exists: !!list,
        inLayer: !!list && list.closest('#popup-layer') !== null,
        role: list?.getAttribute('role'),
        options: list ? list.querySelectorAll('[role=option]').length : 0,
        expanded: select.getAttribute('aria-expanded'),
        // 原生 select 不能被藏起来（藏了就会和自绘层变成两个控件）
        selectVisible: select.getBoundingClientRect().width > 4
      };
    });
    console.log('opened=' + JSON.stringify(opened));
    assert.ok(opened.exists, '点开下拉后没有出现 listbox');
    assert.ok(opened.inLayer, 'listbox 没进共用浮层');
    assert.equal(opened.role, 'listbox', 'listbox 角色不对');
    assert.equal(opened.options, 3, '选项数量不对');
    assert.equal(opened.expanded, 'true', 'aria-expanded 没打开');
    assert.ok(opened.selectVisible, '原生 select 被藏起来了 —— 会和自绘层变成两个控件');

    // ③ 选一项：值变了、事件触发、层关掉、焦点回到 select
    const before = await page.evaluate(() => document.getElementById('library-source').value);
    await page.click('#popup-layer .ui-listbox [role=option]:nth-of-type(3)');
    await page.waitForTimeout(250);
    const after = await page.evaluate(() => ({
      value: document.getElementById('library-source').value,
      open: !!document.querySelector('#popup-layer .ui-listbox'),
      focused: document.activeElement?.id
    }));
    console.log('after=' + JSON.stringify(after) + ' before=' + before);
    assert.notEqual(after.value, before, '选完选项值没变');
    assert.equal(after.open, false, '选完没关闭');
    assert.equal(after.focused, 'library-source', '焦点没回到 select');

    // ④ 纯键盘：聚焦 + Enter 打开 + 方向键移动 + Enter 选中
    await page.focus('#library-source');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(200);
    const keyOpened = await page.evaluate(() => ({
      list: !!document.querySelector('#popup-layer .ui-listbox'),
      focused: document.activeElement?.getAttribute('role')
    }));
    assert.ok(keyOpened.list, '键盘打不开下拉');
    assert.equal(keyOpened.focused, 'option', '打开后焦点没进选项');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('Enter');
    await page.waitForTimeout(250);
    const keyPicked = await page.evaluate(() => ({
      value: document.getElementById('library-source').value,
      open: !!document.querySelector('#popup-layer .ui-listbox'),
      focused: document.activeElement?.id
    }));
    console.log('keyPicked=' + JSON.stringify(keyPicked));
    assert.equal(keyPicked.open, false, '键盘选完没关闭');
    assert.equal(keyPicked.focused, 'library-source', '键盘选完焦点没回来');

    // ⑤ Esc 关闭且焦点回到 select
    await page.focus('#library-source');
    await page.keyboard.press('ArrowDown');
    await page.waitForTimeout(150);
    await page.keyboard.press('Escape');
    await page.waitForTimeout(250);
    const esc = await page.evaluate(() => ({
      open: !!document.querySelector('#popup-layer .ui-listbox'),
      focused: document.activeElement?.id
    }));
    console.log('esc=' + JSON.stringify(esc));
    assert.equal(esc.open, false, 'Esc 没关掉下拉');
    assert.equal(esc.focused, 'library-source', 'Esc 之后焦点没回来');

    // ⑥ 禁用态：点不开
    const disabled = await page.evaluate(async () => {
      const host = document.createElement('div');
      host.id = 'ctl-probe';
      document.body.append(host);
      const select = document.createElement('select');
      select.innerHTML = '<option value="a">甲</option><option value="b">乙</option>';
      select.disabled = true;
      host.append(select);
      window.CelesteControls.enhanceSelect(select);
      select.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true }));
      await new Promise(r => setTimeout(r, 150));
      return { open: !!document.querySelector('#popup-layer .ui-listbox') };
    });
    console.log('disabled=' + JSON.stringify(disabled));
    assert.equal(disabled.open, false, '禁用的下拉还能打开');

    // ⑦ 动态加进来的下拉也要自动接上
    const dynamic = await page.evaluate(async () => {
      const host = document.createElement('div');
      document.body.append(host);
      const select = document.createElement('select');
      select.innerHTML = '<option value="x">新的一项</option>';
      host.append(select);
      await new Promise(r => setTimeout(r, 120));
      return { enhanced: select.classList.contains('ui-select') };
    });
    console.log('dynamic=' + JSON.stringify(dynamic));
    assert.ok(dynamic.enhanced, '动态插入的下拉没自动接进统一控件');

    // ⑧ 共享模态：Esc 只关最上层，焦点还给触发者
    const modal = await page.evaluate(async () => {
      const trigger = document.createElement('button');
      trigger.id = 'modal-trigger';
      document.body.append(trigger);
      trigger.focus();
      const promise = window.CelesteControls.openModal({
        title: '确认操作', message: '这条会丢掉未保存的改动。',
        actions: [{ value: false, label: '取消', variant: 'secondary' }, { value: true, label: '确定', variant: 'primary' }]
      });
      await new Promise(r => setTimeout(r, 150));
      const shown = {
        modal: !!document.querySelector('.ui-modal-scrim .ui-modal'),
        title: document.querySelector('.ui-modal h3')?.textContent,
        focused: document.activeElement?.className
      };
      document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
      const value = await promise;
      await new Promise(r => setTimeout(r, 250));
      return {
        shown,
        value,
        closed: !document.querySelector('.ui-modal-scrim'),
        focusedBack: document.activeElement?.id
      };
    });
    console.log('modal=' + JSON.stringify(modal));
    assert.ok(modal.shown.modal, '模态没出来');
    assert.equal(modal.shown.title, '确认操作', '模态标题不对');
    assert.equal(modal.value, 'cancel', 'Esc 没返回取消');
    assert.ok(modal.closed, 'Esc 之后模态没移除');
    assert.equal(modal.focusedBack, 'modal-trigger', '模态关掉后焦点没还给触发者');

    // ⑨ 复选框三种状态都画得出来（未选 / 选中 / 部分选中）
    const boxes = await page.evaluate(() => {
      const host = document.createElement('div');
      document.body.append(host);
      const make = state => {
        const box = document.createElement('input');
        box.type = 'checkbox';
        if (state === 'checked') box.checked = true;
        if (state === 'mixed') box.indeterminate = true;
        host.append(box);
        return box;
      };
      const a = make('none'), b = make('checked'), c = make('mixed');
      const style = el => {
        const s = getComputedStyle(el);
        return { bg: s.backgroundColor, border: s.borderTopColor, radius: s.borderTopLeftRadius, after: getComputedStyle(el, '::after').content };
      };
      return { plain: style(a), checked: style(b), mixed: style(c) };
    });
    console.log('boxes=' + JSON.stringify(boxes));
    assert.equal(boxes.plain.bg, 'rgb(170, 187, 204)', '未选中没吃 --input-bg');
    assert.notEqual(boxes.checked.bg, boxes.plain.bg, '选中态没变色');
    assert.notEqual(boxes.mixed.bg, boxes.plain.bg, '部分选中态没变色');
    assert.notEqual(boxes.mixed.after, 'none', '部分选中没画横杠');

    // ⑩ 硬编码粉色焦点框不能再出现
    const pink = await page.evaluate(() => {
      const hits = [];
      for (const sheet of document.styleSheets) {
        let rules;
        try { rules = sheet.cssRules; } catch { continue; }
        for (const rule of rules) {
          const text = rule.cssText || '';
          if (/#e84865/i.test(text) && /(outline|box-shadow|border)/i.test(text)) hits.push(text.slice(0, 160));
        }
      }
      return hits;
    });
    console.log('pink=' + JSON.stringify(pink));
    assert.equal(pink.length, 0, '还有硬编码粉色焦点/边框：' + pink.join(' | '));

    assert.deepEqual(errors, [], '页面有 JS 异常：' + errors.join(' / '));
    console.log('CONTROLS_OK');
  } catch (error) {
    console.error('FAIL:', error.message);
    if (errors.length) console.error('异常:', errors);
    process.exitCode = 1;
  } finally {
    await browser.close();
  }
})();
