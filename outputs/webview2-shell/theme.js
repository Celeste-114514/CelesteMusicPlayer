/* 主题应用。宿主在网页 ready 时立刻送一次 celeste.theme，之后只在修订号变了才重发。
   本文件只负责"把值写进根元素"，不做任何自己的默认表 —— 值永远来自宿主算好的那一套。 */
(() => {
  'use strict';
  const root = document.documentElement;

  function applyCss(text) {
    String(text || '').split(';').forEach(pair => {
      if (!pair) return;
      const index = pair.indexOf(':');
      if (index < 1) return;
      root.style.setProperty('--' + pair.slice(0, index).trim(), pair.slice(index + 1).trim());
    });
  }

  const state = { mode: 'light', light: null, dark: null, presetId: '', revision: 0, ready: false };

  // bundle 可以外部传入：外观页改完参数后手上就有算好的一套，
  // 不能再用 state（state 只在宿主推送时更新，会拿旧值把新值盖回去）。
  function apply(mode, bundle) {
    const chosen = bundle || (mode === 'dark' ? state.dark : state.light);
    if (!chosen) return;
    applyCss(chosen.css);
    root.dataset.theme = mode || 'light';
  }

  document.addEventListener('celeste:message', event => {
    const message = event.detail;
    if (message.kind !== 'celeste.theme') return;
    state.mode = message.mode || 'light';
    state.light = message.light || null;
    state.dark = message.dark || null;
    state.presetId = message.presetId || '';
    state.revision = message.revision || 0;
    state.ready = true;
    apply(state.mode);
    document.dispatchEvent(new CustomEvent('celeste:theme', { detail: { ...state } }));
  });

  window.CelesteTheme = {
    state,
    apply,
    applyCss,
    /** 草稿预览：直接覆盖根变量，不落盘；点保存才由宿主写入。 */
    preview(css) { applyCss(css); },
    /** 取消预览：回到宿主当前已保存的那套。 */
    restore() { apply(state.mode); },
    setMode(mode) { apply(mode || state.mode); },
    applyBundle(mode, bundle) { apply(mode || state.mode, bundle); },
    value(mode, name) {
      const bundle = (mode || state.mode) === 'dark' ? state.dark : state.light;
      return bundle && bundle.values ? bundle.values[name] : '';
    }
  };
})();
