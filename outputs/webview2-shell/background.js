/* 背景层：把 --bg-mode 翻译成 <html data-bg="...">，并处理"封面"这一种特殊来源。

   为什么封面要单独一段：它不是主题变量，是跟着当前曲目走的 ——
   换一首歌，封面就换，但没有主题消息，只能靠 celeste.state / celeste:message 感知。

   图片模式的地址由宿主给（拷进受控目录后的 https://celeste-asset.internal/…），
   网页这边拿不到本机路径，CSP 也不许 —— 所以这里只管用，不管读。 */
(() => {
  'use strict';

  const root = document.documentElement;
  const MODES = ['gradient', 'cover', 'image'];

  function mode() {
    const value = getComputedStyle(root).getPropertyValue('--bg-mode').trim();
    return MODES.includes(value) ? value : 'solid';
  }

  function currentCover() {
    const shell = window.CelesteShell;
    const state = shell && shell.getState ? shell.getState() : null;
    return (state && typeof state.coverUrl === 'string') ? state.coverUrl : '';
  }

  function apply() {
    const next = mode();
    root.dataset.bg = next;
    if (next !== 'cover') return;
    // 封面模式：没封面时给 none，让遮罩下面只剩底色，而不是留着上一首的图
    const url = currentCover();
    root.style.setProperty('--bg-image', url ? 'url("' + url + '")' : 'none');
  }

  // 主题值变了（换预设/拖滑杆/预览）
  document.addEventListener('celeste:theme-applied', apply);
  document.addEventListener('celeste:theme', apply);
  // 播放状态变了（换曲 → 换封面）
  document.addEventListener('celeste:message', event => {
    const message = (event && event.detail) || {};
    if (message.kind === 'celeste.state' || message.kind === 'celeste.reply') apply();
  });

  apply();
  window.CelesteBackground = { apply };
})();
