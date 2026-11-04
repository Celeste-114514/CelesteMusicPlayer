(() => {
  'use strict';
  const shell = window.CelesteShell;
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const buttons = ['window-fullscreen', 'np-fullscreen'].map(id => document.getElementById(id)).filter(Boolean);
  let fullscreen = false;
  function render(value) {
    fullscreen = !!value;
    document.body.classList.toggle('is-fullscreen', fullscreen);
    buttons.forEach(button => {
      button.setAttribute('aria-pressed', String(fullscreen));
      button.setAttribute('aria-label', fullscreen ? '退出全屏' : '全屏');
      button.title = fullscreen ? '退出全屏（F11 / Esc）' : '全屏（F11）';
    });
  }
  async function set(value) {
    if (live) { shell.send('window', { action: 'fullscreen', value }); return; }
    try {
      if (value) await document.documentElement.requestFullscreen();
      else if (document.fullscreenElement) await document.exitFullscreen();
    } catch { shell.notify('当前预览环境未允许全屏。'); }
  }
  buttons.forEach(button => button.addEventListener('click', () => set(!fullscreen)));
  document.addEventListener('celeste:message', event => {
    if (event.detail.kind !== 'celeste.window') return;
    document.body.classList.toggle('is-maximized', !!event.detail.maximized);
    render(event.detail.fullscreen);
  });
  document.addEventListener('fullscreenchange', () => { if (!live) render(!!document.fullscreenElement); });
  document.addEventListener('keydown', event => {
    if (event.key === 'F11' || event.key === 'Escape' && fullscreen) {
      event.preventDefault(); event.stopImmediatePropagation();
      set(event.key === 'Escape' ? false : !fullscreen);
    }
  }, true);
  if (live) {
    const grips = document.createElement('div');
    grips.className = 'window-resize-grips';
    grips.setAttribute('aria-hidden', 'true');
    // Grid order, skipping the center. Codes are Win32 non-client hit targets.
    for (const edge of [13, 12, 14, 10, 11, 16, 15, 17]) {
      const grip = document.createElement('div');
      grip.dataset.resizeEdge = String(edge);
      grip.addEventListener('pointerdown', event => {
        if (event.button !== 0 || event.pointerType !== 'mouse') return;
        event.preventDefault();
        event.stopPropagation();
        shell.send('window', { action: 'resize', edge });
      });
      grips.append(grip);
    }
    document.body.append(grips);
    shell.send('window', { action: 'state' });
  }
})();
