/* 底栏按钮的显隐与顺序。
   宿主把构成存成一段人能看懂的串：left:favorite,playback-order,!queue|right:…|tools:…
   （`!` 表示收起来）。校验/去重/补全都在宿主做（WebAppearance.NormalizePlayerButtons），
   这里只照着排 DOM —— 网页不自己另编一套规则，免得两边打起来。

   为什么是"挪节点"而不是"重建按钮"：
   底栏所有点按都是启动时按 id 绑死的（shell.js 里一堆 $('id').addEventListener），
   挪节点不动监听；一旦重建元素，那些监听就全丢了，按钮会变成点了没反应。 */
(() => {
  'use strict';

  const GROUPS = ['left', 'right', 'tools'];

  // tools 组里音量是"图标 + 滑杆"两个元素，在页面上打包成了一个 #volume-group，
  // 所以目录里的 id 要先翻译一下再 getElementById。
  const ALIAS = { volume: 'volume-group' };
  const find = id => document.getElementById(ALIAS[id] || id);

  function host(group) {
    if (group === 'tools') return document.querySelector('.player-tools');
    return document.querySelector('.tb-side.tb-' + group);
  }

  function parse(text) {
    const map = { left: [], right: [], tools: [] };
    String(text || '').split('|').forEach(chunk => {
      const split = chunk.indexOf(':');
      if (split <= 0) return;
      const group = chunk.slice(0, split).trim();
      if (!GROUPS.includes(group)) return;
      chunk.slice(split + 1).split(',').forEach(token => {
        const value = token.trim();
        if (!value) return;
        map[group].push(value.charAt(0) === '!'
          ? { id: value.slice(1), hidden: true }
          : { id: value, hidden: false });
      });
    });
    return map;
  }

  /** 按宿主的构成串重排底栏。返回是否真的动到了元素。 */
  function apply(text) {
    if (!text) return false;
    const map = parse(text);
    let touched = 0;
    GROUPS.forEach(group => {
      const parent = host(group);
      if (!parent) return;
      map[group].forEach((item, index) => {
        const el = find(item.id);
        if (!el) return;
        // 用 hidden 属性而不是 style.display：样式表里有 !important（
        // .transport-buttons .icon-button{display:flex!important}），内联样式压不住它。
        el.hidden = item.hidden;
        // 工具区是一排 flex，靠 order 排；左右两簇直接按顺序 appendChild（DOM 挪动不影响已绑定的点按）
        if (group === 'tools') el.style.order = String(index + 1);
        else parent.appendChild(el);
        touched++;
      });
    });
    return touched > 0;
  }

  // 宿主改构成后会立刻 Save（修订号 +1），下一次推送的 celeste.theme 里就带着新的串
  document.addEventListener('celeste:theme', event => {
    const detail = (event && event.detail) || {};
    if (detail.playerButtons) apply(detail.playerButtons);
  });

  // 主题消息理论上比本文件晚到（都是 defer），万一早到就从 state 补一次
  const state = window.CelesteTheme && window.CelesteTheme.state;
  if (state && state.playerButtons) apply(state.playerButtons);

  window.CelestePlayerLayout = { apply };
})();
