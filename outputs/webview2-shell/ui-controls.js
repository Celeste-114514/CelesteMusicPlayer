/* 统一控件实现。
   一个下拉组件负责所有 select：一个模态负责所有确认框。

   下拉的实现原则：**原地增强原生 select**。
   原生 select 仍然是值的唯一来源、仍然占原来的位置和尺寸（表格里的紧凑格、
   设置页的整行都不会因为多插一个按钮而错位），我们只在它外面画统一的外框、
   箭头和焦点圈，并把"点开后的选项层"换成网页自绘的 listbox。
   读屏和 Tab 顺序里只有一个控件，不会出现"看不见的原生控件 + 自绘按钮"两个。 */
(() => {
  'use strict';
  const motion = window.CelesteMotion;
  const popup = window.CelestePopup;
  const SVG_NS = 'http://www.w3.org/2000/svg';

  function icon(pathData, className) {
    const svg = document.createElementNS(SVG_NS, 'svg');
    if (className) svg.setAttribute('class', className);
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS(SVG_NS, 'path');
    path.setAttribute('d', pathData);
    svg.append(path);
    return svg;
  }
  const ARROW = 'm6 9 6 6 6-6';
  const CHECK = 'm5 12 5 5 9-9';

  // —— 下拉组件 ——

  /**
   * 把一个 <select> 接进统一下拉。
   * options: { size:'form'|'compact', variant:'form'|'bare', menuClass, caption,
   *            placement, gap, maxHeight }
   * 返回句柄：{ select, update, open, close, dispose }
   */
  function enhanceSelect(select, options = {}) {
    if (!select || select.multiple || (select.size && select.size > 1)) return null;
    if (select.__uiSelect) {
      // 同一个 select 可能先被自动挂载扫到、后来又被某个模块带着新参数调用
      // （比如排序下拉要带"排列你的音乐"的抬头）。这里把新参数并进去，不重建控件。
      const handle = select.__uiSelect;
      Object.assign(handle.options, options);
      if (options.variant) select.dataset.uiVariant = options.variant;
      if (options.size) select.dataset.uiSize = options.size;
      if (options.extraClass && !select.classList.contains(options.extraClass)) select.classList.add(options.extraClass);
      handle.update();
      return handle;
    }

    const label = select.getAttribute('aria-label') || select.getAttribute('title') || select.name || '';
    const popupId = `select-${select.id || Math.random().toString(36).slice(2, 8)}`;
    select.classList.add('ui-select');
    select.dataset.uiSize = options.size || select.dataset.uiSize || 'form';
    if (options.variant) select.dataset.uiVariant = options.variant;
    if (options.extraClass) select.classList.add(options.extraClass);
    if (label) select.setAttribute('aria-label', label);

    let lastPointer = 0;
    let typeBuffer = '';
    let typeTimer = 0;

    const handle = {
      select,
      popupId,
      options,
      isOpen: () => popup.isOpen(popupId),
      update,
      open: () => openListbox(),
      close: () => closeListbox(true),
      dispose
    };

    function update() {
      // 原生 select 自己显示当前值，不需要镜像文字。
      // 禁用态走 CSS（:disabled），这里只做一次标记，方便自绘层统一判断。
      select.classList.toggle('ui-select-disabled', !!select.disabled);
    }

    function optionItems() {
      return [...select.options].filter(option => !option.disabled);
    }

    function openListbox(focusMode) {
      if (handle.isOpen()) { closeListbox(true); return; }
      const list = document.createElement('div');
      list.className = 'ui-listbox';
      if (options.menuClass) list.classList.add(options.menuClass);
      list.setAttribute('role', 'listbox');
      if (label) list.setAttribute('aria-label', label);
      if (options.caption) {
        const caption = document.createElement('div');
        caption.className = 'ui-listbox-caption';
        caption.textContent = options.caption;
        list.append(caption);
      }
      const items = optionItems().map(option => {
        const item = document.createElement('button');
        item.type = 'button';
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', String(option.selected));
        item.dataset.value = option.value;
        const text = document.createElement('span');
        text.textContent = option.textContent;
        const mark = icon(CHECK, 'ui-option-mark');
        mark.setAttribute('fill', 'none');
        mark.setAttribute('stroke', 'currentColor');
        mark.setAttribute('stroke-width', '2.4');
        mark.setAttribute('stroke-linecap', 'round');
        mark.setAttribute('stroke-linejoin', 'round');
        item.append(text, mark);
        item.addEventListener('click', () => {
          select.value = option.value;
          select.dispatchEvent(new Event('change', { bubbles: true }));
          closeListbox(true);
        });
        list.append(item);
        return item;
      });

      popup.open({
        id: popupId,
        element: list,
        anchor: select,
        placement: options.placement || 'bottom-start',
        gap: options.gap ?? 6,
        maxHeight: options.maxHeight ?? 320,
        exclusive: true,
        onClose: () => {
          select.setAttribute('aria-expanded', 'false');
          select.removeAttribute('aria-controls');
        }
      });
      select.setAttribute('aria-expanded', 'true');
      select.setAttribute('aria-controls', popupId);
      list.style.minWidth = `${Math.max(select.offsetWidth, 180)}px`;

      const selectedIndex = items.findIndex(item => item.getAttribute('aria-selected') === 'true');
      const target = focusMode === 'last' ? items.length - 1
        : focusMode === 'first' ? 0
          : selectedIndex >= 0 ? selectedIndex : 0;
      items[target]?.focus({ preventScroll: true });
      list.addEventListener('keydown', onListKey);
    }

    function onListKey(event) {
      const items = [...event.currentTarget.querySelectorAll('[role=option]')];
      const index = items.indexOf(document.activeElement);
      let next = index;
      if (event.key === 'ArrowDown') next = (index + 1) % items.length;
      else if (event.key === 'ArrowUp') next = (index - 1 + items.length) % items.length;
      else if (event.key === 'Home') next = 0;
      else if (event.key === 'End') next = items.length - 1;
      else if (event.key === 'Escape') {
        event.preventDefault();
        event.stopPropagation();
        closeListbox(true);
        return;
      } else if (event.key === 'Tab') {
        closeListbox(false);
        return;
      } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
        typeBuffer += event.key.toLowerCase();
        clearTimeout(typeTimer);
        typeTimer = setTimeout(() => { typeBuffer = ''; }, 700);
        const found = items.find(item => item.textContent.trim().toLowerCase().startsWith(typeBuffer));
        if (found) { event.preventDefault(); found.focus({ preventScroll: true }); }
        return;
      } else return;
      event.preventDefault();
      items[next]?.focus({ preventScroll: true });
    }

    function closeListbox(restoreFocus) {
      popup.close(popupId, { restoreFocus: false });
      select.setAttribute('aria-expanded', 'false');
      if (restoreFocus) select.focus({ preventScroll: true });
    }

    // 指针：在按下时拦下原生下拉，改开我们的 listbox。
    select.addEventListener('pointerdown', event => {
      if (select.disabled) return;
      lastPointer = Date.now();
      event.preventDefault();
      select.focus({ preventScroll: true });
      if (handle.isOpen()) closeListbox(false); else openListbox();
    });

    // 键盘触发的 click（Enter/空格或程序调用）没有 pointerdown，这里补上。
    select.addEventListener('click', event => {
      if (Date.now() - lastPointer < 700) { event.preventDefault(); return; }
      event.preventDefault();
      if (select.disabled) return;
      select.focus({ preventScroll: true });
      if (handle.isOpen()) closeListbox(true); else openListbox();
    });

    select.addEventListener('keydown', event => {
      if (select.disabled) return;
      if (event.key === 'ArrowDown' || event.key === 'Enter' || event.key === ' ') {
        event.preventDefault();
        openListbox();
      } else if (event.key === 'ArrowUp') {
        event.preventDefault();
        openListbox('last');
      } else if (event.key === 'Home' || event.key === 'End') {
        event.preventDefault();
        openListbox(event.key === 'Home' ? 'first' : 'last');
      } else if (event.key === 'Escape' && handle.isOpen()) {
        event.preventDefault();
        closeListbox(true);
      }
    });

    // 选项变了（宿主刷新设备/字体/来源）要同步禁用态
    const observer = new MutationObserver(() => update());
    observer.observe(select, { childList: true, subtree: true, attributes: true, attributeFilter: ['disabled'] });

    function dispose() {
      observer.disconnect();
      clearTimeout(typeTimer);
      popup.close(popupId, { immediate: true });
      select.classList.remove('ui-select', 'ui-select-disabled');
      delete select.__uiSelect;
    }

    select.__uiSelect = handle;
    update();
    return handle;
  }

  /** 扫描并增强一片区域里的 select。已增强的会跳过。 */
  function scan(root = document) {
    const nodes = [...root.querySelectorAll('select')]
      .filter(select => !select.dataset.nativeSelect && !select.multiple && !(select.size && select.size > 1));
    nodes.forEach(select => {
      if (select.__uiSelect) { select.__uiSelect.update(); return; }
      enhanceSelect(select, {
        extraClass: select.dataset.uiExtraClass || undefined,
        size: select.dataset.uiSize || 'form'
      });
    });
    return nodes.length;
  }

  /** 动态内容用：只增强新出现的 select，绝不替换已有 DOM。 */
  function observe(root) {
    const watcher = new MutationObserver(records => {
      records.forEach(record => {
        record.addedNodes.forEach(node => {
          if (node.nodeType !== 1) return;
          if (node.tagName === 'SELECT') enhanceSelect(node, { size: node.dataset.uiSize || 'form' });
          node.querySelectorAll?.('select').forEach(select => {
            if (!select.__uiSelect) enhanceSelect(select, { size: select.dataset.uiSize || 'form' });
          });
        });
      });
    });
    watcher.observe(root, { childList: true, subtree: true });
    return watcher;
  }

  /** 手动同步一次（值被外部改过、但没触发 change 时用）。 */
  function sync(root = document) {
    const nodes = root.querySelectorAll ? root.querySelectorAll('select') : [];
    nodes.forEach(select => select.__uiSelect?.update());
    if (root instanceof HTMLSelectElement) root.__uiSelect?.update();
  }

  // —— 共享模态 ——

  const modalStack = [];

  function openModal(options = {}) {
    return new Promise(resolve => {
      const scrim = document.createElement('div');
      scrim.className = 'ui-modal-scrim';
      const box = document.createElement('div');
      box.className = 'ui-modal';
      box.setAttribute('role', 'dialog');
      box.setAttribute('aria-modal', 'true');
      if (options.title) {
        const title = document.createElement('h3');
        title.textContent = options.title;
        box.append(title);
      }
      if (options.message) {
        const text = document.createElement('p');
        text.textContent = options.message;
        box.append(text);
      }
      const actions = document.createElement('div');
      actions.className = 'ui-modal-actions';
      const list = options.actions || [{ value: 'ok', label: '好', variant: 'primary' }];
      const buttons = list.map(action => {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = action.variant || 'secondary';
        button.textContent = action.label;
        button.addEventListener('click', () => finish(action.value));
        actions.append(button);
        return button;
      });
      box.append(actions);
      scrim.append(box);
      document.body.append(scrim);
      const entry = { scrim, box, finish };
      modalStack.push(entry);
      const returnFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      (buttons.find(button => button.classList.contains('primary')) || buttons[0])?.focus({ preventScroll: true });
      motion.popupEnter(box);

      function onKey(event) {
        if (modalStack[modalStack.length - 1] !== entry) return;   // 只有最上层响应 Esc
        if (event.key === 'Escape') {
          event.preventDefault();
          event.stopPropagation();
          finish(options.cancelValue ?? 'cancel');
          return;
        }
        if (event.key === 'Tab') {
          const focusable = buttons.filter(button => !button.disabled);
          if (!focusable.length) return;
          const index = focusable.indexOf(document.activeElement);
          event.preventDefault();
          const next = event.shiftKey ? (index - 1 + focusable.length) % focusable.length : (index + 1) % focusable.length;
          focusable[next].focus({ preventScroll: true });
        }
      }
      document.addEventListener('keydown', onKey, true);
      scrim.addEventListener('pointerdown', event => { if (event.target === scrim) finish(options.cancelValue ?? 'cancel'); });

      function finish(value) {
        const index = modalStack.indexOf(entry);
        if (index < 0) return;
        modalStack.splice(index, 1);
        document.removeEventListener('keydown', onKey, true);
        motion.popupExit(scrim, () => {
          scrim.remove();
          if (returnFocus?.isConnected) {
            try { returnFocus.focus({ preventScroll: true }); } catch { /* 触发者没了就算了 */ }
          }
        });
        resolve(value);
      }
    });
  }

  function confirm(options = {}) {
    return openModal({
      title: options.title || '确认操作',
      message: options.message || '',
      actions: [
        { value: false, label: options.cancelText || '取消', variant: 'secondary' },
        { value: true, label: options.confirmText || '确定', variant: options.danger ? 'danger' : 'primary' }
      ],
      cancelValue: false
    }).then(value => value === true);
  }

  /** 页面就绪后统一挂载：静态下拉先扫一遍，之后只盯新出现的下拉。 */
  function autoMount() {
    scan(document);
    observe(document.body);
  }

  window.CelesteControls = {
    enhanceSelect, scan, observe, sync, openModal, confirm, icon, autoMount
  };

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', autoMount, { once: true });
  } else {
    autoMount();
  }
})();
