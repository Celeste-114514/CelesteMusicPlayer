/* Original Celeste implementation, visually guided by Twilight Echo's AudioVisualizerPanel
   and resources/audio-visualizer/index.html. No foreign runtime or audio session is loaded. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id), page = $('now-playing'), shell = window.CelesteShell;
  const selector = $('np-theme'), visual = $('np-visual');
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const NS = 'http://www.w3.org/2000/svg';
  const svg = (tag, attributes) => {
    const element = document.createElementNS(NS, tag);
    Object.entries(attributes).forEach(([key, value]) => element.setAttribute(key, value));
    return element;
  };
  let theme = 'default', timer = 0, busy = false, generation = 0, lastTrackId = null, hostHidden = false;
  try { if (localStorage.getItem('celeste.now-playing.theme') === 'twilight') theme = 'twilight'; } catch {}
  const dial = svg('svg', { viewBox: '0 0 600 600', 'aria-hidden': 'true' });
  [200, 250, 298].forEach((r, i) => dial.append(svg('circle', { cx: 300, cy: 300, r, class: i === 1 ? 'np-dial-dashed' : '' })));
  for (let i = 0; i < 36; i++) {
    const angle = i * Math.PI / 18, major = i % 9 === 0;
    const radius = major ? 278 : 288;
    dial.append(svg('line', { x1: 300 + Math.sin(angle) * radius, y1: 300 + Math.cos(angle) * radius,
      x2: 300 + Math.sin(angle) * 296, y2: 300 + Math.cos(angle) * 296 }));
  }
  dial.append(svg('path', { d: 'M0 300H600M300 0V600M88 88L512 512M88 512L512 88', class: 'np-dial-axis' }));
  [[300, 0], [300, 600]].forEach(([cx, cy]) => dial.append(svg('circle', { cx, cy, r: 4, class: 'np-dial-marker' })));
  [[300, 42, '0°'], [555, 305, '90°'], [300, 565, '180°'], [42, 305, '270°']].forEach(([x, y, text]) => {
    const label = svg('text', { x, y, 'text-anchor': 'middle', class: 'np-dial-label' }); label.textContent = text; dial.append(label);
  });
  $('np-radar').append(dial);
  const spectrum = $('np-spectrum'), scope = $('np-scope');
  for (let i = 0; i <= 5; i++) spectrum.append(svg('path', { d: `M0 ${i * 50 + 5}H800`, class: 'np-chart-grid' }));
  const spectrumPath = svg('path', { d: 'M0 255H800', class: 'np-spectrum-signal' });
  const scopePath = svg('path', { d: 'M0 32H800', class: 'np-scope-signal' });
  spectrum.append(spectrumPath); scope.append(svg('path', { d: 'M0 32H800', class: 'np-chart-grid' }), scopePath);
  const fmt = seconds => `${Math.floor((seconds || 0) / 60)}:${String(Math.floor((seconds || 0) % 60)).padStart(2, '0')}`;
  const number = (value, min, max) => Number.isFinite(value) ? Math.min(max, Math.max(min, value)) : 0;
  function paint(data = {}) {
    const bands = data.available && Array.isArray(data.bands) ? data.bands : [];
    const points = bands.map((v, i) => `${i / Math.max(1, bands.length - 1) * 800} ${255 - number(v, 0, 1) * 240}`);
    spectrumPath.setAttribute('d', points.length ? `M0 255L${points.join('L')}L800 255Z` : 'M0 255H800');
    const samples = data.samplesAvailable && Array.isArray(data.samples) ? data.samples : [];
    scopePath.setAttribute('d', samples.length ? samples.map((v, i) => `${i ? 'L' : 'M'}${i / Math.max(1, samples.length - 1) * 800} ${32 - number(v, -1, 1) * 29}`).join('') : 'M0 32H800');
    $('np-visual-status').textContent = !shell.getState().playing ? '播放已暂停' : data.available ? 'LIVE · 实时信号' : '当前输出暂无可视化信号';
    const rms = samples.length ? Math.sqrt(samples.reduce((sum, v) => sum + number(v, -1, 1) ** 2, 0) / samples.length) : 0;
    $('np-visual-level').textContent = rms > 0 ? `瞬时 RMS ${(20 * Math.log10(rms)).toFixed(1)} dBFS` : '—';
  }
  async function poll() {
    if (busy || !live || document.hidden || hostHidden || theme !== 'twilight' || !window.CelesteNowPlaying.isOpen() || !shell.getState().playing) return;
    busy = true;
    const ticket = generation, trackId = shell.getState().trackId;
    try {
      const data = await window.CelesteTransport.request('nowPlayingVisual', {});
      if (ticket !== generation || !window.CelesteNowPlaying.isOpen() || document.hidden || hostHidden || !shell.getState().playing
          || shell.getState().trackId !== trackId || data.trackId !== trackId) return;
      paint(data);
    } catch { if (ticket === generation) paint(); }
    finally { busy = false; }
  }
  function sync() {
    const state = shell.getState();
    if (lastTrackId !== state.trackId) { lastTrackId = state.trackId; generation++; paint(); }
    $('np-artist').textContent = (theme === 'twilight' ? state.artist : [state.artist, state.album].filter(Boolean).join(' · ')) || '—';
    $('np-title').dataset.album = state.album ? `FROM · ${state.album}` : 'CELESTE · AUDIO INSTRUMENT';
    $('np-visual-source').textContent = state.source || '—';
    $('np-visual-quality').textContent = state.quality || $('track-quality').textContent || '—';
    $('np-visual-duration').textContent = fmt(state.duration);
    const active = theme === 'twilight' && window.CelesteNowPlaying.isOpen() && !document.hidden && !hostHidden && state.playing;
    if (active && !timer) { poll(); timer = setInterval(poll, 100); }
    if (!active) { clearInterval(timer); timer = 0; generation++; paint(); }
  }
  function apply(value) {
    theme = value === 'twilight' ? 'twilight' : 'default';
    generation++;
    selector.value = theme; page.dataset.playerTheme = theme;
    visual.hidden = theme !== 'twilight';
    try { localStorage.setItem('celeste.now-playing.theme', theme); } catch {}
    sync();
    if (window.CelesteNowPlaying.isOpen()) window.CelesteMotion?.reveal(page.querySelector('.np-body'), 'cover', 8);
  }
  selector.addEventListener('change', () => apply(selector.value));
  document.addEventListener('celeste:state', sync);
  document.addEventListener('celeste:message', event => {
    if (event.detail.kind === 'celeste.visibility') { hostHidden = !!event.detail.hidden; sync(); }
  });
  document.addEventListener('celeste:now-playing', sync);
  document.addEventListener('visibilitychange', sync);
  apply(theme);
})();
