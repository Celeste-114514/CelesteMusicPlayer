/* 半圆导航的几何与构成。
   宿主存的构成串：ring1:songs,albums,!folders|ring2:…（`!` = 收起）。

   为什么几何要在这里算、而不是只改 CSS：
   轮盘是一个 600×290 的 SVG（圆心 300,10），图标位置是「极角 + 半径」换算成百分比，
   引导弧是 path 的 d 属性。半径一变，这两处必须同时改，否则轨道线和图标会错位 ——
   "只改 CSS 宽度"改不动这两样。

   角度规则：可视角 12°→168°（顺时针，从上边缘往左边缘），可见项目均分这段弧。
   收起某一项后重新均分，所以不会留下空位。 */
(() => {
  'use strict';

  const GROUPS = ['ring1', 'ring2'];
  const CX = 300, CY = 10, W = 600, H = 290;
  const START = 12, SPAN = 156;

  let lastText = '';

  function parse(text) {
    const map = { ring1: [], ring2: [] };
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

  function ringRadius(index) {
    const name = index === 0 ? '--nav-ring-1' : '--nav-ring-2';
    const raw = getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    const value = parseFloat(raw.replace(/px$/, ''));
    if (!Number.isFinite(value) || value <= 0) return index === 0 ? 150 : 238;
    return value;
  }

  const itemOf = id => document.querySelector('.corner-item[data-page="' + id + '"]');

  // 引导弧：沿着"图标所在带"的边画，所以半径从图标圈反推（带半宽 45）
  function arcOpen(radius) {
    const left = CX - radius, right = CX + radius;
    return 'M' + left + ' ' + CY + 'A' + radius + ' ' + radius + ' 0 0 0 ' + right + ' ' + CY;
  }

  function arcFill(radius) {
    const left = CX - radius, right = CX + radius;
    return 'M' + left + ' ' + CY + 'H' + right + 'A' + radius + ' ' + radius + ' 0 0 1 ' + left + ' ' + CY + 'Z';
  }

  function paintArcs() {
    const r1 = ringRadius(0), r2 = ringRadius(1);
    const core = Math.max(20, r1 - 45);
    const inner = r1 + 45;
    // 外圈带要比内圈带再宽一点（原来 238+42=280，正好是设计值）
    const edge = Math.max(inner + 20, r2 + 42);
    const set = (selector, d) => {
      const node = document.querySelector(selector);
      if (node) node.setAttribute('d', d);
    };
    set('.quarter-core', arcOpen(core));
    set('.quarter-inner', arcOpen(inner));
    set('.quarter-edge', arcOpen(edge));
    set('.quarter-fill', arcFill(edge));
  }

  function place(map) {
    let touched = 0;
    GROUPS.forEach((group, ringIndex) => {
      const list = map[group] || [];
      const radius = ringRadius(ringIndex);
      list.forEach(item => {
        const el = itemOf(item.id);
        if (!el) return;
        el.hidden = item.hidden;
        touched++;
      });
      const visible = list.filter(item => !item.hidden);
      const count = visible.length;
      visible.forEach((item, index) => {
        const el = itemOf(item.id);
        if (!el) return;
        // 只剩一个时放正中，否则均分整段弧
        const t = count <= 1 ? 0.5 : index / (count - 1);
        const angle = (START + t * SPAN) * Math.PI / 180;
        el.classList.toggle('ring-1', ringIndex === 0);
        el.classList.toggle('ring-2', ringIndex === 1);
        el.style.left = ((CX - Math.cos(angle) * radius) / W * 100) + '%';
        el.style.top = ((CY + Math.sin(angle) * radius) / H * 100) + '%';
      });
    });
    return touched;
  }

  /** 按构成串重排。返回是否动到了元素。 */
  function apply(text) {
    if (!text) return false;
    lastText = text;
    const touched = place(parse(text));
    paintArcs();
    return touched > 0;
  }

  /** 只重算几何（半径被拖动时用，构成没变） */
  function relayout() {
    if (lastText) place(parse(lastText));
    paintArcs();
  }

  // 构成搭主题那趟车送来
  document.addEventListener('celeste:theme', event => {
    const detail = (event && event.detail) || {};
    if (detail.navItems) {
      if (detail.navItems !== lastText) apply(detail.navItems);
      else relayout();
    } else {
      relayout();
    }
  });

  // 预览/切换主题时半径是直接写根变量的，没有主题消息，只能靠这个事件重算
  document.addEventListener('celeste:theme-applied', () => relayout());

  const state = window.CelesteTheme && window.CelesteTheme.state;
  if (state && state.navItems) apply(state.navItems);
  else relayout();

  window.CelesteNavLayout = { apply, relayout };
})();
