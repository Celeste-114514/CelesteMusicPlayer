/* CD 架：专辑页的第二种浏览方式（网格 / CD 架）。
   本文件只负责"把专辑摆到架子上"——筛选、排序、打开与播放都由 albums.js 决定；
   这里不持有曲库、不发宿主命令、不缓存封面，也不改专辑 id。

   对外只三件事：render（摆一次）、setPlaying（只改播放标记，不重建）、clear（切回网格时清空）。 */
(() => {
  'use strict';
  const FALLBACK_GAP = 22, FALLBACK_MIN = 152;
  let host = null, records = [], api = {}, playingId = '', focusId = '', columns = 0, timer = 0;

  const cards = () => host ? [...host.querySelectorAll('.cd-case')] : [];
  const openButton = card => card ? card.querySelector('.cd-case-open') : null;

  /** 当前容器能摆几列。间距与最小宽度从 CSS 读，改样式就等于改密度，不在两处各写一份。 */
  function metrics() {
    if (!host) return { width: 0, columns: 2 };
    const style = getComputedStyle(host);
    const gap = parseFloat(style.getPropertyValue('--cd-gap')) || FALLBACK_GAP;
    const min = parseFloat(style.getPropertyValue('--cd-min')) || FALLBACK_MIN;
    const width = host.clientWidth - (parseFloat(style.paddingLeft) || 0) - (parseFloat(style.paddingRight) || 0);
    // 至少两列：窄窗时宁可盒子小一点，也别把架子挤成一条竖线
    return { width, columns: Math.max(2, Math.round((width + gap) / (min + gap))) };
  }

  function caseNode(album) {
    const card = document.createElement('article');
    card.className = 'cd-case';
    card.dataset.albumId = album.id;

    const open = document.createElement('button');
    open.type = 'button';
    open.className = 'cd-case-open';
    open.tabIndex = -1;
    open.setAttribute('aria-label', `打开专辑 ${album.title}，${album.artist}`);
    open.title = `${album.title}\n${album.artist}`;

    const body = document.createElement('span');
    body.className = 'cd-case-body';
    const art = api.artwork ? api.artwork(album) : document.createElement('span');
    art.classList.add('cd-case-art');
    const flag = document.createElement('span');
    flag.className = 'cd-case-flag';
    flag.textContent = '正在播放';
    body.append(art, flag);

    const text = document.createElement('span');
    text.className = 'cd-case-text';
    const title = document.createElement('strong');
    title.className = 'cd-case-title';
    title.textContent = album.title;
    const artist = document.createElement('span');
    artist.className = 'cd-case-artist';
    artist.textContent = album.artist;
    const meta = document.createElement('small');
    meta.className = 'cd-case-meta';
    meta.textContent = [album.year || '', album.trackCount + ' 首'].filter(Boolean).join(' · ');
    text.append(title, artist, meta);
    open.append(body, text);
    open.addEventListener('click', () => api.onOpen?.(album));

    // 播放按钮不进 Tab 链：架子有几十上百张，每张两个 Tab 停靠点会把键盘用户堵死。
    // 键盘走"方向键选盒 → Enter 开专辑 → 详情页播放"，鼠标点这个按钮直接播。
    const play = document.createElement('button');
    play.type = 'button';
    play.className = 'cd-case-play';
    play.tabIndex = -1;
    play.title = '播放这张专辑';
    play.setAttribute('aria-label', `播放专辑 ${album.title}`);
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    const use = document.createElementNS(svg.namespaceURI, 'use');
    use.setAttribute('href', '#i-play');
    svg.append(use);
    play.append(svg);
    play.addEventListener('click', event => { event.stopPropagation(); api.onPlay?.(album); });

    card.append(open, play);
    if (album.id === playingId) {
      card.dataset.playing = 'true';
      open.setAttribute('aria-current', 'true');
    }
    return card;
  }

  /** 搁板的位置要贴着封面下沿，所以按实测封面高度写给每一行 */
  function syncPlank() {
    const sample = host?.querySelector('.cd-case-art');
    if (!sample) return;
    const height = sample.getBoundingClientRect().height;
    if (!height) return;
    host.querySelectorAll('.cd-shelf-row').forEach(row => row.style.setProperty('--cover-h', `${height.toFixed(1)}px`));
  }

  /** 焦点停靠点：整架只有一个 Tab 停靠点，方向键在内部走（标准的 roving tabindex） */
  function applyTabStop(focusNow) {
    const list = cards();
    if (!list.length) return;
    const wanted = list.find(card => card.dataset.albumId === focusId) || list[0];
    list.forEach(card => {
      const open = openButton(card);
      if (open) open.tabIndex = card === wanted ? 0 : -1;
    });
    if (!focusNow) return;
    const open = openButton(wanted);
    open.focus({ preventScroll: true });
    open.scrollIntoView({ block: 'nearest' });
  }

  function build() {
    if (!host) return;
    const { width, columns: next } = metrics();
    columns = next;
    host.replaceChildren();
    if (!records.length || width <= 0) return;   // 隐藏时不摆，显示后会重摆

    const fragment = document.createDocumentFragment();
    for (let start = 0; start < records.length; start += columns) {
      const row = document.createElement('div');
      row.className = 'cd-shelf-row';
      row.style.setProperty('--cols', String(columns));
      const shelf = document.createElement('div');
      shelf.className = 'cd-shelf-cases';
      records.slice(start, start + columns).forEach(album => shelf.append(caseNode(album)));
      row.append(shelf);
      fragment.append(row);
    }
    host.append(fragment);
    syncPlank();
    applyTabStop();
  }

  function schedule() {
    clearTimeout(timer);
    timer = setTimeout(() => {
      if (!host || !host.isConnected || host.hidden) return;
      const { width, columns: next } = metrics();
      if (width <= 0) return;
      // 列数变了要重排；只是窗口宽度变了就挪一下搁板
      if (next !== columns) build(); else syncPlank();
    }, 120);
  }

  function bind() {
    if (host.dataset.cdShelfBound === '1') return;
    host.dataset.cdShelfBound = '1';

    host.addEventListener('focusin', event => {
      const open = event.target.closest?.('.cd-case-open');
      if (!open) return;
      focusId = open.closest('.cd-case').dataset.albumId;
      cards().forEach(card => {
        const item = openButton(card);
        if (item) item.tabIndex = item === open ? 0 : -1;
      });
      api.onFocus?.(focusId);
    });

    host.addEventListener('keydown', event => {
      const open = event.target.closest?.('.cd-case-open');
      if (!open) return;
      const list = cards(), index = list.indexOf(open.closest('.cd-case'));
      const step = { ArrowLeft: -1, ArrowRight: 1, ArrowUp: -columns, ArrowDown: columns }[event.key];
      let next = -1;
      if (step !== undefined) next = index + step;
      else if (event.key === 'Home') next = 0;
      else if (event.key === 'End') next = list.length - 1;
      else if (event.key === 'PageUp') next = index - columns * 2;
      else if (event.key === 'PageDown') next = index + columns * 2;
      else return;
      // 到头就停住，不跨行回绕：架子是二维的，回绕会让人瞬间丢失方位
      if (next < 0 || next >= list.length || next === index) return;
      event.preventDefault();
      const target = openButton(list[next]);
      target.focus({ preventScroll: true });
      target.scrollIntoView({ block: 'nearest' });
    });

    if (typeof ResizeObserver === 'function') new ResizeObserver(schedule).observe(host);
  }

  function render(options) {
    if (!options || !options.host) return;
    if (host !== options.host) { host = options.host; columns = 0; bind(); }
    api = { artwork: options.artwork, onOpen: options.onOpen, onPlay: options.onPlay, onFocus: options.onFocus };
    records = Array.isArray(options.albums) ? options.albums : [];
    playingId = options.playingId || '';
    if (options.focusId !== undefined && options.focusId !== null) focusId = options.focusId;
    build();
    if (options.focusNow) applyTabStop(true);
  }

  /** 只改播放标记：切歌/进度刷新走这里，绝不重建整架 */
  function setPlaying(id) {
    id = id || '';
    if (id === playingId) return;
    playingId = id;
    cards().forEach(card => {
      const on = card.dataset.albumId === playingId, open = openButton(card);
      if (on) { card.dataset.playing = 'true'; open?.setAttribute('aria-current', 'true'); }
      else { delete card.dataset.playing; open?.removeAttribute('aria-current'); }
    });
  }

  function clear() {
    records = [];
    columns = 0;
    if (host) host.replaceChildren();
  }

  window.CelesteCdShelf = Object.freeze({ render, setPlaying, clear });
})();
