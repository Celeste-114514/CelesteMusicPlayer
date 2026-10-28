/* 播放队列整页。
   数据只有两个来源：宿主的 queue 命令（顺序、分页、移动）和 celeste.state（当前播放）。
   这里不自己维护"第二份队列"，也不按队列以外的列表去猜下一首。 */
(() => {
  'use strict';
  const $ = id => document.getElementById(id), shell = window.CelesteShell;
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const transport = window.CelesteTransport;
  const PAGE = 200;
  const orders = { Sequential: '顺序播放', Random: '随机播放', ListLoop: '列表循环', TrackLoop: '单曲循环', TrackOnce: '单曲播放' };

  let queue = null, token = 0, loading = false, filter = '', seeking = false;
  let drag = null, moving = false, locateTimer = 0;

  const time = value => {
    const seconds = Math.max(0, Math.round(Number(value) || 0));
    const h = Math.floor(seconds / 3600);
    const m = Math.floor(seconds % 3600 / 60);
    return h > 0 ? `${h}:${String(m).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`
      : `${m}:${String(seconds % 60).padStart(2, '0')}`;
  };
  const longTime = seconds => {
    const minutes = Math.round((Number(seconds) || 0) / 60);
    if (minutes < 60) return `${minutes} 分钟`;
    return `${Math.floor(minutes / 60)} 小时 ${minutes % 60} 分`;
  };
  const element = (tag, text, cls) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (cls) node.className = cls; return node; };

  function request(payload) {
    if (!live || !transport) return Promise.reject(new Error('请在桌面播放器中使用队列页。'));
    return transport.request('queue', payload);
  }

  /* ── 当前 / 下一首 ─────────────────────────────────────────── */

  function paintCover(node, url) {
    if (url) {
      node.style.backgroundImage = `url("${url}")`;
      node.classList.add('has-cover');
    } else {
      node.style.backgroundImage = '';
      node.classList.remove('has-cover');
    }
  }

  function renderNow() {
    const state = shell.getState();
    const playing = !!state.trackId;
    $('queue-now-title').textContent = playing ? (state.title || '未知曲目') : '未在播放';
    $('queue-now-meta').textContent = playing ? [state.artist, state.album].filter(Boolean).join(' · ') : '从下面的队列里挑一首开始';
    const tags = [];
    if (playing && state.quality) tags.push(state.quality);
    if (playing && state.source === 'NETWORK') tags.push('网络来源');
    $('queue-now-quality').textContent = tags.join(' · ');
    paintCover($('queue-now-cover'), playing ? state.coverUrl : '');
    $('queue-now-cover').setAttribute('aria-label', playing ? `打开播放详情：${state.title || ''}` : '打开播放详情');

    const duration = Number(state.duration) || 0;
    $('queue-seek').max = String(duration);
    if (!seeking) $('queue-seek').value = String(Math.min(duration, Number(state.position) || 0));
    $('queue-seek').style.setProperty('--fill', `${duration > 0 ? (Math.min(duration, Number(state.position) || 0) / duration) * 100 : 0}%`);
    $('queue-elapsed').textContent = time(state.position);
    $('queue-duration').textContent = time(duration);
    $('queue-favorite').setAttribute('aria-pressed', String(!!state.favorite));
    $('queue-seek').disabled = !playing;
    $('queue-favorite').disabled = !playing;

    // 顺序按钮不在这一页另挂一套：shell.js 对全站 [data-order] 统一发命令和刷 aria-pressed，
    // 这里再挂一个监听就会一次点击发两条 setPlaybackOrder。
    markCurrent();
  }

  /* 上一首：和"下一首"对称的一块。规则由宿主算（和播放器的上一首按钮同一套），
     这里只负责把结果画出来；点它就是真的去播那一首，不是"在列表里找一下"。 */
  function renderPrev() {
    const prev = queue?.previous || { kind: 'unknown' };
    const cover = $('queue-prev-cover'), card = $('queue-prev-card');
    document.querySelector('.queue-now').classList.toggle('no-prev', prev.kind === 'unknown');
    const clickable = prev.kind === 'track' || prev.kind === 'self';
    card.setAttribute('aria-disabled', String(!clickable));
    if (clickable) {
      $('queue-prev-title').textContent = prev.title || '未知曲目';
      $('queue-prev-meta').textContent = prev.kind === 'self'
        ? '队列里只有这一首'
        : [prev.artist, prev.duration ? time(prev.duration) : ''].filter(Boolean).join(' · ');
      paintCover(cover, prev.coverUrl || '');
      card.dataset.queueIndex = String(prev.index ?? '');
      card.title = '播放上一首';
    } else {
      $('queue-prev-title').textContent = '没有上一首';
      $('queue-prev-meta').textContent = prev.kind === 'none' ? '已经是队列的第一首' : '';
      paintCover(cover, '');
      delete card.dataset.queueIndex;
      card.title = '';
    }
  }

  function renderNext() {
    const next = queue?.next || { kind: 'unknown' };
    const cover = $('queue-next-cover'), card = $('queue-next-card');
    const order = queue?.order || 'ListLoop';
    const self = () => order === 'TrackLoop' ? '正在单曲循环，这首会继续重播' : '队列里只有这一首，播完回到自己';
    // 什么都没在播的时候"下一首"没有意义，整块收掉，别用一句被截断的话占着地方
    document.querySelector('.queue-now').classList.toggle('no-next', next.kind === 'unknown');
    switch (next.kind) {
      case 'track':
      case 'self':
        $('queue-next-title').textContent = next.title || '未知曲目';
        $('queue-next-meta').textContent = next.kind === 'self' ? self()
          : [next.artist, next.duration ? time(next.duration) : ''].filter(Boolean).join(' · ');
        paintCover(cover, next.coverUrl || '');
        card.dataset.queueIndex = String(next.index ?? '');
        card.title = '在队列里定位这一首';
        break;
      case 'random':
        $('queue-next-title').textContent = '随机选择下一首';
        $('queue-next-meta').textContent = '随机模式不预告具体曲目，避免和你真正听到的对不上';
        paintCover(cover, '');
        delete card.dataset.queueIndex;
        card.title = '';
        break;
      case 'none':
        $('queue-next-title').textContent = '没有下一首';
        $('queue-next-meta').textContent = order === 'TrackOnce' ? '单曲播放，这首结束就停' : '队列按顺序播完就停';
        paintCover(cover, '');
        delete card.dataset.queueIndex;
        card.title = '';
        break;
      default:
        $('queue-next-title').textContent = '还没有正在播放的曲目';
        $('queue-next-meta').textContent = '双击下面任意一首开始播放';
        paintCover(cover, '');
        delete card.dataset.queueIndex;
        card.title = '';
    }
  }

  /* ── 列表 ─────────────────────────────────────────────────── */

  function rows() { return [...$('queue-list').querySelectorAll('.queue-row')]; }

  // 状态每 250ms 就来一次，这里只在"播放的曲目真的变了"时才扫一遍行。
  let markedKey = '';
  function markCurrent() {
    const state = shell.getState();
    const key = `${state.trackId}|${queue?.currentIndex}`;
    if (key === markedKey) return;
    markedKey = key;
    rows().forEach(row => {
      const current = queue && Number(row.dataset.queueIndex) === queue.currentIndex
        && row.dataset.trackId === state.trackId;
      row.setAttribute('aria-current', String(!!current));
    });
  }

  function buildRow(track) {
    const row = element('div', undefined, 'queue-row');
    row.dataset.trackId = track.id;
    row.dataset.queueIndex = String(track.index);
    // 右键菜单靠这两个值判断"点的是队列里的第几首"以及"这个顺序是不是已经过期"
    row.dataset.queueRevision = String(queue?.revision ?? '');
    row.tabIndex = 0;
    row.setAttribute('role', 'listitem');
    row.setAttribute('aria-label', `第 ${track.index + 1} 首：${track.title}`);

    const grip = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    grip.setAttribute('class', 'queue-grip');
    grip.setAttribute('aria-hidden', 'true');
    const gripUse = document.createElementNS('http://www.w3.org/2000/svg', 'use');
    gripUse.setAttribute('href', '#i-grip');
    grip.append(gripUse);
    grip.addEventListener('dblclick', event => event.stopPropagation());

    const cover = element('img', undefined, 'queue-cover');
    cover.alt = '';
    cover.loading = 'lazy';
    cover.draggable = false;   // img 默认可拖，不关掉就从封面上拖不出整行
    cover.addEventListener('error', () => cover.removeAttribute('src'), { once: true });
    if (track.coverUrl) cover.src = track.coverUrl;

    const copy = element('span', undefined, 'queue-copy');
    copy.append(element('strong', track.title || '未知曲目'),
      element('small', [track.artist, track.album].filter(Boolean).join(' · ')));

    row.append(grip, element('span', String(track.index + 1), 'queue-index'), cover, copy,
      element('span', track.format || '', 'queue-format'), element('time', time(track.duration), 'queue-time'));

    row.addEventListener('dblclick', () => start(track));
    row.addEventListener('keydown', event => {
      if (event.target !== row) return;
      if (event.key === 'Enter') { event.preventDefault(); start(track); }
      if (!event.altKey) return;
      if (event.key === 'ArrowUp' || event.key === 'ArrowDown') {
        event.preventDefault();
        move(track.index, track.index + (event.key === 'ArrowUp' ? -1 : 1), track.id);
      }
    });
    bindDrag(row, track);
    return row;
  }

  function renderList() {
    const list = $('queue-list');
    list.replaceChildren();
    list.classList.toggle('is-filtered', !!filter);
    markedKey = '';   // 行都重建了，当前行标记要重新算

    const source = queue?.tracks || [];
    const needle = filter.toLocaleLowerCase();
    const visible = needle
      ? source.filter(track => [track.title, track.artist, track.album].some(value => String(value || '').toLocaleLowerCase().includes(needle)))
      : source;

    if (!source.length) {
      const empty = element('div', undefined, 'queue-empty');
      empty.append(element('strong', '播放队列是空的'),
        element('p', '在曲库或播放列表里选“添加到播放队列”，这里就会出现接下来的播放顺序。'));
      list.append(empty);
    } else if (!visible.length) {
      const empty = element('div', undefined, 'queue-empty');
      empty.append(element('strong', '队列里没有匹配的曲目'),
        element('p', '清空搜索框就回到完整队列。搜索只影响这一页看到的内容，不改实际播放顺序。'));
      list.append(empty);
    } else {
      visible.forEach(track => list.append(buildRow(track)));
    }

    $('queue-filter-note').textContent = needle
      ? `匹配 ${visible.length} 首 · 清除搜索后可调整顺序`
      : (queue?.total ? '拖动左侧握柄调整顺序，双击播放' : '');
    $('queue-more').hidden = !!needle || !queue || queue.tracks.length >= queue.total;
    markCurrent();
  }

  function renderSummary() {
    if (!queue) { $('queue-summary').textContent = '正在读取队列…'; return; }
    if (!queue.total) { $('queue-summary').textContent = '0 首'; return; }
    const summary = queue.summary || {};
    const parts = [`共 ${queue.total} 首`, `总时长 ${longTime(summary.totalSeconds)}`];
    if (summary.hasCurrent) parts.push(`之后还有 ${longTime(summary.remainingSeconds)}`);
    $('queue-summary').textContent = parts.join(' · ');
  }

  function render() {
    if (shell.getPage() !== 'queue') return;
    renderSummary();
    renderList();
    renderPrev();
    renderNext();
    renderNow();
  }

  async function load(append = false) {
    const current = ++token;
    loading = true;
    if (!append) $('queue-status').textContent = '正在读取队列…';
    try {
      let result;
      if (live) result = await request({ operation: 'get', offset: append ? queue?.tracks.length || 0 : 0 });
      else result = demoQueue(append ? queue?.tracks.length || 0 : 0);
      if (current !== token) return;
      if (append && queue?.revision !== result.revision) return load();
      queue = append ? { ...result, tracks: [...queue.tracks, ...result.tracks] } : result;
      $('queue-status').textContent = '';
      loading = false;
      render();
    } catch (error) {
      if (current === token) { $('queue-status').textContent = error.message; shell.notify(error.message); }
    } finally {
      if (current === token) loading = false;
    }
  }

  /* 浏览器里没有宿主：拿曲库拼一份假队列，只为把版式看清楚 */
  function demoQueue(offset) {
    const library = shell.getLibrary().map((track, index) => ({
      ...track, index, album: track.album || '', duration: track.duration || 0,
      format: (String(track.id || '').length % 2 ? 'FLAC' : 'WAV'), coverUrl: track.coverUrl || ''
    }));
    const order = shell.getState().playbackOrder || 'ListLoop', last = library.length - 1;
    const noPrev = order === 'Sequential' || order === 'TrackOnce';
    return {
      revision: 0, total: library.length, offset, currentIndex: 0, order,
      summary: { totalSeconds: library.reduce((sum, t) => sum + (t.duration || 0), 0), remainingSeconds: library.slice(1).reduce((sum, t) => sum + (t.duration || 0), 0), hasCurrent: true },
      previous: !library.length ? { kind: 'unknown' } : noPrev ? { kind: 'none' }
        : { kind: last === 0 ? 'self' : 'track', index: last, ...library[last] },
      next: library[1] ? { kind: 'track', index: 1, ...library[1] } : { kind: 'none' },
      tracks: library.slice(offset, offset + PAGE)
    };
  }

  async function start(track) {
    try {
      if (live) await request({ operation: 'play', revision: queue.revision, index: track.index, trackId: track.id });
      else shell.playDemo(Number(track.index) || 0);
      await load();
    } catch (error) { shell.notify(error.message); load(); }
  }

  async function move(from, to, trackId) {
    if (moving || !queue) return;
    if (to < 0 || to >= (queue.total || 0)) return;
    if (from === to) return;
    if (to >= queue.tracks.length) { $('queue-status').textContent = '请先点“加载更多”，再拖到更后面的位置。'; return; }
    moving = true;
    try {
      await request({ operation: 'move', from, to, revision: queue.revision, trackId });
      $('queue-status').textContent = '已调整队列顺序';
      await load();
    } catch (error) {
      $('queue-status').textContent = error.message;
      await load();
    } finally { moving = false; }
  }

  /* ── 拖动排序 ─────────────────────────────────────────────────
     不用浏览器自带的 HTML5 拖放：那是系统的拖放循环，指针一离开列表就变成"禁止"
     光标，落点也不稳。这里自己跟 pointer 事件（鼠标 / 触屏 / 笔同一套），
     行内移动超过 4px 才算开始拖，双击播放和点选都不受影响。 */

  const DRAG_SLOP = 4;

  function bindDrag(row, track) {
    row.addEventListener('pointerdown', event => {
      if (!live || moving || filter || !queue) return;
      if (event.button !== 0) return;
      // 触屏从行身开始拖会和"上下滑列表"打架，只认左侧握柄
      if (event.pointerType === 'touch' && !event.target.closest('.queue-grip')) return;
      drag = {
        row, from: track.index, trackId: track.id, revision: queue.revision,
        x: event.clientX, y: event.clientY, active: false, target: null, ghost: null
      };
      window.addEventListener('pointermove', onDragMove);
      window.addEventListener('pointerup', onDragEnd);
      window.addEventListener('pointercancel', onDragEnd);
    });
  }

  function onDragMove(event) {
    if (!drag) return;
    // 指针在窗口外松开时收不到 pointerup：只要下次移动时按键已全部松开，就当这一拖结束
    if (drag.active && event.buttons === 0) { onDragEnd(); return; }
    if (!drag.active) {
      if (Math.abs(event.clientY - drag.y) < DRAG_SLOP && Math.abs(event.clientX - drag.x) < DRAG_SLOP) return;
      beginDrag();
    }
    event.preventDefault();
    drag.x = event.clientX;
    drag.y = event.clientY;
    paintGhost();
    paintTarget(event.clientY);
    autoScroll(event.clientY);
  }

  function beginDrag() {
    const row = drag.row, box = row.getBoundingClientRect();
    drag.active = true;
    drag.height = box.height;
    document.body.classList.add('is-reordering');
    document.getSelection()?.removeAllRanges();
    row.classList.add('is-dragging');

    const ghost = element('div', undefined, 'queue-ghost');
    const art = element('span', undefined, 'queue-ghost-art');
    const source = row.querySelector('.queue-cover');
    if (source?.getAttribute('src')) art.style.backgroundImage = `url("${source.getAttribute('src')}")`;
    const copy = element('span', undefined, 'queue-ghost-copy');
    copy.append(element('strong', row.querySelector('.queue-copy strong')?.textContent || '未知曲目'),
      element('small', row.querySelector('.queue-copy small')?.textContent || ''));
    ghost.append(art, copy);
    ghost.style.width = `${Math.min(box.width, 460)}px`;
    document.body.append(ghost);
    drag.ghost = ghost;
    paintGhost();
  }

  // 浮层跟手，但偏到指针右下方 14px：正好压在指针底下会盖住落点横线，什么也看不出来
  function paintGhost() {
    if (!drag?.ghost) return;
    const width = drag.ghost.offsetWidth || 320, height = drag.ghost.offsetHeight || drag.height;
    const left = Math.min(Math.max(drag.x + 14, 8), window.innerWidth - width - 8);
    const top = Math.min(Math.max(drag.y + 14, 8), window.innerHeight - height - 8);
    drag.ghost.style.left = `${left}px`;
    drag.ghost.style.top = `${top}px`;
  }

  /* 落点：指针压在谁的上半就是"插到它前面"，压在最后一行下半就是"插到队尾"。
     拖动行自己不算落点 —— 否则在它自己身上晃也会高亮。 */
  function paintTarget(y) {
    const others = rows().filter(item => item !== drag.row);
    let hit = null;
    for (const item of others) {
      const box = item.getBoundingClientRect();
      if (y < box.top + box.height / 2) { hit = { row: item, after: false }; break; }
    }
    if (!hit && others.length) hit = { row: others.at(-1), after: true };
    rows().forEach(item => item.classList.remove('drop-before', 'drop-after'));
    if (!hit) { drag.target = null; return; }
    hit.row.classList.add(hit.after ? 'drop-after' : 'drop-before');
    let to = Number(hit.row.dataset.queueIndex) + (hit.after ? 1 : 0);
    if (drag.from < to) to -= 1;
    drag.target = to;
  }

  function autoScroll(y) {
    const main = document.querySelector('.main');
    if (!main) return;
    const area = main.getBoundingClientRect();
    if (y < area.top + 56) main.scrollTop -= 16;
    else if (y > area.bottom - 56) main.scrollTop += 16;
  }

  function onDragEnd() {
    const state = drag;
    endDrag();
    // 只是点了一下（没越过阈值）或者没挪位置：什么都不发
    if (!state?.active || state.target === null || state.target === state.from) return;
    move(state.from, state.target, state.trackId);
  }

  function endDrag() {
    drag?.ghost?.remove();
    if (drag?.row) drag.row.classList.remove('is-dragging');
    rows().forEach(row => row.classList.remove('drop-before', 'drop-after'));
    document.body.classList.remove('is-reordering');
    window.removeEventListener('pointermove', onDragMove);
    window.removeEventListener('pointerup', onDragEnd);
    window.removeEventListener('pointercancel', onDragEnd);
    drag = null;
  }

  /* ── 定位当前 ───────────────────────────────────────────────── */

  function locate() {
    if (!queue || queue.currentIndex < 0) { shell.notify('现在没有正在播放的队列曲目。'); return; }
    const row = rows().find(item => Number(item.dataset.queueIndex) === queue.currentIndex);
    if (!row) {
      shell.notify(queue.tracks.length < queue.total ? '当前曲目还没加载出来，先点“加载更多”。' : '当前曲目不在队列里。');
      return;
    }
    if (filter) { $('queue-search').value = ''; filter = ''; renderList(); }
    const target = rows().find(item => Number(item.dataset.queueIndex) === queue.currentIndex) || row;
    target.scrollIntoView({ block: 'center', behavior: 'smooth' });
    target.classList.add('is-located');
    clearTimeout(locateTimer);
    locateTimer = setTimeout(() => target.classList.remove('is-located'), 1400);
  }

  /* ── 事件 ─────────────────────────────────────────────────── */

  function applyFilter(value) {
    filter = String(value || '').trim();
    if (!queue) return;
    renderList();
  }

  $('queue-search').addEventListener('input', event => {
    $('search').value = event.target.value;
    applyFilter(event.target.value);
  });
  $('search').addEventListener('input', event => {
    if (shell.getPage() !== 'queue') return;
    $('queue-search').value = event.target.value;
    applyFilter(event.target.value);
  });
  $('queue-more').addEventListener('click', () => load(true));
  $('queue-locate').addEventListener('click', locate);
  $('queue-prev-card').addEventListener('click', () => {
    const prev = queue?.previous;
    if (!prev || (prev.kind !== 'track' && prev.kind !== 'self')) return;
    if (!live) { shell.playDemo(Number(prev.index) || 0); return; }
    shell.send('previous');
    // 宿主换歌后当前行会跟着变：稍等一下再拉一次队列，别让"正在播放"那行标错一整拍
    setTimeout(() => { if (shell.getPage() === 'queue') load(); }, 500);
  });
  $('queue-next-card').addEventListener('click', () => {
    const index = Number($('queue-next-card').dataset.queueIndex);
    if (!Number.isFinite(index)) return;
    const row = rows().find(item => Number(item.dataset.queueIndex) === index);
    if (row) { row.scrollIntoView({ block: 'center', behavior: 'smooth' }); row.classList.add('is-located'); setTimeout(() => row.classList.remove('is-located'), 1400); }
  });
  $('queue-now-cover').addEventListener('click', () => window.CelesteNowPlaying?.open());
  $('queue-open-now').addEventListener('click', () => window.CelesteNowPlaying?.open());
  $('queue-favorite').addEventListener('click', () => { if (live) shell.send('favorite'); });
  $('queue-seek').addEventListener('input', event => {
    seeking = true;
    const value = Number(event.target.value);
    event.target.style.setProperty('--fill', `${Number(event.target.max) > 0 ? value / Number(event.target.max) * 100 : 0}%`);
    $('queue-elapsed').textContent = time(value);
  });
  $('queue-seek').addEventListener('change', event => {
    const seconds = Number(event.target.value);
    seeking = false;
    if (live) shell.send('seek', { seconds });
  });
  document.addEventListener('celeste:navigate', event => {
    if (event.detail !== 'queue') { token++; return; }
    // 顶部全局搜索框的内容带进这一页，两个输入框保持一致（搜索只影响显示，不改队列）
    $('queue-search').value = $('search').value;
    filter = String($('search').value || '').trim();
    load();
  });
  document.addEventListener('celeste:message', event => {
    if (event.detail?.kind !== 'celeste.state') return;
    if (shell.getPage() !== 'queue') return;
    renderNow();
  });
  document.addEventListener('celeste:library', () => { if (shell.getPage() === 'queue' && !live) load(); });

  // 队列被别处改动（加入队列、播完一首）时按修订号刷新；没有变化就只更新当前行。
  setInterval(async () => {
    if (!live || loading || moving || drag || document.hidden || shell.getPage() !== 'queue') return;
    const current = token;
    try {
      const result = await request({ operation: 'get', offset: 0 });
      if (current !== token || shell.getPage() !== 'queue') return;
      if (!queue || result.revision !== queue.revision) { queue = result; render(); }
      else { queue.currentIndex = result.currentIndex; queue.next = result.next; queue.previous = result.previous; queue.order = result.order; renderPrev(); renderNext(); markCurrent(); }
    } catch { }
  }, 5000);
})();
