import { albumWindow, ARTISTS_PER_FACE, createScene } from './rhine-scene.js';

const sameTrack = (a, b) => a != null && b != null && String(a) === String(b);
const clamp = (n, limit) => Math.max(0, Math.min(n, limit - 1));
const number = n => String(n).padStart(2, '0');
function makeButton(text, attributes = {}) {
  const button = document.createElement('button');
  button.type = 'button';
  button.textContent = text;
  for (const [name, value] of Object.entries(attributes)) button.setAttribute(name, value);
  return button;
}

export function startRoom(data, options) {
  const { artists } = data;
  const opener = document.activeElement;
  const root = document.createElement('section');
  root.className = 'rr-root rr-rhine';
  root.setAttribute('aria-label', '唱片室');
  root.tabIndex = -1;
  root.innerHTML = `
    <header class="rr-archive-head">
      <div><strong>CELESTE</strong><span>唱片室</span></div>
      <nav aria-label="唱片室层级"></nav>
      <button type="button" data-act="exit">退出唱片室</button>
    </header>
    <main class="rr-archive-main">
      <aside class="rr-archive-info">
        <p class="rr-kicker"></p><h1></h1><p class="rr-archive-meta"></p>
        <ol class="rr-browse"></ol>
      </aside>
      <div class="rr-archive-stage" aria-label="三维唱片室">
        <p class="rr-render-state" hidden>三维视图暂不可用，可通过列表继续浏览。</p>
        <button type="button" class="rr-key-hit" aria-label="播放" hidden></button>
      </div>
    </main>
    <footer class="rr-archive-foot">
      <div class="rr-page">
        <button type="button" data-act="prev">上一个</button>
        <span class="rr-counter"></span>
        <button type="button" data-act="next">下一个</button>
      </div>
      <p class="rr-status" role="status"></p>
      <div class="rr-transport" hidden>
        <button type="button" data-act="previous">上一首</button>
        <button type="button" data-act="togglePlay">播放</button>
        <button type="button" data-act="nextTrack">下一首</button>
      </div>
      <button type="button" data-act="open"></button>
    </footer>`;
  const backgrounds = [...document.body.children].filter(element => !element.inert && !['SCRIPT', 'STYLE', 'LINK'].includes(element.tagName));
  for (const element of backgrounds) element.inert = true;
  document.body.append(root);
  root.focus();
  const $ = selector => root.querySelector(selector);
  const action = id => $(`[data-act="${id}"]`);
  const stage = $('.rr-archive-stage');
  const key = $('.rr-key-hit');
  const list = $('.rr-browse');
  const transport = $('.rr-transport');
  const state = { layer: 1, artist: 0, album: 0 };
  const past = [];
  const future = [];
  let playback = { playing: false, trackId: null };
  let loading = false;
  let scene;
  let shownList = '';
  let gesture = null;
  const selectedArtist = () => artists[state.artist];
  const selectedAlbum = () => selectedArtist().albums[state.album];
  const playing = () => playback.playing && selectedAlbum().trackIds.some(id => sameTrack(id, playback.trackId));

  function locateKey(screen) {
    if (state.layer !== 3) return;
    key.style.left = `${(screen.x * .5 + .5) * 100}%`;
    key.style.top = `${(.5 - screen.y * .5) * 100}%`;
    // Map the hit target to the frontal physical button, with a minimum touch size.
    key.hidden = false;
  }
  try { scene = createScene(stage, artists, locateKey); }
  catch (error) {
    console.warn('[music-room] WebGL unavailable', error);
    $('.rr-render-state').hidden = false;
  }

  function updatePlayback() {
    const active = playing();
    const label = active ? '暂停' : '播放';
    key.setAttribute('aria-label', label);
    key.title = label;
    key.disabled = loading;
    action('togglePlay').textContent = label;
    for (const button of transport.querySelectorAll('button')) button.disabled = loading;
    $('.rr-status').textContent = state.layer === 3 ? (loading ? '正在装载光盘' : active ? '正在播放' : '光盘已就位')
      : state.layer === 1 && scene ? '横向拖动转柜，单击抽屉选中' : '';
    scene?.setPlaying(active);
    if (state.layer === 3) {
      Array.from(list.children).forEach((row, i) => { row.classList.toggle('current', sameTrack(selectedAlbum().list[i]?.id ?? selectedAlbum().trackIds[i], playback.trackId)); });
    }
  }

  function updateList() {
    const a = selectedArtist();
    const album = selectedAlbum();
    const signature = [state.layer, state.artist, state.album].join('/');
    if (signature === shownList) return;
    shownList = signature;
    const fragment = document.createDocumentFragment();
    const editions = albumWindow(state.album, a.albums.length);
    const first = state.layer === 1 ? Math.floor(state.artist / ARTISTS_PER_FACE) * ARTISTS_PER_FACE : editions.start;
    const items = state.layer === 1 ? artists.slice(first, first + ARTISTS_PER_FACE)
      : state.layer === 2 ? a.albums.slice(first, editions.end) : album.list;
    items.forEach((entry, i) => {
      const row = document.createElement('li');
      const idx = first + i;
      if (state.layer === 3) {
        const time = Math.max(0, entry.s || 0);
        row.textContent = `${number(i + 1)}  ${entry.t}`;
        const duration = document.createElement('span');
        duration.textContent = `${Math.floor(time / 60)}:${number(time % 60)}`;
        row.append(duration);
      } else {
        const selected = idx === (state.layer === 1 ? state.artist : state.album);
        const button = makeButton(entry.name || entry.title, { 'aria-pressed': String(selected) });
        button.title = entry.name || entry.title;
        button.onclick = () => pick(idx);
        row.append(button);
      }
      fragment.append(row);
    });
    list.replaceChildren(fragment);
  }

  function render() {
    const a = selectedArtist();
    const album = selectedAlbum();
    const total = state.layer === 1 ? artists.length : a.albums.length;
    const index = state.layer === 1 ? state.artist : state.album;
    root.dataset.layer = state.layer;
    $('.rr-kicker').textContent = ['ARTIST ARCHIVE', 'ALBUM EDITIONS', 'OPTICAL TRANSPORT'][state.layer - 1];
    $('h1').textContent = state.layer === 1 ? a.name : album.title;
    $('.rr-archive-meta').textContent = state.layer === 1 ? `${a.albums.length} 张专辑`
      : [a.name, album.year, `${album.tracks} 首`].filter(Boolean).join(' · ');
    $('.rr-counter').textContent = `${number(index + 1)} / ${number(total)}`;
    $('.rr-page').hidden = state.layer === 3;
    action('prev').disabled = index === 0;
    action('next').disabled = index + 1 >= total;
    action('open').hidden = state.layer === 3;
    action('open').textContent = state.layer === 1 ? '打开抽屉' : '放入 CD 机';
    transport.hidden = state.layer !== 3;
    key.hidden = state.layer !== 3 || !scene;
    const titles = ['档案柜', '专辑架', 'CD 机'].slice(0, state.layer);
    $('nav').replaceChildren(...titles.map((title, i) => {
      const button = makeButton(title);
      if (state.layer === i + 1) button.setAttribute('aria-current', 'step');
      else button.onclick = () => go({ ...state, layer: i + 1 }, false);
      return button;
    }));
    updateList();
    updatePlayback();
  }

  function apply(next, animate, play) {
    gesture = null;
    stage.classList.remove('rr-dragging');
    Object.assign(state, next);
    loading = !!scene && state.layer === 3 && play;
    const album = selectedAlbum();
    const onSeated = () => {
      loading = false;
      updatePlayback();
      if (play) options.onPlay(album);
    };
    scene?.show(state, animate && (state.layer < 3 || play), onSeated);
    render();
    if (play && !scene) options.onPlay(album);
  }
  function go(next, play = false) {
    if (next.layer !== state.layer) {
      past.push({ ...state });
      future.length = 0;
      root.focus();
    }
    apply(next, true, play);
  }
  function back() {
    if (!past.length) { options.onClose(); return; }
    future.push({ ...state });
    apply(past.pop(), false, false);
    root.focus();
  }
  function forward() {
    if (!future.length) return;
    past.push({ ...state });
    apply(future.pop(), false, false);
    root.focus();
  }
  function openSelection() {
    if (state.layer === 1) go({ ...state, layer: 2 });
    else if (state.layer === 2) go({ ...state, layer: 3 }, true);
  }
  function pick(index) {
    if (state.layer === 1) {
      go({ layer: 1, artist: index, album: index === state.artist ? state.album : 0 });
    } else if (state.layer === 2) {
      if (index === state.album) openSelection();
      else go({ ...state, album: index });
    }
  }
  function move(delta) {
    if (state.layer === 1) {
      const artist = clamp(state.artist + delta, artists.length);
      if (artist !== state.artist) go({ ...state, artist, album: 0 });
    } else if (state.layer === 2) {
      const album = clamp(state.album + delta, selectedArtist().albums.length);
      if (album !== state.album) go({ ...state, album });
    }
  }
  function control(command) {
    if (state.layer !== 3 || loading) return;
    if (command === 'togglePlay') scene?.press();
    options.onControl(command, selectedAlbum());
  }

  const listeners = new AbortController();
  const listen = (el, event, fn, extra = {}) => el.addEventListener(event, fn, { ...extra, signal: listeners.signal });
  listen(action('exit'), 'click', options.onClose);
  listen(action('open'), 'click', openSelection);
  listen(action('prev'), 'click', () => move(-1));
  listen(action('next'), 'click', () => move(1));
  listen(action('previous'), 'click', () => control('previous'));
  listen(action('nextTrack'), 'click', () => control('next'));
  listen(action('togglePlay'), 'click', () => control('togglePlay'));
  listen(key, 'click', () => control('togglePlay'));
  listen(stage, 'pointerdown', event => {
    if (state.layer !== 1 || !scene || event.button !== 0 || gesture) return;
    gesture = { pointer: event.pointerId, x: event.clientX, y: event.clientY, angle: scene.cabinetAngle(), dragged: false };
    stage.setPointerCapture(event.pointerId);
  });
  listen(stage, 'pointermove', event => {
    if (gesture?.pointer !== event.pointerId) return;
    const dx = event.clientX - gesture.x;
    if (!gesture.dragged && Math.abs(dx) < 5) return;
    gesture.dragged = true;
    stage.classList.add('rr-dragging');
    const quarterWidth = Math.max(105, Math.min(225, stage.clientWidth * .31));
    scene.dragCabinet(gesture.angle, dx / quarterWidth);
  });
  function endGesture(event, cancelled) {
    if (gesture?.pointer !== event.pointerId) return;
    const { dragged, x, y } = gesture;
    gesture = null;
    stage.classList.remove('rr-dragging');
    if (dragged) pick(scene.releaseCabinet());
    else if (!cancelled && Math.hypot(event.clientX - x, event.clientY - y) < 5) {
      const index = scene.pick(event);
      if (index !== undefined) pick(index);
    }
  }
  listen(stage, 'pointerup', event => endGesture(event, false));
  listen(stage, 'pointercancel', event => endGesture(event, true));
  listen(stage, 'lostpointercapture', event => endGesture(event, true));
  listen(stage, 'click', event => {
    if (state.layer !== 2) return;
    const index = scene?.pick(event);
    if (index !== undefined) pick(index);
  });
  listen(stage, 'wheel', event => {
    if (state.layer >= 3 || Math.abs(event.deltaY) < 2) return;
    event.preventDefault();
    move(Math.sign(event.deltaY));
  }, { passive: false });
  listen(window, 'keydown', event => {
    if (event.defaultPrevented || event.ctrlKey || event.altKey || event.metaKey || event.isComposing) return;
    const key = event.key;
    if (['ArrowUp', 'ArrowDown', 'ArrowLeft', 'ArrowRight'].includes(key) && state.layer < 3) move(['ArrowUp', 'ArrowLeft'].includes(key) ? -1 : 1);
    else if (key === 'Escape') back();
    else if (event.target.closest?.('button')) return;
    else if (key === 'Enter' && state.layer < 3) openSelection();
    else if (key === ' ' && state.layer === 3 && !event.repeat) control('togglePlay');
    else return;
    event.preventDefault();
    event.stopImmediatePropagation();
  }, { capture: true });
  apply(state, false, false);
  return {
    back, forward,
    setPlaying(value, trackId) { playback = { playing: value, trackId }; updatePlayback(); },
    destroy() {
      listeners.abort();
      scene?.destroy();
      root.remove();
      for (const element of backgrounds) element.inert = false;
      opener?.focus();
    }
  };
}
