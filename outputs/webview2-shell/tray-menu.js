/* 托盘菜单的渲染层。只认两样输入：宿主下发的主题变量，和一组带 actionId 的菜单项。
   点一下就把 actionId 发给宿主，自己不执行任何命令 —— 命令归后端适配器管。
   键盘：上下移动焦点、Enter 执行、Esc 关闭；失焦交给宿主判断是不是"点到了外面"。 */
(() => {
  'use strict';
  const menu = document.getElementById('menu'), empty = document.getElementById('empty');
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  let items = [], focusIndex = -1;

  const send = payload => { if (live) window.chrome.webview.postMessage({ kind: 'celeste.command', version: 1, command: 'trayMenu', ...payload }); };

  function applyTheme(theme) {
    if (!theme) return;
    if (theme.mode) document.documentElement.dataset.mode = theme.mode;
    // 宿主给的是同一套预设解析出来的 CSS 变量块，直接铺到 :root
    const root = document.documentElement;
    Object.entries(theme.vars || {}).forEach(([key, value]) => root.style.setProperty('--' + key, value));
    // 字号缩放会改行高，换肤后必须重新量一次（此时若有 items 会被紧接着的 render 再量一次，无害）
    reportSize();
  }

  /* 量一次菜单真实尺寸报给宿主。must be synchronous：菜单窗平时是隐藏的，
     隐藏的 WebView 里 requestAnimationFrame 不一定会跑，等它量就永远等不到。 */
  let lastSize = '';
  function reportSize() {
    const width = Math.ceil(menu.offsetWidth), height = Math.ceil(menu.offsetHeight);
    if (width <= 0 || height <= 0) return;
    const key = width + 'x' + height;
    if (key === lastSize) { send({ action: 'size', width, height }); return; }
    lastSize = key;
    send({ action: 'size', width, height });
  }

  function render() {
    menu.replaceChildren();
    empty.hidden = items.length > 0;
    items.forEach((item, index) => {
      if (item.separator) { const sep = document.createElement('div'); sep.className = 'tray-sep'; sep.setAttribute('role', 'separator'); menu.append(sep); return; }
      const button = document.createElement('button');
      button.className = 'tray-item'; button.type = 'button'; button.dataset.index = String(index);
      button.setAttribute('role', 'menuitem'); button.disabled = item.enabled === false;
      if (item.checked !== null && item.checked !== undefined) button.setAttribute('aria-checked', String(!!item.checked));
      const check = document.createElement('span'); check.className = 'tray-check'; check.textContent = '✓';
      const label = document.createElement('span'); label.className = 'tray-label'; label.textContent = item.label || '';
      button.append(check, label);
      button.addEventListener('click', () => send({ action: 'invoke', actionId: item.actionId }));
      button.addEventListener('mouseenter', () => { setKeyboardMode(false); focusIndex = index; focusAt(focusIndex); });
      menu.append(button);
    });
    focusIndex = -1;
    setKeyboardMode(false);
    reportSize();
  }

  /* 输入方式：只有键盘操作才画焦点环（见 css 里的 html[data-kbd]） */
  function setKeyboardMode(on) { document.documentElement.dataset.kbd = on ? '1' : ''; }

  function focusAt(index) {
    const rows = [...menu.querySelectorAll('.tray-item:not([disabled])')];
    if (!rows.length) return;
    const clamped = Math.max(0, Math.min(rows.length - 1, index));
    rows[clamped].focus();
    focusIndex = clamped;
  }

  function move(delta) {
    const rows = [...menu.querySelectorAll('.tray-item:not([disabled])')];
    if (!rows.length) return;
    const current = rows.indexOf(document.activeElement);
    focusAt(current < 0 ? (delta > 0 ? 0 : rows.length - 1) : current + delta);
  }

  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') { send({ action: 'close' }); return; }
    if (event.key === 'ArrowDown') { setKeyboardMode(true); move(1); event.preventDefault(); }
    else if (event.key === 'ArrowUp') { setKeyboardMode(true); move(-1); event.preventDefault(); }
    else if (event.key === 'Home') { setKeyboardMode(true); focusAt(0); event.preventDefault(); }
    else if (event.key === 'End') { setKeyboardMode(true); focusAt(9999); event.preventDefault(); }
  });
  // 焦点跑出菜单 = 用户点到别处了，让宿主收起（宿主还要判断是不是点到了自己的子窗口）
  document.addEventListener('focusout', event => { if (!menu.contains(event.relatedTarget)) send({ action: 'blur' }); });

  if (live) {
    window.chrome.webview.addEventListener('message', event => {
      const message = event.data;
      if (!message || message.version !== 1) return;
      if (message.kind === 'celeste.trayTheme') { applyTheme(message.theme); if (message.items) { items = message.items; render(); } return; }
      if (message.kind === 'celeste.trayItems') { items = message.items || []; render(); return; }
    });
    send({ action: 'ready' });
  }
})();
