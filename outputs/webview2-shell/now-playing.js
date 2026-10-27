/* 正在播放详情页。入口只有一个：底栏左侧封面。
   页面自身不持有播放状态 —— 全部从 CelesteShell.getState() 读，动作走 CelesteShell.send()，
   所以它和底栏永远是同一份状态，不存在两套进度。 */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const shell = window.CelesteShell;
  const motion = () => window.CelesteMotion;
  const live = typeof window.chrome?.webview?.postMessage === 'function';

  const page = $('now-playing');
  if (!page || !shell) return;

  const openButton = $('now-playing-open');
  const closeButton = $('np-close');
  const coverBox = $('np-cover');
  const titleEl = $('np-title');
  const artistEl = $('np-artist');
  const metaEl = $('np-meta');
  const progress = $('np-progress');
  const elapsed = $('np-elapsed');
  const duration = $('np-duration');
  const playButton = $('np-play');
  const previousButton = $('np-previous');
  const nextButton = $('np-next');
  const favoriteButton = $('np-favorite');
  const lyricBox = $('np-lyrics');
  const pinButton = $('np-lyrics-pin');
  const statusEl = $('np-status');

  let open = false;
  let ticker = 0;
  let lyricLines = [];
  let lyricTrackId = '';
  let lyricState = 'idle';        // idle | loading | ready | none | offline
  let currentIndex = -1;
  let userScrolled = false;
  let pollBusy = false;
  let openGeneration = 0;
  let seekDirty = false;
  let lastTrackId = null;
  let coverGhost = null;

  const format = seconds => {
    seconds = Math.max(0, Math.floor(Number(seconds) || 0));
    return Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0');
  };

  // —— 封面 ——
  function renderCover(url) {
    if (coverBox.dataset.url === (url || '')) return;
    coverBox.dataset.url = url || '';
    coverBox.replaceChildren();
    coverBox.classList.toggle('is-empty', !url);
    if (!url) {
      const mark = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
      const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
      use.setAttribute('href', '#i-note');
      mark.append(use);
      coverBox.append(mark);
      return;
    }
    const image = new Image();
    image.alt = '';
    image.decoding = 'async';
    image.src = url;
    image.addEventListener('error', () => { if (coverBox.dataset.url === url) coverBox.replaceChildren(); }, { once: true });
    coverBox.append(image);
    image.addEventListener('load', () => {
      if (open && coverBox.dataset.url === url) motion()?.fadeIn(coverBox, 'cover');
    }, { once: true });
  }

  // —— 顶部信息与进度 ——
  function render() {
    const state = shell.getState();
    titleEl.textContent = state.title || '没有正在播放的歌曲';
    artistEl.textContent = [state.artist, state.album].filter(Boolean).join(' · ') || '—';
    const quality = $('track-quality')?.textContent || '';
    metaEl.textContent = quality && !/等待播放/.test(quality) ? quality : '';
    metaEl.hidden = !metaEl.textContent;

    const coverUrl = state.coverUrl ? new URL(state.coverUrl, location.href) : null;
    if (coverUrl?.hostname === 'celeste-cover.internal') coverUrl.searchParams.set('size', '768');
    renderCover(coverUrl?.href || '');
    duration.textContent = format(state.duration);
    if (!seekDirty) {
      elapsed.textContent = format(state.position);
      progress.max = String(Math.max(1, state.duration || 1));
      progress.value = String(Math.min(state.position || 0, state.duration || 0));
      progress.style.setProperty('--fill', (state.duration ? (state.position / state.duration) * 100 : 0) + '%');
    }
    playButton.setAttribute('aria-pressed', String(!!state.playing));
    playButton.setAttribute('aria-label', state.playing ? '暂停' : '播放');
    playButton.querySelector('use').setAttribute('href', state.playing ? '#i-pause' : '#i-play');
    favoriteButton.setAttribute('aria-pressed', String(!!state.favorite));
    previousButton.disabled = nextButton.disabled = false;

    if (state.trackId !== lastTrackId) {
      lastTrackId = state.trackId || '';
      // 换歌：先把旧歌词清掉，别让上一首的词停在新歌下面
      if (open) setLyrics([], 'loading');
      if (open) loadLyrics(lastTrackId, 0);
    }
    if (lyricState === 'ready') highlight(state.position || 0);
  }

  // —— 歌词 ——
  function setLyrics(lines, nextState) {
    lyricLines = Array.isArray(lines) ? lines : [];
    lyricState = nextState || (lyricLines.length ? 'ready' : 'none');
    currentIndex = -1;
    userScrolled = false;
    pinButton.hidden = true;
    lyricBox.replaceChildren();
    if (lyricState === 'loading') {
      lyricBox.append(placeholder('正在读取歌词…'));
      return;
    }
    renderLyrics();
    if (open) motion()?.reveal(lyricBox, 'cover', 8);
  }

  function renderLyrics() {
    if (lyricLines.length) {
      lyricLines.forEach((line, index) => {
        const row = document.createElement('p');
        row.className = 'np-lyric' + (line.tr ? ' is-translation' : '');
        row.dataset.time = String(line.t);
        row.dataset.index = String(index);
        row.textContent = line.text || ' ';
        // 有时间才能点着跳；纯文本歌词只展示，不装作能定位
        if (line.t >= 0 && shell.getState().duration > 0) {
          row.addEventListener('click', () => {
            if (window.getSelection()?.toString()) return;
            seek(line.t);
          });
          row.classList.add('is-seekable');
          row.setAttribute('role', 'button');
          row.tabIndex = 0;
          row.addEventListener('keydown', event => {
            if (event.key === 'Enter' || event.key === ' ') { event.preventDefault(); seek(line.t); }
          });
        }
        lyricBox.append(row);
      });
      highlight(shell.getState().position || 0, true);
      return true;
    }
    lyricBox.append(placeholder(lyricState === 'offline'
      ? '浏览器预览里读不到歌词，启动桌面播放器后会显示。'
      : '这首歌没有找到歌词。'));
    return true;
  }

  function placeholder(text) {
    const node = document.createElement('p');
    node.className = 'np-lyric-empty';
    node.textContent = text;
    return node;
  }

  function highlight(position, force) {
    let index = -1;
    for (let i = 0; i < lyricLines.length; i += 1) {
      if (lyricLines[i].t <= position + 0.12) index = i; else break;
    }
    if (index === currentIndex && !force) return;
    currentIndex = index;
    const rows = lyricBox.children;
    for (let i = 0; i < rows.length; i += 1) rows[i].classList.toggle('is-current', i === index);
    if (index >= 0 && !userScrolled) scrollToRow(rows[index], false);
  }

  function scrollToRow(row, smooth = true) {
    if (!row) return;
    const top = row.offsetTop - lyricBox.clientHeight / 2 + row.offsetHeight / 2;
    lyricBox.scrollTo({ top: Math.max(0, top), behavior: smooth && motion()?.canMove() ? 'smooth' : 'auto' });
  }

  function markUserScroll() {
    // 用户自己在翻：别把他拽回当前句，但要给一个出口
    userScrolled = true;
    if (pinButton.hidden) {
      pinButton.hidden = false;
      motion()?.fadeIn(pinButton, 'control');
    }
  }

  lyricBox.addEventListener('wheel', markUserScroll, { passive: true });
  lyricBox.addEventListener('touchmove', markUserScroll, { passive: true });
  lyricBox.addEventListener('keydown', event => {
    if (['ArrowUp', 'ArrowDown', 'PageUp', 'PageDown', 'Home', 'End'].includes(event.key)) markUserScroll();
  });

  pinButton.addEventListener('click', () => {
    userScrolled = false;
    pinButton.hidden = true;
    scrollToRow(lyricBox.children[currentIndex], true);
  });

  // —— 从宿主取歌词（一次取整首，进度在前端算，不每帧跨宿主）——
  async function loadLyrics(trackId, attempt) {
    if (!live) {
      lyricState = 'offline';
      setLyrics([], 'offline');
      return;
    }
    if (!trackId) { setLyrics([], 'none'); return; }
    const generation = openGeneration;
    try {
      const data = await window.CelesteTransport.request('nowPlaying', {});
      if (generation !== openGeneration || !open) return;
      // 换歌了：旧回复不许盖到新歌上
      if (shell.getState().trackId !== trackId) return;
      if (data && data.trackId && data.trackId !== trackId) {
        if (attempt < 8) setTimeout(() => { if (open && openGeneration === generation) loadLyrics(trackId, attempt + 1); }, 600);
        return;
      }
      if (data && Array.isArray(data.lines) && data.lines.length) {
        lyricTrackId = trackId;
        setLyrics(data.lines, 'ready');
        return;
      }
      // 歌词是异步解析的：刚换歌时可能还没好，稍等再问两次
      if (attempt < 3) { setTimeout(() => { if (open && openGeneration === generation) loadLyrics(trackId, attempt + 1); }, 600); return; }
      lyricTrackId = trackId;
      setLyrics([], 'none');
    } catch {
      if (generation !== openGeneration) return;
      setLyrics([], 'none');
    }
  }

  // —— 动作 ——
  const send = (command, payload = {}) => shell.send(command, payload);
  function seek(seconds) {
    seekDirty = false;
    send('seek', { seconds });
    elapsed.textContent = format(seconds);
    progress.value = String(seconds);
    highlight(seconds, true);
  }
  playButton.addEventListener('click', () => send('togglePlay'));
  previousButton.addEventListener('click', () => send('previous'));
  nextButton.addEventListener('click', () => send('next'));
  favoriteButton.addEventListener('click', () => send('favorite'));
  progress.addEventListener('input', () => {
    seekDirty = true;
    const value = Number(progress.value);
    elapsed.textContent = format(value);
    progress.style.setProperty('--fill', (progress.max > 0 ? (value / Number(progress.max)) * 100 : 0) + '%');
  });
  progress.addEventListener('change', () => seek(Number(progress.value)));

  // —— 打开 / 关闭 ——
  function openPage() {
    if (open) return;
    open = true;
    openGeneration += 1;
    const state = shell.getState();
    lastTrackId = state.trackId || '';
    const sourceRect = document.querySelector('.mini-cover')?.getBoundingClientRect();
    page.hidden = false;
    document.body.classList.add('now-playing-open');
    render();
    if (lastTrackId) loadLyrics(lastTrackId, 0); else setLyrics([], 'none');
    // 进场动画：页面淡入，封面从底栏飞过来
    page.getAnimations().forEach(animation => animation.cancel());
    flyCover(coverBox.dataset.url, false, sourceRect);
    motion()?.reveal(page, 'cover', 18);
    motion()?.reveal(document.querySelector('.np-left'), 'cover', 10);
    ticker = setInterval(() => {
      if (document.hidden) return;
      if (pollBusy) return;
      pollBusy = true;
      try { render(); } finally { pollBusy = false; }
    }, 250);
    closeButton.focus({ preventScroll: true });
  }

  function closePage(returnFocus = true) {
    if (!open) return;
    open = false;
    const generation = ++openGeneration;
    clearInterval(ticker);
    ticker = 0;
    document.body.classList.remove('now-playing-open');
    flyCover(coverBox.dataset.url, true);
    const finish = () => {
      if (open || generation !== openGeneration) return;
      page.hidden = true;
      lyricBox.replaceChildren();
      lyricLines = [];
      currentIndex = -1;
      userScrolled = false;
    };
    if (motion()?.canFade()) {
      const distance = motion().shift(18, 'cover');
      motion().animate(page, [{ opacity: 1, transform: 'translateY(0)' }, { opacity: 0, transform: `translateY(${distance}px)` }],
        { duration: motion().duration(180, 'cover'), easing: motion().easings.exit, fill: 'none' }).finished.then(finish);
    } else finish();
    if (returnFocus) openButton?.focus({ preventScroll: true });
  }

  /** 封面从底栏飞到详情页（反向同理）。减少动画时直接跳过，退回淡变。 */
  function flyCover(url, reverse, sourceRect) {
    const small = document.querySelector('.mini-cover');
    coverGhost?.remove();
    coverGhost = null;
    if (!small || !url || !motion()?.canMove() || motion()?.getOff().includes('cover')) return;
    const from = sourceRect || (reverse ? coverBox : small).getBoundingClientRect();
    const to = (reverse ? small : coverBox).getBoundingClientRect();
    if (!from.width || !to.width) return;
    const ghost = document.createElement('img');
    ghost.className = 'np-cover-ghost';
    coverGhost = ghost;
    ghost.alt = '';
    ghost.src = url;
    ghost.style.left = from.left + 'px';
    ghost.style.top = from.top + 'px';
    ghost.style.width = from.width + 'px';
    ghost.style.height = from.height + 'px';
    document.body.append(ghost);
    const dx = (to.left + to.width / 2) - (from.left + from.width / 2);
    const dy = (to.top + to.height / 2) - (from.top + from.height / 2);
    const scale = to.width / from.width;
    const duration = motion()?.duration(300, 'cover') ?? 300;
    motion()?.animate(ghost, [
      { transform: 'translate(0, 0) scale(1)' },
      { transform: `translate(${dx}px, ${dy}px) scale(${scale})` }
    ], { duration, easing: motion()?.easings.continuity }).finished.then(() => ghost.remove());
  }

  // 宿主的每次状态推送都要立刻反映到页面上（换歌尤其不能等 250ms 的轮询）
  document.addEventListener('celeste:state', () => { if (open) render(); });

  openButton?.addEventListener('click', openPage);
  closeButton?.addEventListener('click', () => closePage());
  // Esc 收起。挂在 document 上而不是页面上：页面里的按钮被藏起来时焦点会掉回 body，
  // 那时键盘事件到不了 section。先让上一层（菜单/浮层）处理，它 preventDefault 了我们就不抢。
  document.addEventListener('keydown', event => {
    if (!open || event.defaultPrevented) return;
    if (event.key === 'Escape') { event.preventDefault(); closePage(); }
  });
  // 点页面空白处不关（避免误触）；只认 Esc 和固定按钮

  window.CelesteNowPlaying = Object.freeze({
    open: openPage,
    close: closePage,
    isOpen: () => open,
    getLyrics: () => lyricLines.slice(),
    getLyricState: () => lyricState
  });
})();
