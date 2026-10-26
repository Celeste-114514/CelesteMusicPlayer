/* 主题应用。宿主在网页 ready 时立刻送一次 celeste.theme，之后只在修订号变了才重发。
   本文件只负责"把值写进根元素"，不做任何自己的默认表 —— 值永远来自宿主算好的那一套。 */
(() => {
  'use strict';
  const root = document.documentElement;

  // 宿主给的是 "--bg:#eae8e6;--shell:#fcfbfa" 这种已经带 -- 前缀的串。
  // ⚠ 别再自己拼一次 --：拼成 ----bg 会让 setProperty 直接抛错，后面所有变量都不生效
  //   （改深浅、换预设全都"没反应"就是这个造成的）。
  // 单个坏变量也不许连累整批，所以逐条容错。
  function applyCss(text) {
    let applied = 0;
    String(text || '').split(';').forEach(pair => {
      if (!pair) return;
      const index = pair.indexOf(':');
      if (index < 1) return;
      const name = pair.slice(0, index).trim();
      const value = pair.slice(index + 1).trim();
      if (!name || !value) return;
      try { root.style.setProperty(name.startsWith('--') ? name : '--' + name, value); applied++; }
      catch { /* 跳过这一个，不要连累其它变量 */ }
    });
    return applied;
  }

  const state = { mode: 'light', light: null, dark: null, presetId: '', revision: 0, ready: false, playerButtons: '', navItems: '' };

  // bundle 可以外部传入：外观页改完参数后手上就有算好的一套，
  // 不能再用 state（state 只在宿主推送时更新，会拿旧值把新值盖回去）。
  function apply(mode, bundle) {
    const chosen = bundle || (mode === 'dark' ? state.dark : state.light);
    if (!chosen) return;
    const applied = applyCss(chosen.css);
    window.CelesteTheme.lastApplied = applied;
    root.dataset.theme = mode || 'light';
    // 一个变量都没写进去就明说，别让用户对着没变化的界面猜
    if (applied === 0 && window.CelesteShell && chosen.css) window.CelesteShell.notify('主题变量没能写进页面，请把这一情况反馈一下。');
    // 画在 canvas 里的东西（进度波形）拿不到 CSS 变量，得有人告诉它们重画
    document.dispatchEvent(new CustomEvent('celeste:theme-applied', { detail: { mode: root.dataset.theme } }));
  }

  document.addEventListener('celeste:message', event => {
    const message = event.detail;
    if (message.kind !== 'celeste.theme') return;
    state.mode = message.mode || 'light';
    state.light = message.light || null;
    state.dark = message.dark || null;
    state.presetId = message.presetId || '';
    state.revision = message.revision || 0;
    // 底栏按钮的显隐/顺序搭同一趟车（宿主那边存在修订号里），
    // 单独一个字段，别和外观数值混一起 —— player-layout.js 只认它。
    if (typeof message.playerButtons === 'string') state.playerButtons = message.playerButtons;
    // 导航构成也搭这趟车（只有项目增删/换圈时才变，几何由 nav-layout.js 重算）
    if (typeof message.navItems === 'string') state.navItems = message.navItems;
    state.ready = true;
    apply(state.mode);
    document.dispatchEvent(new CustomEvent('celeste:theme', { detail: { ...state } }));
  });

  window.CelesteTheme = {
    state,
    apply,
    applyCss,
    /** 草稿预览：直接覆盖根变量，不落盘；点保存才由宿主写入。 */
    preview(css) {
      applyCss(css);
      document.dispatchEvent(new CustomEvent('celeste:theme-applied', { detail: { mode: root.dataset.theme, preview: true } }));
    },
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
