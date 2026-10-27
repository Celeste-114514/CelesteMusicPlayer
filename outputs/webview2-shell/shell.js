/* Shell preview. Audio remains in C#. This v1 protocol requires a host adapter;
   it deliberately does not pretend to support the removed legacy WebHost. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const webview = window.chrome?.webview;
  const live = typeof webview?.postMessage === 'function';
  const pages = {
    overview: ['主页', 'home', ''],
    songs: ['歌曲', 'note', '每一首，都有自己的位置。'],
    albums: ['专辑', 'disc', '以专辑为单位，听完整个故事。'],
    artists: ['艺术家', 'person', '沿着熟悉的声音，发现更多。'],
    favorites: ['喜欢的音乐', 'heart', '把喜欢的声音，留在身边。'],
    recent: ['最近播放', 'clock', '重逢那些刚刚打动你的旋律。'],
    playlists: ['播放列表', 'list', '为每一种心情，收集一段旋律。'],
    library: ['媒体库', 'folder', '管理你的音乐来源。'],
    folders: ['文件夹', 'folder', '沿着熟悉的目录，找到你的音乐。'],
    queue: ['播放队列', 'queue', '让喜欢的旋律，接着发生。'],
    albumArtists: ['专辑艺人', 'people', '从创作者出发，重新认识一张专辑。'],
    ratings: ['评分', 'star', '那些值得留下记号的音乐。'],
    mostPlayed: ['播放最多', 'wave', '时间替你留下的偏爱。'],
    tags: ['标签', 'tag', '整理音乐里的每一个细节。'],
    dsp: ['音效处理', 'wave', '为声音找到适合你的表达。'],
    settings: ['设置', 'settings', '让这个音乐空间，更像你。']
  };
  const samples = [
    { title: 'Priestess', artist: 'Periphery', album: 'Juggernaut: Omega', duration: 303, quality: '16bit/44.1kHz', bitrate: '989kbps', art: 'teal', sleeve: 'JUGGERNAUT\nOMEGA' },
    { title: 'Graveless', artist: 'Periphery', album: 'Juggernaut: Omega', duration: 235, quality: '16bit/44.1kHz', bitrate: '1068kbps', art: 'teal', sleeve: 'JUGGERNAUT\nOMEGA' },
    { title: 'Omega', artist: 'Periphery', album: 'Juggernaut: Omega', duration: 704, quality: '16bit/44.1kHz', bitrate: '996kbps', art: 'teal', sleeve: 'JUGGERNAUT\nOMEGA' },
    { title: '"Slut!" (Taylor’s Version) [From The Vault]', artist: 'Taylor Swift', album: '1989 (Taylor’s Version) [Deluxe]', duration: 180, quality: '24bit/48kHz', bitrate: '1679kbps', art: 'ocean', sleeve: '1989\nTAYLOR’S VERSION' },
    { title: '"SMTⅤV" hallucination', artist: 'アトラスサウンドチーム / ATLUS GAME MUSIC', album: '真・女神転生Ⅴ Vengeance オリジナル・サウンドトラック', duration: 92, quality: '16bit/44.1kHz', bitrate: '799kbps', art: 'crimson', sleeve: 'VENGEANCE\nORIGINAL SOUNDTRACK' },
    { title: '"Vengeance" end theme', artist: 'アトラスサウンドチーム / ATLUS GAME MUSIC', album: '真・女神転生Ⅴ Vengeance オリジナル・サウンドトラック', duration: 371, quality: '16bit/44.1kHz', bitrate: '932kbps', art: 'crimson', sleeve: 'VENGEANCE\nORIGINAL SOUNDTRACK' },
    { title: '#Love (feat. Ann & Gaku)', artist: 'supercell', album: '#Love (feat. Ann & Gaku) - EP', duration: 262, quality: '24bit/96kHz', bitrate: '2975kbps', art: 'gold', sleeve: '#LOVE\nSUPERCELL' },
    { title: '#Love -instrumental- (feat. Ann & Gaku)', artist: 'supercell', album: '#Love (feat. Ann & Gaku) - EP', duration: 261, quality: '24bit/96kHz', bitrate: '2820kbps', art: 'gold', sleeve: '#LOVE\nSUPERCELL' },
    { title: '’tis the damn season', artist: 'Taylor Swift', album: 'evermore (Deluxe)', duration: 229, quality: '24bit/44.1kHz', bitrate: '1478kbps', art: 'forest', sleeve: 'EVERMORE\nTAYLOR SWIFT' },
    { title: '(Do)Minion', artist: 'Eluveitie', album: 'Everything Remains (As It Never Was)', duration: 307, quality: '16bit/44.1kHz', bitrate: '1021kbps', art: 'stone', sleeve: 'ELUVEITIE\nEVERYTHING REMAINS' },
    { title: '…Ready For It?', artist: 'Taylor Swift', album: 'reputation', duration: 208, quality: '16bit/44.1kHz', bitrate: '984kbps', art: 'silver', sleeve: 'REPUTATION\nTAYLOR SWIFT' },
    { title: '0℃の日曜', artist: 'TOSHIKI HAYASHI (%C)', album: 'CITY HIP POP MIX', duration: 172, codec: 'MPEG 1 Layer III', quality: '16bit/44.1kHz', bitrate: '320kbps', art: 'blue', sleeve: 'CITY HIP POP\nMIX' },
    { title: '107', artist: '黒木渚', album: '自由律', duration: 432, quality: '16bit/44.1kHz', bitrate: '807kbps', art: 'rose', sleeve: '自由律\n黒木渚' }
  ];
  let state = { ...samples[0], position: 0, playing: false, volume: 65, index: 0, favorite: false, shuffle: false, repeat: false, playbackOrder:'ListLoop', desktopLyrics:false };
  const orders={Sequential:'顺序播放',Random:'随机播放',ListLoop:'列表循环',TrackLoop:'单曲循环',TrackOnce:'单曲播放'};
  let coverGeneration=0;
  async function updateCover(url) {
    const cover=document.querySelector('.mini-cover');
    if(cover.dataset.url===url)return;
    const generation=++coverGeneration;
    cover.dataset.url=url;
    cover.className='mini-cover live-cover';
    cover.querySelector('span')?.remove();
    if(!url){cover.replaceChildren();return;}
    const image=new Image();image.alt='';image.decoding='async';image.src=url;
    try { await image.decode(); }
    catch { if(generation===coverGeneration)cover.replaceChildren();return; }
    if(generation!==coverGeneration)return;
    const previous=[...cover.children];
    cover.append(image);
    // Commit decoded pixels before starting the crossfade. Ignore late arrivals
    // from a previous song when the listener skips several tracks quickly.
    requestAnimationFrame(()=>requestAnimationFrame(()=>{
      if(generation!==coverGeneration){image.remove();return;}
      image.classList.add('cover-current');
      // The old layer stays opaque beneath the arriving image until it is covered.
      const hold = (window.CelesteMotion?.duration(360, 'cover') ?? 360) + 90;
      setTimeout(()=>previous.forEach(old=>old.remove()),hold);
    }));
  }
  let selectedSong = null, songQuery = '', sortAscending = true;
  let libraryTracks = [], libraryAlbums = [], pendingTracks = [], libraryRevision = -1, libraryLoading = live, libraryTotal = 0, visibleLimit = 200, rowStamp = '';
  const metadataCache = new Map(), metadataRequested = new Set();
  const metadataObserver = live ? new IntersectionObserver(entries => {
    const ids=[];
    entries.forEach(entry=>{const id=entry.target.dataset.trackId;if(entry.isIntersecting&&id&&!metadataRequested.has(id)){metadataRequested.add(id);ids.push(id);}});
    if(ids.length)send('trackInfo',{trackIds:ids});
  }, {root:document.querySelector('.main'),rootMargin:'100px'}) : null;
  let previewQueue = samples.map((_,index) => index);
  const collator = new Intl.Collator('zh-CN', { numeric: true, sensitivity: 'base' });
  let currentPage = 'overview', history = [], panel = null, toastTimer, lastTick = performance.now(), seeking = false, volumeChanging = false;
  let pendingTrack = null;
  // Give every play entry point the same immediate feedback; only the host confirms audio playback.
  const send = (command, payload = {}) => {
    if (!live) return;
    const id = command === 'playTrack' ? payload.trackId : command === 'playAll' ? payload.trackIds?.[0] : null;
    const track = id && libraryTracks.find(item => item.id === id);
    if (track) {
      pendingTrack = { id, until: performance.now() + 8000 };
      state = { ...state, ...track, trackId: id, index: libraryTracks.indexOf(track), position: 0, playing: false, favorite: !!track.favorite };
      $('track-quality').textContent = '正在准备播放…';
      render();
      window.CelesteMotion?.fadeIn(document.querySelector('.track-info'), 'cover');
      document.dispatchEvent(new CustomEvent('celeste:state', { detail: { ...state } }));
    }
    webview.postMessage({ kind: 'celeste.command', version: 1, command, ...payload });
  };
  const notify = text => { $('toast').textContent = text; $('toast').classList.add('visible'); clearTimeout(toastTimer); toastTimer = setTimeout(() => $('toast').classList.remove('visible'), 2600); };
  const icon = name => { const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg'); const use = document.createElementNS(svg.namespaceURI, 'use'); use.setAttribute('href', '#i-' + name); svg.append(use); return svg; };
  const format = seconds => { seconds = Math.max(0, Math.floor(seconds)); return Math.floor(seconds / 60) + ':' + String(seconds % 60).padStart(2, '0'); };
  const fill = input => input.style.setProperty('--fill', `${Number(input.value) / Math.max(1, Number(input.max)) * 100}%`);
  let pathActions=[];
  let pendingDirection='forward';   // 返回键走反方向转场
  function setPath(items){
    const root=$('breadcrumb');root.replaceChildren();pathActions=[{label:'主页',action:()=>navigate('overview')},...items];
    if(currentPage==='overview')pathActions=pathActions.slice(0,1);
    pathActions.forEach((item,index)=>{if(index){const slash=document.createElement('span');slash.className='slash';slash.textContent='/';root.append(slash);}const button=document.createElement('button');button.textContent=item.label;button.addEventListener('click',()=>item.action?.());if(index===pathActions.length-1)button.setAttribute('aria-current','page');root.append(button);});
    $('back').disabled=pathActions.length<2;
  }
  function navigate(page, remember = true, direction = 'forward') {
    if (!pages[page]) return;
    const moveDirection = direction === 'back' ? 'back' : (pendingDirection === 'back' ? 'back' : 'forward');
    pendingDirection = 'forward';
    send('cancelOverview');
    if (remember && page !== currentPage) history.push(currentPage);
    currentPage = page;
    setPath([{label:pages[page][0]}]);
    $('overview-page').hidden = page !== 'overview';
    $('songs-page').hidden = page !== 'songs';
    $('albums-page').hidden = page !== 'albums';
    $('artists-page').hidden = !['artists','albumArtists'].includes(page);
    $('browse-page').hidden = !['folders','favorites','ratings','recent','mostPlayed','playlists','queue'].includes(page);
    $('settings-page').hidden = page !== 'settings';
    $('category-page').hidden = ['overview','songs','albums','artists','albumArtists','folders','favorites','ratings','recent','mostPlayed','playlists','queue','settings'].includes(page);
    $('search').placeholder = page === 'songs' ? '搜索标题、专辑、艺术家' : page === 'albums' ? '搜索专辑、艺术家、年份' : '搜索你的音乐';
    $('category-title').textContent = pages[page][0];
    $('category-description').textContent = pages[page][2];
    $('category-icon').replaceChildren(icon(pages[page][1]).firstChild);
    $('canvas-title').textContent = '这一页，由你来定义。';
    $('canvas-description').textContent = '整体空间已经准备好。内容布局和细节，留给下一步一起打磨。';
    document.querySelectorAll('[data-page]').forEach(button => { const selected = button.dataset.page === page; button.classList.toggle('selected', selected); if (selected) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current'); });
    $('back').disabled = history.length === 0;
    document.querySelector('.main').scrollTop = 0;
    if (page === 'songs') renderSongs();
    document.dispatchEvent(new CustomEvent('celeste:navigate',{detail:page}));
    // 页面转场交给动效控制器：新页淡入并轻微上移。快速连点时旧动画会被作废，
    // 不会闪白，也不会把刚打开的页面又关掉。
    const shown = [...document.querySelectorAll('.page')].find(el => !el.hidden);
    if (shown) window.CelesteMotion?.page(shown, moveDirection);
  }
  // Fixed quarter-circle, clockwise from the top edge toward the left edge.
  // Radii are in the 600x290 SVG viewBox, so they scale together with the container size in navigation.css.
  // 150 and 238 are NOT arbitrary: the wheel draws three guide arcs at radius 105 (core), 195 (inner)
  // and 280 (edge). Each icon ring must sit at the midpoint of the band it lives in, or it looks off-centre:
  //   inner ring: (105 + 195) / 2 = 150      outer ring: (195 + 280) / 2 = 237.5 ≈ 238
  // If you change either number, change the matching guide arc in index.html by the same amount.
  const rings = [
    { radius: 150, pages: ['songs','albums','artists','albumArtists','folders'] },
    { radius: 238, pages: ['favorites','ratings','mostPlayed','playlists','queue','recent'] }
  ];
  rings.forEach((ring, ringIndex) => ring.pages.forEach((page,index) => {
    const angle = (12 + index * 156 / (ring.pages.length - 1)) * Math.PI / 180;
    const button = document.createElement('button');
    button.className = 'corner-item ring-' + (ringIndex + 1);
    button.dataset.page = page;
    button.setAttribute('aria-label', pages[page][0]);
    button.title = pages[page][0];
    button.style.left = (300 - Math.cos(angle) * ring.radius) / 600 * 100 + '%';
    button.style.top = (10 + Math.sin(angle) * ring.radius) / 290 * 100 + '%';
    button.append(icon(pages[page][1]));
    $('orbit-content').append(button);
  }));
  function setOrbit(open) {
    const orbit = $('corner-orbit');
    orbit.classList.toggle('is-collapsed', !open);
    document.querySelector('.shell').classList.toggle('orbit-expanded', open);
    $('orbit-toggle').setAttribute('aria-expanded', String(open));
    $('orbit-toggle').setAttribute('aria-label', open ? '收起分类轮盘' : '展开分类轮盘');
    $('orbit-toggle').title = open ? '收起分类轮盘' : '展开分类轮盘';
    if (!open && $('orbit-content').contains(document.activeElement)) $('orbit-toggle').focus({ preventScroll: true });
    $('orbit-content').inert = !open;
  }
  $('orbit-toggle').addEventListener('click', () => setOrbit($('orbit-toggle').getAttribute('aria-expanded') !== 'true'));
  // Scrolling any page or scrollable panel dismisses the wheel, never opens it.
  document.querySelector('.workspace').addEventListener('wheel', () => setOrbit(false), { passive: true });
  document.querySelector('.main').addEventListener('scroll', () => setOrbit(false), { passive: true });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape') setOrbit(false);
    if (['PageDown','PageUp','Home','End','ArrowDown','ArrowUp',' '].includes(event.key) && !event.target.closest('#corner-orbit,input,select,button')) setOrbit(false);
  });
  document.addEventListener('click', event => { const target = event.target.closest('[data-page]'); if (target) { navigate(target.dataset.page); setOrbit(false); } else if (!event.target.closest('#corner-orbit')) setOrbit(false); });
  $('back').addEventListener('click', () => {pendingDirection='back';const parent=pathActions.at(-2);if(parent?.action)parent.action();else navigate('overview');});
  document.addEventListener('keydown', event => { if (event.key === '/' && !['INPUT','TEXTAREA'].includes(document.activeElement.tagName)) { event.preventDefault(); $('search').focus(); } });
  $('search').addEventListener('input', event => { songQuery = event.target.value.trim(); if (currentPage === 'songs') renderSongs(); });
  $('search').addEventListener('keydown', event => { if (event.key === 'Enter') { songQuery = event.target.value.trim(); navigate(['albums','artists','albumArtists','folders','favorites','ratings','recent','mostPlayed','playlists','queue'].includes(currentPage)?currentPage:'songs'); setOrbit(false); } });
  let sourceFilter = 'all';
  const inSource = track => sourceFilter === 'all' || (sourceFilter === 'local' ? !track.remote : sourceFilter === 'network' ? track.remote : track.sourceId === sourceFilter);
  function refreshSources() {
    const select = $('library-source');
    if (!select) return;
    const sources = new Map(libraryTracks.filter(t => t.sourceId).map(t => [t.sourceId, t.sourceName || '网络来源']));
    select.replaceChildren(new Option('全部来源', 'all'), new Option('本地曲库', 'local'), new Option('网络曲库', 'network'));
    sources.forEach((name, id) => select.append(new Option(name, id)));
    if (!['all', 'local', 'network'].includes(sourceFilter) && !sources.has(sourceFilter)) sourceFilter = 'all';
    select.value = sourceFilter;
  }
  $('library-source')?.addEventListener('change', event => {
    sourceFilter = event.target.value; visibleLimit = 200; rowStamp = '';
    renderSongs();
    document.dispatchEvent(new CustomEvent('celeste:library'));
    document.dispatchEvent(new CustomEvent('celeste:navigate', { detail: currentPage }));
  });
  function visibleSongs() {
    const query = songQuery.toLocaleLowerCase();
    const key = $('songs-sort').value;
    return (live ? libraryTracks : samples).map((track,index) => ({ track,index })).filter(({track}) => inSource(track) && (!query || [track.title,track.artist,track.album].some(value => value.toLocaleLowerCase().includes(query)))).sort((a,b) => {
      const result = ['duration','year','track'].includes(key) ? Number(a.track[key]||0)-Number(b.track[key]||0) : collator.compare(a.track[key]||'',b.track[key]||'');
      return (result || collator.compare(a.track.title,b.track.title) || a.index-b.index) * (sortAscending ? 1 : -1);
    });
  }
  function updateSongRows() {
    const stamp = `${state.index}:${state.playing}:${selectedSong}`;
    if (stamp === rowStamp) return;
    rowStamp = stamp;
    document.querySelectorAll('.song-row').forEach(button => {
      const index = Number(button.dataset.index), playing = index === state.index && state.playing;
      button.classList.toggle('selected', index === selectedSong);
      button.classList.toggle('playing', playing);
      button.setAttribute('aria-pressed', String(index === selectedSong));
      const number = button.querySelector('.song-number');
      if (playing) number.replaceChildren(icon('wave')); else number.textContent = button.dataset.number;
    });
  }
  function renderSongs() {
    const tracks = visibleSongs();
    // 排序 / 换来源会让行的位置整体移动：先记位置，重建后再让新行从旧位置滑过去
    const rowPositions = window.CelesteMotion?.captureRows($('songs-list'), { selector: '.song-row' });
    $('songs-list').replaceChildren();
    metadataObserver?.disconnect();
    rowStamp = '';
    const source = live ? libraryTracks.filter(inSource) : samples;
    $('songs-count').textContent = libraryLoading ? `正在加载曲库 · ${pendingTracks.length} / ${libraryTotal} 首` : (songQuery ? `${tracks.length} 首匹配 · 共 ${source.length} 首` : `${source.length} 首歌曲 · ${format(source.reduce((sum,track)=>sum+track.duration,0)).replace(':',' 分 ')} 秒`);
    $('songs-preview').textContent = live ? (libraryLoading ? '正在加载' : '统一曲库') : '演示曲库';
    $('songs-play-all').disabled = tracks.length === 0;
    $('songs-empty').hidden = tracks.length > 0;
    $('songs-empty').querySelector('h2').textContent = libraryLoading ? '正在载入你的音乐' : !source.length ? '曲库中还没有歌曲' : '没有找到相关歌曲';
    $('songs-empty').querySelector('p').textContent = libraryLoading ? '曲库较大时请稍候。' : !source.length ? '可在文件夹页添加本地音乐，或在设置的网络页添加来源。' : '试试其他歌名、艺术家或专辑。';
    $('songs-clear').hidden = !songQuery;
    $('songs-more').hidden = tracks.length <= visibleLimit;
    tracks.slice(0,visibleLimit).forEach(({track,index}, order) => {
      const item = document.createElement('li'), button = document.createElement('button');
      button.className = 'song-row'; button.dataset.index = index; button.dataset.number = order + 1;
      // 排序/换源时靠这个键把"新的一行"对回"旧位置"，才有滑动；不是给 CSS 用的
      button.dataset.flipkey = live ? track.id : String(index);
      if(live)button.dataset.trackId=track.id;
      button.setAttribute('aria-label', `${track.title}，${track.artist}，${format(track.duration)}`);
      const number = document.createElement('span'); number.className='song-number'; number.textContent=order+1;
      const cover = document.createElement('span'); cover.className='song-cover art-'+(track.art || 'teal'); cover.setAttribute('aria-hidden','true');
      const sleeve = document.createElement('small'); sleeve.textContent=track.sleeve; cover.append(sleeve);
      if (live) { cover.className='song-cover live-cover'; sleeve.textContent=''; cover.append(icon('note')); const image=document.createElement('img'); image.src=track.coverUrl; image.loading='lazy'; image.alt=''; image.addEventListener('error',()=>image.remove(),{once:true}); cover.append(image); }
      const copy = document.createElement('span'); copy.className='song-copy';
      const title = document.createElement('span'); title.className='song-title'; title.textContent=track.title;
      const subtitle = document.createElement('span'); subtitle.className='song-subtitle'; subtitle.textContent=track.artist+' · '+track.album;
      button.title=track.title+'\n'+subtitle.textContent;
      const badges = document.createElement('span'); badges.className='song-badges';
      (live ? metadataCache.get(track.id)||track.chips : [track.codec || 'MPEG-4 ALAC', track.quality, track.bitrate]).forEach(value => { const badge=document.createElement('span'); badge.textContent=value; badges.append(badge); });
      copy.append(title,subtitle,badges);
      const duration = document.createElement('span'); duration.className='song-duration'; duration.textContent=format(track.duration);
      button.append(number,cover,copy,duration);
      const playFromList = () => { previewQueue=visibleSongs().map(item=>item.index); chooseTrack(index); };
      button.addEventListener('click', event => { selectedSong=index; updateSongRows(); if (event.detail === 0) playFromList(); });
      button.addEventListener('dblclick', playFromList);
      item.append(button); $('songs-list').append(item);
      metadataObserver?.observe(button);
    });
    updateSongRows();
    // 有行真的换了位置就滑过去；整批换内容（切来源、搜索）才退回一次轻淡变
    const moved = window.CelesteMotion?.replayRows($('songs-list'), rowPositions, { selector: '.song-row' }) || 0;
    if (!moved) window.CelesteMotion?.fadeIn($('songs-list'), 'list');
  }
  $('songs-more').addEventListener('click',()=>{visibleLimit+=200;renderSongs();});
  $('songs-sort').addEventListener('change', renderSongs);
  $('songs-direction').addEventListener('click', () => { sortAscending=!sortAscending; $('songs-direction').textContent=sortAscending?'升序 ↑':'降序 ↓'; $('songs-direction').setAttribute('aria-label',sortAscending?'切换为降序':'切换为升序'); renderSongs(); });
  $('songs-clear').addEventListener('click', () => { songQuery=''; $('search').value=''; renderSongs(); $('search').focus(); });
  $('songs-play-all').addEventListener('click', () => { const tracks=visibleSongs(); if(tracks.length) { if(live)send('playAll',{trackIds:tracks.map(item=>item.track.id)});else{previewQueue=tracks.map(item=>item.index);chooseTrack(previewQueue[0]);} } });
  function render() {
    $('track-title').textContent = state.title; $('track-artist').textContent = state.artist;
    if (!live) {
      const cover = document.querySelector('.mini-cover');
      cover.className = 'mini-cover art-' + state.art;
      cover.querySelector('span').textContent = state.sleeve;
    } else {
      updateCover(state.coverUrl || '');
    }
    $('play').setAttribute('aria-pressed', String(state.playing)); $('play').setAttribute('aria-label', state.playing ? '暂停' : '播放'); $('play').querySelector('use').setAttribute('href', state.playing ? '#i-pause' : '#i-play');
    $('progress').max = Math.max(1, state.duration);
    if (!seeking) $('progress').value = state.position;
    $('progress').setAttribute('aria-valuetext', `${format(state.position)} / ${format(state.duration)}`); fill($('progress'));
    $('elapsed').textContent = format(state.position); $('duration').textContent = format(state.duration);
    if(!volumeChanging)$('volume').value = state.volume;
    $('volume').disabled=state.volumeEnabled===false;
    $('volume').title=state.volumeEnabled===false?'独占 / ASIO 当前使用原音量策略，请调节设备硬件音量':'音量';fill($('volume'));
    $('favorite').setAttribute('aria-pressed',String(state.favorite));
    const order=orders[state.playbackOrder]||orders.ListLoop;
    $('playback-order').setAttribute('aria-label',order);
    $('playback-order').title=order+' · 左键切换，右键选择';
    // Each mode now has its own glyph. Previously Sequential/TrackOnce reused the queue icon
    // (three bars) and TrackLoop depended on a "1" badge, which read as noise at 21px.
    const orderIcons={Sequential:'#i-order-sequential',ListLoop:'#i-order-listloop',TrackLoop:'#i-order-trackloop',TrackOnce:'#i-order-once',Random:'#i-order-random'};
    $('playback-order').querySelector('use').setAttribute('href',orderIcons[state.playbackOrder]||'#i-order-listloop');
    $('order-badge').textContent='';
    document.querySelectorAll('[data-order]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.order===state.playbackOrder)));
    $('lyrics').setAttribute('aria-pressed',String(state.desktopLyrics));
    $('lyrics-badge').textContent=state.desktopLyrics?(state.desktopLyricsLocked?'锁':'on'):'off';
    updateSongRows();
  }
  function chooseTrack(index) { if (live) { const track=libraryTracks[index]; if(track)send('playTrack',{trackId:track.id});return; } state = { ...state, ...samples[index], index, position: 0, playing: true }; selectedSong=index; lastTick=performance.now(); render(); if (panel === 'queue') renderPanel(); }
  function stepTrack(direction) {
    if (live) return send(direction > 0 ? 'next' : 'previous');
    const current = previewQueue.indexOf(state.index);
    const next = (current + direction + previewQueue.length) % previewQueue.length;
    chooseTrack(previewQueue[next]);
  }
  function togglePlay() { if (live) { send('togglePlay'); return; } state.playing = !state.playing; lastTick = performance.now(); render(); }
  $('play').addEventListener('click', togglePlay);
  $('next').addEventListener('click', () => stepTrack(1));
  $('previous').addEventListener('click', () => stepTrack(-1));
  $('favorite').addEventListener('click',()=>{if(live)send('favorite');else{state.favorite=!state.favorite;render();}});
  function setOrder(order){if(live)send('setPlaybackOrder',{order});else{state.playbackOrder=order;state.shuffle=order==='Random';state.repeat=order==='TrackLoop';render();}$('order-menu').hidden=true;}
  $('playback-order').addEventListener('click',()=>{if(live)send('transport',{action:'cycleOrder'});else{const keys=Object.keys(orders);setOrder(keys[(keys.indexOf(state.playbackOrder)+1)%keys.length]);}});
  $('playback-order').addEventListener('contextmenu',event=>{event.preventDefault();$('order-menu').hidden=!$('order-menu').hidden;});
  document.querySelectorAll('[data-order]').forEach(button=>button.addEventListener('click',()=>setOrder(button.dataset.order)));
  document.addEventListener('pointerdown',event=>{if(!event.target.closest('#order-menu,#playback-order'))$('order-menu').hidden=true;});
  document.addEventListener('keydown',event=>{if(event.key==='Escape')$('order-menu').hidden=true;});
  [['seek-back','seekBack',-5],['seek-forward','seekForward',5]].forEach(([id,action,seconds])=>$(id).addEventListener('click',()=>{if(live)send('transport',{action});else{state.position=Math.max(0,Math.min(state.duration,state.position+seconds));render();}}));
  [['audio-settings','audio'],['transport-more','more'],['queue','queue']].forEach(([id,view])=>$(id).addEventListener('click',()=>window.CelesteTransport?.open(view,$(id))));
  $('lyrics').addEventListener('click',()=>live?send('transport',{action:'desktopLyrics'}):notify('桌面歌词需在桌面预览版中使用'));
  $('lyrics').addEventListener('contextmenu',event=>{event.preventDefault();if(live)send('transport',{action:'unlockLyrics'});});
  $('progress').addEventListener('input', event => { seeking=true; fill(event.target); $('elapsed').textContent=format(Number(event.target.value)); });
  $('progress').addEventListener('change', event => { const position=Number(event.target.value); seeking=false; state.position=position; if (live) send('seek', { seconds: position }); render(); });
  $('volume').addEventListener('input', event => { volumeChanging=true;const volume=Number(event.target.value);state.volume=volume;fill(event.target);if(live)send('setVolume',{volume}); });
  $('volume').addEventListener('change',()=>{volumeChanging=false;});
  $('volume').addEventListener('blur',()=>{volumeChanging=false;});
  function renderPanel() { $('panel-title').textContent=panel==='queue'?'接下来播放':'歌词'; $('panel-body').replaceChildren(); if (panel==='queue' && !live) previewQueue.forEach(index => { const track=samples[index]; const button=document.createElement('button'); button.className='panel-track' + (state.index===index?' active':''); const number=document.createElement('small'); number.textContent=String(index+1).padStart(2,'0'); const text=document.createElement('span'); text.textContent=track.title; const duration=document.createElement('small'); duration.textContent=format(track.duration)+' · 演示曲目'; text.append(duration); button.append(number,text); button.addEventListener('click',()=>chooseTrack(index)); $('panel-body').append(button); }); else { const p=document.createElement('p'); p.className=panel==='lyrics'?'lyric-placeholder':'panel-notice'; p.textContent=panel==='lyrics'?'让每一句，\n都有回响。':'队列接口准备接入。'; $('panel-body').append(p); } const note=document.createElement('p'); note.className='panel-subtle'; note.textContent=panel==='lyrics'?'歌词区域占位 · 待接入真实歌词':'队列面板框架 · 具体样式待定'; $('panel-body').append(note); }
  function setPanel(next) { panel=next; $('side-panel').hidden=!panel; $('queue').setAttribute('aria-expanded',String(panel==='queue')); if(panel)renderPanel(); }
  $('panel-close').addEventListener('click',()=>setPanel(null));
  document.querySelectorAll('[data-window]').forEach(button=>button.addEventListener('click',()=>live?send('window',{ action:button.dataset.window }):notify('窗口操作将在 WebView2 桌面外壳中启用')));
  if (live) {
    $('native-view').hidden=false;
    $('native-view').addEventListener('click',()=>send('window',{action:'close'}));
    $('library-note').textContent='本机音频引擎 · 原输出与 DSP 配置';
    $('mode-label').textContent='等待播放器连接'; $('track-title').textContent='等待播放状态'; state.title='等待播放状态'; state.artist='Celeste Music Player'; state.duration=0; $('track-quality').textContent='WebView2 · 等待宿主数据';
    webview.addEventListener('message', event => {
      const message=event.data;
      if(message?.version!==1)return;
      document.dispatchEvent(new CustomEvent('celeste:message',{detail:message}));
      if(message.kind==='celeste.error'){notify(String(message.message||'操作失败'));return;}
      if(message.kind==='celeste.metadata'&&typeof message.trackId==='string'&&Array.isArray(message.chips)){
        const chips=message.chips.filter(value=>typeof value==='string');metadataCache.set(message.trackId,chips);
        const button=[...document.querySelectorAll('.song-row')].find(row=>row.dataset.trackId===message.trackId);
        if(button){const badges=button.querySelector('.song-badges');badges.replaceChildren();chips.forEach(value=>{const badge=document.createElement('span');badge.textContent=value;badges.append(badge);});}
        return;
      }
      if(message.kind==='celeste.library'){
        if(message.phase==='begin'){libraryRevision=message.revision;pendingTracks=[];libraryTotal=message.total;libraryLoading=true;}
        else if(message.revision!==libraryRevision)return;
        else if(message.phase==='batch'&&Array.isArray(message.tracks))pendingTracks.push(...message.tracks.filter(track=>typeof track.id==='string'&&typeof track.title==='string'&&typeof track.artist==='string'&&typeof track.album==='string'&&Number.isFinite(track.duration)&&Array.isArray(track.chips)));
        else if(message.phase==='end'){libraryTracks=pendingTracks;libraryAlbums=Array.isArray(message.albums)?message.albums:[];pendingTracks=[];libraryLoading=false;refreshSources();visibleLimit=200;state.index=libraryTracks.findIndex(track=>track.id===state.trackId);selectedSong=null;document.dispatchEvent(new CustomEvent('celeste:library'));}
        if(currentPage==='songs')renderSongs();
        return;
      }
      if(message.kind!=='celeste.state'||!message.state||typeof message.state!=='object')return;
      const incoming=message.state;
      if (pendingTrack) {
        if (incoming.trackId !== pendingTrack.id && incoming.busy === true && !incoming.error && performance.now() < pendingTrack.until) return;
        pendingTrack = null;
      }
      ['title','artist','album'].forEach(key=>{if(typeof incoming[key]==='string')state[key]=incoming[key]});
      if(typeof incoming.trackId==='string'){
        state.trackId=incoming.trackId;
        state.index=libraryTracks.findIndex(track=>track.id===state.trackId);
        state.coverUrl=typeof incoming.coverUrl==='string'?incoming.coverUrl:(state.index>=0?libraryTracks[state.index].coverUrl:'');
      }
      ['duration','position','volume'].forEach(key=>{if(Number.isFinite(incoming[key]))state[key]=Math.max(0,incoming[key])});
      state.volume=Math.min(100,state.volume); state.position=Math.min(state.duration,state.position);
      ['playing','favorite','shuffle','repeat'].forEach(key=>{if(typeof incoming[key]==='boolean')state[key]=incoming[key]});
      if(orders[incoming.playbackOrder])state.playbackOrder=incoming.playbackOrder;
      ['desktopLyrics','desktopLyricsLocked'].forEach(key=>{if(typeof incoming[key]==='boolean')state[key]=incoming[key]});
      if(typeof incoming.volumeEnabled==='boolean')state.volumeEnabled=incoming.volumeEnabled;
      $('mode-label').textContent='已连接播放器';
      const mode={WasapiShared:'WASAPI 共享',WasapiExclusive:'WASAPI 独占',Asio:'ASIO'}[incoming.outputMode]||'';
      $('track-quality').textContent=[incoming.busy?'正在准备播放…':mode,incoming.quality].filter(Boolean).join(' · ')||'等待播放';
      $('track-quality').title=[mode,incoming.outputDevice,incoming.quality].filter(Boolean).join(' · ');
      if(incoming.error&&incoming.error!==state.lastError)notify(incoming.error);
      state.lastError=incoming.error||'';
      render();
      // 别的模块（正在播放详情页）也要跟着同一份状态走，别再各自轮询一遍
      document.dispatchEvent(new CustomEvent('celeste:state',{detail:{...state}}));
    });
    send('ready');
  } else {
    setInterval(() => { const now=performance.now(); const delta=(now-lastTick)/1000; lastTick=now; if(!state.playing||document.hidden||seeking)return; state.position=Math.min(state.duration,state.position+delta); if(state.position>=state.duration){if(state.repeat)state.position=0;else if(state.shuffle){const choices=previewQueue.filter(index=>index!==state.index);chooseTrack(choices.length?choices[Math.floor(Math.random()*choices.length)]:state.index)}else stepTrack(1)} render(); },250);
    document.addEventListener('visibilitychange',()=>lastTick=performance.now());
  }
  window.CelesteShell=Object.freeze({send,notify,navigate,setPath,getPage:()=>currentPage,getState:()=>({...state}),getLibrary:()=>live?libraryTracks.filter(inSource):samples,getAlbums:()=>libraryAlbums.filter(album=>album.trackIds.some(id=>libraryTracks.some(t=>t.id===id&&inSource(t)))),playTrack:id=>chooseTrack(libraryTracks.findIndex(track=>track.id===id)),playDemo:chooseTrack,playTracks:ids=>send('playAll',{trackIds:ids})});
  navigate('overview',false); setOrbit(true); render();
})();
