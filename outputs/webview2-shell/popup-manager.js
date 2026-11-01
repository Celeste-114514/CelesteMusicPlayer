/* 浮层总管（popup manager）。
   菜单、下拉、列选择器、提示气泡都走这里，所以层级、贴边翻转、外部点击、Esc、
   焦点恢复只有一份实现。位置外壳和动画内层分开 —— transform 互相覆盖是这类
   浮层最常见的毛病。 */
(() => {
  'use strict';
  const motion = window.CelesteMotion;
  const stack = [];
  let layer = null;

  function ensureLayer() {
    if (layer && layer.isConnected) return layer;
    layer = document.createElement('div');
    layer.id = 'popup-layer';
    layer.className = 'popup-layer';
    document.body.append(layer);
    return layer;
  }

  const entries = new Map();   // id -> entry
  let tokenSeed = 0;           // 每次打开发一个令牌：旧浮层的退场动画不许拆掉新浮层

  function topEntry() {
    for (let index = stack.length - 1; index >= 0; index -= 1) {
      const entry = stack[index];
      if (entry.element.isConnected) return entry;
    }
    return null;
  }

  function measure(element) {
    const rect = element.getBoundingClientRect();
    return { width: rect.width, height: rect.height, left: rect.left, top: rect.top, right: rect.right, bottom: rect.bottom };
  }

  /**
   * 贴边计算：优先首选方向，放不下就翻面，再放不下就夹在视口内。
   * 返回 { left, top, maxHeight }。
   */
  function place(element, anchor, options = {}) {
    const margin = options.margin ?? 12;
    const gap = options.gap ?? 8;
    const viewportWidth = window.innerWidth;
    const viewportHeight = window.innerHeight;
    const size = measure(element);
    let anchorRect;
    if (anchor && typeof anchor.getBoundingClientRect === 'function') anchorRect = anchor.getBoundingClientRect();
    else if (anchor && typeof anchor.x === 'number') anchorRect = { left: anchor.x, top: anchor.y, right: anchor.x, bottom: anchor.y, width: 0, height: 0 };
    else anchorRect = { left: margin, top: margin, right: margin, bottom: margin, width: 0, height: 0 };

    const placement = options.placement || 'bottom-start';
    const [side, align] = placement.split('-');
    let left = align === 'end' ? anchorRect.right - size.width : anchorRect.left;
    let top = side === 'top' ? anchorRect.top - size.height - gap : anchorRect.bottom + gap;
    if (side === 'right') left = anchorRect.right + gap;
    if (side === 'left') left = anchorRect.left - size.width - gap;
    if (side === 'bottom' && align === 'center') left = anchorRect.left + anchorRect.width / 2 - size.width / 2;

    // 放不下就翻到另一侧
    const overflowsBottom = top + size.height > viewportHeight - margin;
    const overflowsTop = top < margin;
    if (side === 'bottom' && overflowsBottom && anchorRect.top - size.height - gap > margin) top = anchorRect.top - size.height - gap;
    else if (side === 'top' && overflowsTop && anchorRect.bottom + size.height + gap < viewportHeight - margin) top = anchorRect.bottom + gap;

    // 仍然放不下就夹住，并限制高度让内容自己滚
    const availableBelow = viewportHeight - top - margin;
    const availableAbove = anchorRect.top - gap - margin;
    let maxHeight = options.maxHeight ?? 360;
    if (options.fitViewport) maxHeight = Math.min(maxHeight, viewportHeight - 2 * margin);
    else if (side === 'top') maxHeight = Math.min(maxHeight, Math.max(120, availableAbove));
    else maxHeight = Math.min(maxHeight, Math.max(120, availableBelow));

    left = Math.max(margin, Math.min(left, viewportWidth - size.width - margin));
    top = Math.max(margin, Math.min(top, viewportHeight - size.height - margin));

    element.style.left = `${Math.round(left)}px`;
    element.style.top = `${Math.round(top)}px`;
    element.style.maxHeight = `${Math.round(maxHeight)}px`;
    return { left, top, maxHeight };
  }

  function reposition(entry) {
    if (!entry || !entry.element.isConnected) return;
    place(entry.element, entry.anchor, entry.options);
  }

  /**
   * 打开一个浮层。
   * options: { id, anchor, className, role, ariaLabel, placement, gap, margin, maxHeight,
   *            onClose, exclusive, dismissable, focus }
   */
  function open(options = {}) {
    const id = options.id || `popup-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`;
    if (entries.has(id)) close(id);
    if (options.exclusive !== false) closeAll(id);

    const element = options.element || document.createElement('div');
    element.classList.add('ui-popup');
    if (options.className) element.classList.add(...String(options.className).split(' ').filter(Boolean));
    if (options.role) element.setAttribute('role', options.role);
    if (options.ariaLabel) element.setAttribute('aria-label', options.ariaLabel);
    element.dataset.popupId = id;

    const returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const entry = {
      id, element, anchor: options.anchor || null, options,
      returnFocus, generation: 0, onClose: options.onClose || null,
      // 令牌：同一个 id 被反复打开时，旧的那次退场动画结束时只能拆自己那一份，
      // 不许把刚打开的新浮层一起拆掉（"打开未完成就反开"最常见的毛病）。
      token: ++tokenSeed
    };
    entries.set(id, entry);
    stack.push(entry);
    element.dataset.popupToken = String(entry.token);

    ensureLayer().append(element);
    // 先摆位再动画：位置漂移是最刺眼的毛病
    place(element, entry.anchor, options);
    if (options.content) {
      if (typeof options.content === 'string') element.innerHTML = options.content;
      else element.append(options.content);
    }
    place(element, entry.anchor, options);

    if (options.focus !== 'none') {
      const focusTarget = element.matches('input,textarea,select,button,[tabindex]')
        ? element
        : element.querySelector('[data-autofocus],input,textarea,select,button:not([disabled]),[tabindex]:not([tabindex="-1"])');
      (focusTarget || element).focus({ preventScroll: true });
    }
    motion.popupEnter(element);
    return entry;
  }

  function close(id, options = {}) {
    const entry = typeof id === 'object' ? id : entries.get(id);
    if (!entry) return Promise.resolve();
    entries.delete(entry.id);
    const index = stack.indexOf(entry);
    if (index >= 0) stack.splice(index, 1);
    entry.generation += 1;
    const element = entry.element;
    // 焦点要**立刻**回到触发它的控件，不能等退场动画走完 ——
    // 这 100 多毫秒里键盘操作会丢（用户按 Esc 关掉菜单，马上按方向键就"没反应"）。
    if (options.restoreFocus !== false && entry.returnFocus?.isConnected) {
      try { entry.returnFocus.focus({ preventScroll: true }); } catch { /* 目标没了就算了 */ }
    }
    const done = () => {
      // 只有还是这一份（令牌没变）才拆。同一个元素被重新打开过时，旧动画不许拆它。
      if (element.dataset.popupToken === String(entry.token)) element.remove();
      entry.onClose?.();
    };
    if (options.immediate) { done(); return Promise.resolve(); }
    return motion.popupExit(entry.element, done);
  }

  function closeAll(except) {
    [...stack].forEach(entry => { if (entry.id !== except) close(entry.id); });
  }

  function isOpen(id) { return entries.has(id); }

  // —— 全局行为 ——
  document.addEventListener('pointerdown', event => {
    const entry = topEntry();
    if (!entry) return;
    const target = event.target;
    if (entry.element.contains(target)) return;
    if (entry.options.anchor instanceof HTMLElement && entry.options.anchor.contains(target)) return;
    close(entry.id);
  }, true);

  document.addEventListener('keydown', event => {
    const entry = topEntry();
    if (!entry) return;
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close(entry.id);
      return;
    }
    if (event.key === 'Tab') {
      // 菜单类浮层按 Tab 收起（焦点自然回到触发器）；对话框类留在原地
      if (entry.options.dismissable !== false) close(entry.id);
    }
  }, true);

  let resizeFrame = 0;
  const schedule = () => {
    if (resizeFrame) return;
    resizeFrame = requestAnimationFrame(() => { resizeFrame = 0; stack.forEach(reposition); });
  };
  window.addEventListener('resize', schedule);
  window.addEventListener('scroll', schedule, true);

  window.CelestePopup = {
    open, close, closeAll, isOpen, place, reposition,
    stack, top: topEntry,
    layer: ensureLayer
  };
})();
