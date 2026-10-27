(() => {
  'use strict';
  const shell = window.CelesteShell, live = typeof window.chrome?.webview?.postMessage === 'function';
  const popup = window.CelestePopup, motion = window.CelesteMotion;
  const selector = '.song-row,.album-track[data-track-id],.web-queue-row';
  const MENU_ID = 'track-context-menu';

  // 多选栏：外壳负责居中，内层负责动画。
  // 两个都写 transform 会互相覆盖（外壳要 translateX(-50%) 居中，动画要 translateY 进出场），
  // 所以拆成两层，各管各的。
  const barHost = document.createElement('div');
  barHost.className = 'track-selection-bar-host';
  const bar = document.createElement('div');
  bar.className = 'track-selection-bar';
  bar.hidden = true;
  barHost.append(bar);
  document.body.append(barHost);

  let selected = new Set(), scope = null, anchor = null, multi = false, epoch = 0, returnFocus = null, busy = false, barVisible = false;
  const key = row => row.dataset.queueIndex === undefined ? row.dataset.trackId || 'demo-' + row.dataset.index : 'q:' + row.dataset.queueRevision + ':' + row.dataset.queueIndex;
  const container = row => row.parentElement.closest('#songs-list,#album-tracks,#artist-songs,#browse-tracks,#web-queue-list,#folder-songs') || row.parentElement;
  const rows = () => scope ? [...scope.querySelectorAll(selector)] : [];
  const chosen = () => rows().filter(row => selected.has(key(row)));

  function paint() {
    rows().forEach(row => { row.classList.toggle('context-selected', selected.has(key(row))); });
    bar.replaceChildren();
    if (!multi) { setBarVisible(false); return; }
    const label = document.createElement('span');
    label.textContent = `已选择 ${chosen().length} 首`;
    const operate = document.createElement('button');
    operate.textContent = '操作所选歌曲';
    operate.disabled = !chosen().length || busy;
    operate.onclick = () => {
      const first = chosen()[0], box = operate.getBoundingClientRect();
      if (first) openTracks(first, box.left, box.top - 12);
    };
    const done = document.createElement('button');
    done.textContent = '完成';
    done.onclick = reset;
    bar.append(label, operate, done);
    setBarVisible(true);
  }

  /** 多选栏进出：只在"隐藏↔显示"的那一刻动，重复选择不会一直闪。 */
  function setBarVisible(visible) {
    if (visible === barVisible) return;
    barVisible = visible;
    if (visible) { bar.hidden = false; motion?.popupEnter(bar); }
    else motion?.popupExit(bar, () => { if (!barVisible) bar.hidden = true; });
  }

  function reset() { selected.clear(); paint(); multi = false; scope = null; anchor = null; bar.hidden = true; barVisible = false; close(); }

  function select(row, event = {}) {
    const parent = container(row);
    if (scope !== parent) { reset(); scope = parent; }
    const list = rows(), id = key(row);
    if (event.shiftKey && anchor && list.includes(anchor)) {
      const a = list.indexOf(anchor), b = list.indexOf(row);
      if (!event.ctrlKey && !event.metaKey) selected.clear();
      list.slice(Math.min(a, b), Math.max(a, b) + 1).forEach(item => selected.add(key(item)));
      multi = true;
    } else if (event.ctrlKey || event.metaKey || multi) {
      selected.has(id) ? selected.delete(id) : selected.add(id);
      multi = true; anchor = row;
    } else { selected.clear(); selected.add(id); anchor = row; }
    paint();
  }

  function close(focus = false, immediate = false) {
    epoch++;
    // 出错时用 immediate：菜单没加载出来就别留在屏幕上淡出 ——
    // 半透明的菜单还挡着点击，看着像"点了没反应"。
    if (popup.isOpen(MENU_ID)) popup.close(MENU_ID, { restoreFocus: false, immediate });
    if (focus && returnFocus?.isConnected) returnFocus.focus({ preventScroll: true });
  }

  /** 新建一个菜单元素并挂到共用浮层：层级、贴边、外部点击、Esc、焦点恢复都归它管。 */
  function showMenu(x, y, fill) {
    const element = document.createElement('div');
    element.id = MENU_ID;
    element.className = 'track-context-menu';
    element.setAttribute('role', 'menu');
    fill(element);
    popup.open({
      id: MENU_ID,
      element,
      anchor: { x, y },
      placement: 'bottom-start',
      gap: 0,
      margin: 8,
      maxHeight: Math.max(220, window.innerHeight - 16),
      exclusive: true,
      onClose: () => { epoch++; }
    });
    element.addEventListener('keydown', onMenuKey);
    element.querySelector('button:not(:disabled)')?.focus({ preventScroll: true });
    return element;
  }

  // 菜单内键盘：上下键在可用项之间跳（跳过禁用项）、Home/End、键入定位。
  // Esc 与 Tab 由共用浮层统一处理，这里不重复。
  function onMenuKey(event) {
    const items = [...event.currentTarget.querySelectorAll('button:not(:disabled)')];
    if (!items.length) return;
    const index = items.indexOf(document.activeElement);
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      items[(index + step + items.length) % items.length]?.focus({ preventScroll: true });
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      items[event.key === 'Home' ? 0 : items.length - 1]?.focus({ preventScroll: true });
    } else if (event.key.length === 1 && !event.ctrlKey && !event.metaKey && !event.altKey) {
      const now = Date.now();
      if (now - (onMenuKey.last || 0) > 700) onMenuKey.buffer = '';
      onMenuKey.last = now;
      onMenuKey.buffer = (onMenuKey.buffer || '') + event.key.toLowerCase();
      const found = items.find(item => item.textContent.trim().toLowerCase().startsWith(onMenuKey.buffer));
      if (found) { event.preventDefault(); found.focus({ preventScroll: true }); }
    }
  }

  function button(target, label, action, disabled = false) {
    const item = document.createElement('button');
    item.type = 'button';
    item.setAttribute('role', 'menuitem');
    item.textContent = label;
    item.disabled = disabled;
    item.onclick = async () => {
      if (busy) return;
      close();
      try { busy = true; await action(); }
      catch (error) { shell.notify(error.message); }
      finally { busy = false; paint(); }
    };
    target.append(item);
    return item;
  }

  function divider(target) { const line = document.createElement('hr'); line.setAttribute('role', 'separator'); target.append(line); }

  function payload(items) {
    const queue = items[0].dataset.queueIndex !== undefined;
    if (queue) return { queue: true, revision: Number(items[0].dataset.queueRevision), entries: items.map(row => ({ index: Number(row.dataset.queueIndex), trackId: row.dataset.trackId })) };
    return { queue: false, trackIds: items.map(row => row.dataset.trackId) };
  }

  async function openTracks(row, x, y) {
    if (busy) return;
    if (scope !== container(row) || !selected.has(key(row))) { reset(); scope = container(row); selected.add(key(row)); anchor = row; paint(); }
    close();                       // 关掉上一个；epoch 也跟着前进
    returnFocus = row;
    const generation = epoch;
    const items = chosen(), snapshot = payload(items), queue = snapshot.queue;

    const invoke = async (operation, extra = {}) => {
      if (!live) { if (operation === 'play') shell.playDemo(Number(items[0].dataset.trackId ?? items[0].dataset.index)); else shell.notify('请在桌面播放器中使用此功能。'); return; }
      await window.CelesteTransport.request('trackAction', { operation, ...snapshot, ...extra });
      document.dispatchEvent(new CustomEvent('celeste:tracksChanged'));
      if (['removeQueue', 'pin', 'delete'].includes(operation)) reset();
    };

    // 先把菜单摆出来（读权限要等宿主回话），别让右键"没反应"
    const element = showMenu(x, y, target => {
      const loading = document.createElement('div');
      loading.className = 'context-loading';
      loading.setAttribute('role', 'status');
      loading.textContent = '正在读取可用操作…';
      target.append(loading);
    });

    let caps = { favorite: false, canDelete: false };
    try { if (live) caps = await window.CelesteTransport.request('trackAction', { operation: 'get', ...snapshot }); }
    catch (error) { if (generation === epoch) { close(false, true); shell.notify(error.message); } return; }
    if (generation !== epoch) return;      // 期间被关掉了：不许把它复活
    element.replaceChildren();

    button(element, '播放', () => invoke('play'), items.length !== 1);
    button(element, '下一首播放', () => invoke('next'));
    if (queue) { button(element, '置顶', () => invoke('pin')); button(element, '从播放队列中删除', () => invoke('removeQueue')); }
    else { button(element, '添加至播放队列', () => invoke('addQueue')); button(element, '添加到播放列表', () => invoke('addPlaylist')); }
    button(element, '多选', () => { multi = true; paint(); row.focus({ preventScroll: true }); });
    divider(element);
    button(element, caps.favorite ? '取消喜欢' : '添加到我喜欢的音乐', () => invoke('favorite', { value: !caps.favorite }));
    const ratings = document.createElement('div');
    ratings.className = 'context-rating';
    ratings.setAttribute('role', 'group');
    ratings.setAttribute('aria-label', '评分');
    element.append(ratings);
    for (let value = 0; value <= 5; value++) button(ratings, value === 0 ? '清除评分' : value + ' 星', () => invoke('rating', { value }));
    divider(element);
    button(element, '标签编辑', () => window.CelesteTags.open(items.map(row => row.dataset.trackId)));
    button(element, '复制歌名与艺术家', () => invoke('copy'));
    button(element, '在文件夹中显示', () => invoke('reveal'), items.length !== 1);
    button(element, '定位到专辑', () => window.CelesteAlbums.openForTrack(snapshot.trackIds?.[0] || snapshot.entries?.[0].trackId), items.length !== 1);
    divider(element);
    button(element, caps.canDelete ? '从磁盘删除（回收站）' : '从磁盘删除（不可用）', () => invoke('delete'), !caps.canDelete);
    element.querySelector('button:not(:disabled)')?.focus({ preventScroll: true });
  }

  function rowFrom(event) { return event.target.closest?.(selector); }

  document.addEventListener('contextmenu', event => {
    const row = rowFrom(event);
    if (!row) return;
    event.preventDefault();
    event.stopPropagation();
    openTracks(row, event.clientX, event.clientY);
  });
  document.addEventListener('click', event => {
    const row = rowFrom(event);
    if (!row || event.target.closest('select,input,.row-tools')) return;
    const selecting = multi || event.ctrlKey || event.metaKey || event.shiftKey;
    select(row, event);
    if (selecting) { event.preventDefault(); event.stopImmediatePropagation(); }
  }, true);
  document.addEventListener('dblclick', event => { if (multi && rowFrom(event)) { event.preventDefault(); event.stopImmediatePropagation(); } }, true);
  document.addEventListener('keydown', event => {
    const row = rowFrom(event);
    if (event.key === 'Escape' && multi && !popup.isOpen(MENU_ID)) { event.preventDefault(); reset(); return; }
    if (!row || event.target.closest('select,input,.row-tools')) return;
    if (event.key === 'ContextMenu' || (event.shiftKey && event.key === 'F10')) {
      event.preventDefault(); event.stopImmediatePropagation();
      const box = row.getBoundingClientRect();
      openTracks(row, box.left + 30, box.top + 20);
    } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
      event.preventDefault(); scope = container(row); multi = true; rows().forEach(item => selected.add(key(item))); paint();
    } else if (event.key === ' ' && multi) {
      event.preventDefault(); event.stopImmediatePropagation(); select(row, { ctrlKey: true });
    }
  }, true);
  document.addEventListener('celeste:navigate', reset);
  document.addEventListener('celeste:library', reset);

  window.CelesteTrackMenu = Object.freeze({
    open: openTracks,
    folder: (event, item, action) => {
      close();
      returnFocus = event.target.closest('[role=treeitem]');
      showMenu(event.clientX, event.clientY, target => {
        button(target, '播放该文件夹内的音频', () => action('play', item));
        button(target, '添加至播放队列', () => action('queue', item));
      });
    },
    playlist: (event, name, action) => {
      close();
      returnFocus = event.target.closest('button');
      showMenu(event.clientX, event.clientY, target => {
        button(target, '设置封面（本地图片…）', () => action('setCover', name));
        button(target, '从网络搜索封面…', () => action('searchCover', name));
        button(target, '恢复默认（首曲封面）', () => action('resetCover', name));
        divider(target);
        button(target, '添加到播放队列', () => action('queuePlaylist', name));
        button(target, '导出（m3u8）', () => action('exportPlaylist', name));
      });
    }
  });
})();
