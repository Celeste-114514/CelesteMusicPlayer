/* 托盘菜单的渲染层。只认两样输入：宿主下发的主题变量，和一组带 actionId 的菜单项。
   点一下就把 actionId 发给宿主，自己不执行任何命令 —— 命令归后端适配器管。
   键盘：上下移动焦点、Enter 执行、Esc 关闭；失焦交给宿主判断是不是"点到了外面"。 */
(() => {
  'use strict';
  const menu = document.getElementById('menu'), empty = document.getElementById('empty');
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  let items = [], focusIndex = -1;
  let invoking = false;
  const iconPaths = {
    showMain:'M3 4h14v12H3z M3 8h14', showMini:'M3 4h14v12H3z M10 11h5v3h-5z',
    showScattered:'M8 14V5l8-2v9 M8 8l8-2 M3 14h5 M11 12h5',
    toggleLyrics:'M4 5h12 M4 10h8 M4 15h10', unlockLyrics:'M6 9V6a4 4 0 0 1 7-2 M4 9h12v8H4z',
    previous:'M5 5v10 M15 5l-8 5 8 5z', togglePlay:'M7 4l9 6-9 6z',
    next:'M15 5v10 M5 5l8 5-8 5z',
    favorite:'M10 16S3 12 3 7.5C3 3 8 3 10 6c2-3 7-3 7 1.5C17 12 10 16 10 16z',
    openSettings:'M4 5h12 M4 10h12 M4 15h12 M7 3v4 M13 8v4 M8 13v4',
    openLogs:'M3 6V4h5l2 2h7v10H3z', openUpdate:'M10 13V3 M6 7l4-4 4 4 M4 13v4h12v-4',
    exit:'M9 3H4v14h5 M9 10h8 M14 7l3 3-3 3'
  };
  function actionIcon(item) {
    const svg=document.createElementNS('http://www.w3.org/2000/svg','svg');
    svg.classList.add('tray-icon');svg.setAttribute('viewBox','0 0 20 20');svg.setAttribute('aria-hidden','true');
    const path=document.createElementNS(svg.namespaceURI,'path');
    path.setAttribute('d',item.actionId==='togglePlay'&&item.label==='暂停'?'M7 4v12 M13 4v12':iconPaths[item.actionId]||iconPaths.showMain);
    svg.append(path);return svg;
  }

  const send = payload => { if (live) window.chrome.webview.postMessage({ kind: 'celeste.command', version: 1, command: 'trayMenu', ...payload }); };

  function applyTheme(theme) {
    if (!theme) return;
    if (theme.mode) document.documentElement.dataset.mode = theme.mode;
    // 宿主给的是同一套预设解析出来的 CSS 变量块，直接铺到 :root
    const root = document.documentElement;
    Object.entries(theme.vars || {}).forEach(([key, value]) => root.style.setProperty('--' + key, value));
    // 字号缩放会改行高，换肤后必须重新量一次（此时若有 items 会被紧接着的 render 再量一次，无害）
    // Measure only after the corresponding item list has been rendered.
  }

  /* 量一次菜单真实尺寸报给宿主。must be synchronous：菜单窗平时是隐藏的，
     隐藏的 WebView 里 requestAnimationFrame 不一定会跑，等它量就永远等不到。 */
  let lastSize = '';
  function reportSize() {
    const width = Math.ceil(Math.max(menu.offsetWidth,menu.scrollWidth)), height = Math.ceil(Math.max(menu.offsetHeight,menu.scrollHeight));
    if (width <= 0 || height <= 0) return;
    const key = width + 'x' + height;
    if (key === lastSize) { send({ action: 'size', width, height }); return; }
    lastSize = key;
    send({ action: 'size', width, height });
  }

  function render() {
    invoking = false;
    menu.replaceChildren();
    empty.hidden = items.length > 0;
    if(items.length){
      const brand=document.createElement('div');brand.className='tray-brand';brand.setAttribute('role','presentation');
      brand.textContent='CELESTE';const dot=document.createElement('span');brand.append(dot);menu.append(brand);
    }
    items.forEach((item, index) => {
      if (item.separator) { const sep = document.createElement('div'); sep.className = 'tray-sep'; sep.setAttribute('role', 'separator'); menu.append(sep); return; }
      const button = document.createElement('button');
      if(item.actionId==='exit'&&index>0&&!items[index-1].separator){const sep=document.createElement('div');sep.className='tray-sep';sep.setAttribute('role','separator');menu.append(sep);}
      button.className = 'tray-item'; button.type = 'button'; button.dataset.index = String(index);
      button.setAttribute('role', 'menuitem'); button.disabled = item.enabled === false;
      if (item.checked !== null && item.checked !== undefined) button.setAttribute('aria-checked', String(!!item.checked));
      const check = document.createElementNS('http://www.w3.org/2000/svg','svg'); check.classList.add('tray-check'); check.setAttribute('viewBox','0 0 16 16'); check.setAttribute('aria-hidden','true');
      const tick=document.createElementNS('http://www.w3.org/2000/svg','path');tick.setAttribute('d','M3 8l3 3 7-7');tick.setAttribute('fill','none');tick.setAttribute('stroke','currentColor');tick.setAttribute('stroke-width','1.6');check.append(tick);
      const label = document.createElement('span'); label.className = 'tray-label'; label.textContent = item.label || '';
      button.append(actionIcon(item), label, check);
      const invoke = () => { if (button.disabled || invoking) return; invoking = true; send({ action: 'invoke', actionId: item.actionId }); };
      button.addEventListener('pointerdown', event => { if (event.button === 0) { event.preventDefault(); invoke(); } });
      button.addEventListener('click', invoke); // Keyboard / accessibility activation.
      button.addEventListener('mouseenter', () => { setKeyboardMode(false); });
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
  // The native parent owns outside-click dismissal. DOM focus changes are not
  // evidence of leaving the window (especially while clicking the first row).

  if (live) {
    window.CelesteTray={render:message=>{
      applyTheme(message.theme);items=message.items||[];render();
      return {width:Math.ceil(Math.max(menu.offsetWidth,menu.scrollWidth)),height:Math.ceil(Math.max(menu.offsetHeight,menu.scrollHeight))};
    }};
    window.chrome.webview.addEventListener('message', event => {
      const message = event.data;
      if (!message || message.version !== 1) return;
      if (message.kind === 'celeste.trayTheme') { applyTheme(message.theme); if (message.items) { items = message.items; render(); } return; }
      if (message.kind === 'celeste.trayItems') { items = message.items || []; render(); return; }
    });
    send({ action: 'ready' });
  }
})();
