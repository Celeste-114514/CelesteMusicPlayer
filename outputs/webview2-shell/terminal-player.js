/* 音频终端详情页：三区仪表 + 真实立体声相位仪。
   数据全部来自 nowPlayingVisual 的 stereo 快照；没有采样就显示空网格和状态，不画假图形。
   自己管订阅与轮询：详情页关掉、暂停、宿主隐藏都要把采样还回去（宿主侧是引用计数）。 */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const page = $('now-playing'), shell = window.CelesteShell;
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const NS = 'http://www.w3.org/2000/svg';
  const svgEl = (tag, attrs) => { const e = document.createElementNS(NS, tag); Object.entries(attrs || {}).forEach(([k, v]) => e.setAttribute(k, v)); return e; };
  const fmt = s => `${Math.floor((s || 0) / 60)}:${String(Math.floor((s || 0) % 60)).padStart(2, '0')}`;
  const db = v => v > 0.0001 ? `${(20 * Math.log10(v)).toFixed(1)} dBFS` : '—';
  const dash = '—';

  let built = false, running = false, timer = 0, frame = 0, busy = false, hostHidden = false;
  let scope = null, subscribed = false, trackId = '', facts = null, queueRevision = -1, queueToken = 0;
  let spectrumTarget = [], spectrumShown = [], peakLeft = 0, peakRight = 0, peakAt = 0;
  const els = {};

  function block(title, id, kind = 'dl') {
    const section = document.createElement('section'); section.className = 'term-block';
    const h = document.createElement('h3'); h.textContent = title; section.append(h);
    const body = document.createElement(kind); body.id = id; section.append(body);
    return section;
  }

  function build() {
    if (built) return; built = true;
    const root = document.createElement('section');
    root.id = 'np-terminal'; root.className = 'np-terminal'; root.hidden = true;
    root.dataset.termAccent = 'amber';

    const head = document.createElement('header'); head.className = 'term-head';
    const brand = document.createElement('span'); brand.className = 'term-brand'; brand.textContent = 'CELESTE // AUDIO TERMINAL';
    els.state = document.createElement('span'); els.state.className = 'term-state'; els.state.textContent = 'STOPPED';
    els.time = document.createElement('span'); els.time.className = 'term-time'; els.time.textContent = '0:00 / 0:00';
    els.chain = document.createElement('span'); els.chain.className = 'term-chain'; els.chain.textContent = dash;
    head.append(brand, els.state, els.time, els.chain);

    const grid = document.createElement('div'); grid.className = 'term-grid';
    const left = document.createElement('aside'); left.className = 'term-col term-col-left';
    left.append(block('AUDIO STREAM / 音频流', 'term-stream'), block('SIGNAL CHAIN / 信号链', 'term-signal'), block('FILE / 文件', 'term-file'));

    const center = document.createElement('section'); center.className = 'term-col term-col-center';
    const spec = document.createElement('div'); spec.className = 'term-block';
    const specTitle = document.createElement('h3'); specTitle.textContent = 'SPECTRUM / 实时频谱'; spec.append(specTitle);
    els.spectrum = svgEl('svg', { id: 'term-spectrum', class: 'term-chart', viewBox: '0 0 800 220', preserveAspectRatio: 'none', 'aria-hidden': 'true' });
    spec.append(els.spectrum);
    els.spectrumStatus = document.createElement('div'); els.spectrumStatus.id = 'term-spectrum-status'; els.spectrumStatus.className = 'term-status'; els.spectrumStatus.textContent = '等待音频信号';
    spec.append(els.spectrumStatus);
    const level = document.createElement('div'); level.className = 'term-block';
    const levelTitle = document.createElement('h3'); levelTitle.textContent = 'LEVEL / 左右电平'; level.append(levelTitle);
    els.levels = document.createElement('div'); els.levels.id = 'term-levels'; els.levels.className = 'term-levels';
    ['L', 'R'].forEach(name => {
      const row = document.createElement('div'); row.className = 'term-level';
      const label = document.createElement('span'); label.textContent = name;
      const bar = document.createElement('div'); bar.className = 'term-level-bar';
      const fill = document.createElement('div'); fill.className = 'term-level-fill';
      const peak = document.createElement('div'); peak.className = 'term-level-peak';
      bar.append(fill, peak);
      const readout = document.createElement('b'); readout.textContent = dash;
      row.append(label, bar, readout); els.levels.append(row);
      (els.levelParts ||= {})[name] = { fill, peak, readout };
    });
    level.append(els.levels);
    center.append(spec, level);

    const right = document.createElement('aside'); right.className = 'term-col term-col-right';
    const phase = document.createElement('div'); phase.className = 'term-block';
    const phaseTitle = document.createElement('h3'); phaseTitle.textContent = 'PHASE / 相位'; phase.append(phaseTitle);
    els.phase = svgEl('svg', { id: 'term-phase', class: 'term-chart ps', viewBox: '0 0 400 400', role: 'img', 'aria-label': '立体声相位图' });
    phase.append(els.phase);
    els.phaseStats = document.createElement('div'); els.phaseStats.id = 'term-phase-stats'; els.phaseStats.className = 'term-status'; els.phaseStats.textContent = '暂无采样';
    phase.append(els.phaseStats);
    const device = block('DEVICE / 设备', 'term-device');
    right.append(phase, device);

    grid.append(left, center, right);

    const lower = document.createElement('div'); lower.className = 'term-lower';
    els.lyrics = document.createElement('div'); els.lyrics.id = 'term-lyrics'; els.lyrics.className = 'term-lyrics'; els.lyrics.setAttribute('role', 'region'); els.lyrics.setAttribute('aria-label', '歌词');
    els.queue = document.createElement('div'); els.queue.id = 'term-queue'; els.queue.className = 'term-queue'; els.queue.setAttribute('aria-label', '接下来播放');
    lower.append(els.lyrics, els.queue);

    root.append(head, grid, lower);
    page.append(root);
    els.root = root;
    scope = new window.CelestePhaseScope.PhaseScope(els.phase);
  }

  // 播放控制条是整页共用的（方案：底部保留统一播放控制）。
  // 终端模式把 .np-body 整个藏起来了，所以要把控制条搬进终端区底部，退出时原样放回。
  let controlsHome = null;
  function moveControls(intoTerminal) {
    const controls = page.querySelector('.np-controls');
    if (!controls) return;
    if (intoTerminal) {
      controlsHome ||= { parent: controls.parentElement, next: controls.nextElementSibling };
      els.root.append(controls);
    } else if (controlsHome?.parent) {
      if (controlsHome.next && controlsHome.next.parentElement === controlsHome.parent) controlsHome.parent.insertBefore(controls, controlsHome.next);
      else controlsHome.parent.append(controls);
      controlsHome = null;
    }
  }

  function setStereo(on) {
    if (on === subscribed || !live) return;
    subscribed = on;
    shell.send('stereoSampling', { enabled: on });
  }

  function isActive() {
    return page.dataset.playerTheme === 'terminal' && !!window.CelesteNowPlaying?.isOpen()
      && !document.hidden && !hostHidden && !!shell.getState().playing;
  }

  function rows(target, pairs) {
    target.replaceChildren();
    pairs.forEach(([k, v]) => {
      const dt = document.createElement('dt'); dt.textContent = k;
      const dd = document.createElement('dd'); dd.textContent = v;
      if (v !== dash) dd.classList.add('is-live');
      target.append(dt, dd);
    });
  }

  function factsText(key, value, suffix = '') { return value ? `${value}${suffix}` : dash; }

  function syncFacts(state) {
    const a = facts || {};
    rows($('term-stream'), [
      ['SAMPLE RATE', a.sampleRate ? `${(a.sampleRate / 1000).toFixed(a.sampleRate % 1000 ? 1 : 0)} kHz` : dash],
      ['BIT DEPTH', factsText(a.bitDepth, '-bit')],
      ['CHANNELS', a.channels === 2 ? 'Stereo' : a.channels === 1 ? 'Mono' : a.channels ? `${a.channels} ch` : dash],
      ['BITRATE', factsText(a.kbps, ' kbps')]
    ]);
    // 信号链只显示后端真给的值，不从文件格式猜输出状态
    rows($('term-signal'), [
      ['SOURCE', state.source || dash],
      ['QUALITY', state.quality?.split(' · ')[0] || dash],
      ['OUTPUT', state.outputDevice || dash],
      ['STAGE', 'post-dsp']
    ]);
    rows($('term-device'), [
      ['OUTPUT', state.outputDevice || dash],
      ['SIZE', a.fileSize ? `${(a.fileSize / 1048576).toFixed(1)} MB` : dash],
      ['FORMAT', state.quality?.split(' · ')[0] || dash]
    ]);
    rows($('term-file'), [
      ['FILE SIZE', a.fileSize ? `${(a.fileSize / 1048576).toFixed(1)} MB` : dash],
      ['DURATION', fmt(state.duration)],
      ['TRACK', state.title || dash]
    ]);
  }

  function syncLyrics(state) {
    const lines = window.CelesteNowPlaying?.getLyrics?.() || [];
    const box = els.lyrics;
    if (!lines.length) { box.textContent = window.CelesteNowPlaying?.getLyricState?.() === 'loading' ? '正在解析歌词…' : '这首歌没有可用的歌词'; return; }
    const position = state.position || 0;
    let current = -1;
    for (let i = 0; i < lines.length; i++) if (lines[i].t <= position) current = i; else break;
    box.replaceChildren();
    lines.forEach((line, index) => {
      const div = document.createElement('div');
      div.className = 'term-line' + (index === current ? ' is-current' : '');
      div.textContent = line.text || '';
      box.append(div);
    });
    const active = box.querySelector('.is-current');
    if (active) active.scrollIntoView({ block: 'center' });
  }

  async function syncQueue() {
    if (!live) { els.queue.textContent = '队列接口准备接入。'; return; }
    const token = ++queueToken;
    try {
      const data = await window.CelesteTransport.request('queue', { operation: 'get', offset: 0, limit: 50 });
      if (token !== queueToken) return;                 // 旧响应不盖新的
      queueRevision = data?.revision ?? queueRevision;
      const tracks = Array.isArray(data?.tracks) ? data.tracks : [];
      els.queue.replaceChildren();
      tracks.slice(0, 30).forEach(track => {
        const row = document.createElement('button');
        row.className = 'term-queue-row'; row.type = 'button';
        if (track.id === shell.getState().trackId) row.setAttribute('aria-current', 'true');
        const index = document.createElement('small'); index.textContent = String((track.index ?? 0) + 1).padStart(2, '0');
        const title = document.createElement('span'); title.textContent = track.title || '未知曲目';
        row.append(index, title);
        row.addEventListener('click', () => window.CelesteTransport.request('queue', { operation: 'play', revision: queueRevision, index: track.queueIndex ?? track.index ?? 0, trackId: track.id }).catch(error => shell.notify(error.message)));
        els.queue.append(row);
      });
    } catch { els.queue.textContent = '队列暂不可用。'; }
  }

  function applySnapshot(data) {
    const stereo = data?.stereo || {};
    if (Array.isArray(data?.bands)) {
      // 140 段按对数分组成 64 根柱子：高频挤在一起看不清，对数才像"频谱"
      const groups = 64, source = data.bands;
      const next = new Array(groups).fill(0);
      for (let i = 0; i < groups; i++) {
        const from = Math.floor(Math.pow(source.length, i / groups)), to = Math.max(from + 1, Math.floor(Math.pow(source.length, (i + 1) / groups)));
        let peak = 0;
        for (let j = from; j < to && j < source.length; j++) peak = Math.max(peak, Number(source[j]) || 0);
        next[i] = peak;
      }
      spectrumTarget = next;
    }
    const ok = !!stereo.available;
    els.spectrumStatus.textContent = data?.available ? 'LIVE · POST-DSP' : '当前输出暂无采样信号';
    if (ok) {
      scope.push({ left: stereo.left, right: stereo.right, generation: stereo.generation, available: true });
      const now = performance.now();
      // 峰值短暂保持（约 700ms 后开始回落），不做"永远停在最高点"的假峰值
      if (now - peakAt > 700 || stereo.peakLeft > peakLeft) { peakLeft = stereo.peakLeft; peakAt = now; }
      if (now - peakAt > 700 || stereo.peakRight > peakRight) { peakRight = stereo.peakRight; peakAt = now; }
      const parts = els.levelParts;
      parts.L.fill.style.transform = `scaleX(${Math.min(1, stereo.rmsLeft)})`;
      parts.R.fill.style.transform = `scaleX(${Math.min(1, stereo.rmsRight)})`;
      parts.L.peak.style.left = `${Math.min(100, peakLeft * 100)}%`;
      parts.R.peak.style.left = `${Math.min(100, peakRight * 100)}%`;
      parts.L.readout.textContent = `${db(stereo.rmsLeft)} / 峰值 ${db(stereo.peakLeft)}`;
      parts.R.readout.textContent = `${db(stereo.rmsRight)} / 峰值 ${db(stereo.peakRight)}`;
      els.phaseStats.textContent = `${scope.correlationText(stereo.correlation)} · ${scope.status} · ${stereo.count} 对 / ${stereo.sampleRate ? (stereo.sampleRate / 1000).toFixed(1) : '?'} kHz`;
    } else {
      scope.clear();
      els.phaseStats.textContent = '暂无采样 · 空网格';
      const parts = els.levelParts;
      ['L', 'R'].forEach(name => {
        parts[name].fill.style.transform = 'scaleX(0)';
        parts[name].peak.style.left = '0%';
        parts[name].readout.textContent = dash;
      });
    }
  }

  async function poll() {
    if (busy || !running) return;
    busy = true;
    const id = shell.getState().trackId;
    try {
      const data = await window.CelesteTransport.request('nowPlayingVisual', {});
      if (running && shell.getState().trackId === id) applySnapshot(data);
    } catch { /* 单次取不到就等下一拍，不弹错 */ }
    finally { busy = false; }
  }

  function draw(now) {
    frame = 0; if (!running) return;
    scope.render(now);
    if (spectrumShown.length !== spectrumTarget.length) spectrumShown = spectrumTarget.slice();
    const reduce = window.CelesteMotion && !window.CelesteMotion.canMove();
    for (let i = 0; i < spectrumTarget.length; i++) spectrumShown[i] = reduce ? spectrumTarget[i] : spectrumShown[i] + (spectrumTarget[i] - spectrumShown[i]) * 0.4;
    const bars = spectrumShown;
    els.spectrum.replaceChildren();
    for (let i = 0; i < 5; i++) els.spectrum.append(svgEl('line', { x1: 0, y1: i * 55 + 2, x2: 800, y2: i * 55 + 2, stroke: 'currentColor', 'stroke-opacity': '.12' }));
    if (bars.length) {
      const width = 800 / bars.length;
      bars.forEach((v, i) => {
        if (!(v > 0.002)) return;
        els.spectrum.append(svgEl('rect', { x: (i * width + 0.6).toFixed(1), y: (220 - v * 216).toFixed(1), width: Math.max(1, width - 1.2).toFixed(1), height: (v * 216).toFixed(1), fill: 'currentColor', 'fill-opacity': '.85' }));
      });
    }
    frame = requestAnimationFrame(draw);
  }

  async function sync() {
    if (!built) return;
    const state = shell.getState();
    els.state.textContent = state.playing ? 'PLAYING' : 'PAUSED';
    els.state.dataset.playing = String(!!state.playing);
    els.time.textContent = `${fmt(state.position)} / ${fmt(state.duration)}`;
    els.chain.textContent = [state.source, state.quality, state.outputDevice].filter(Boolean).join(' · ') || dash;
    if (state.trackId !== trackId) {
      trackId = state.trackId; facts = null; scope?.clear();
      spectrumTarget = []; spectrumShown = []; peakLeft = peakRight = 0;
      if (live && trackId) {
        try {
          const data = await window.CelesteTransport.request('nowPlaying', {});
          if (shell.getState().trackId === trackId) facts = data?.audio || null;
        } catch { facts = null; }
        syncQueue();
      }
    }
    syncFacts(state);
    syncLyrics(state);
    if (isActive() !== running) {
      running = isActive();
      els.root.hidden = page.dataset.playerTheme !== 'terminal';
      if (running) {
        moveControls(true);
        setStereo(true);
        if (!timer) { poll(); timer = setInterval(poll, 33); }   // 采样 30Hz，绘制交给 rAF
        if (!frame) frame = requestAnimationFrame(draw);
      } else {
        moveControls(false);
        setStereo(false);
        clearInterval(timer); timer = 0; cancelAnimationFrame(frame); frame = 0;
        scope?.clear();
      }
    }
    els.root.hidden = page.dataset.playerTheme !== 'terminal';
  }

  function observe() {
    const observer = new MutationObserver(() => {
      els.root.hidden = page.dataset.playerTheme !== 'terminal';
      sync();
    });
    observer.observe(page, { attributes: true, attributeFilter: ['data-player-theme', 'hidden'] });
  }

  document.addEventListener('celeste:state', sync);
  document.addEventListener('celeste:now-playing', sync);
  document.addEventListener('visibilitychange', sync);
  document.addEventListener('celeste:message', event => {
    const data = event.detail;
    if (data?.kind === 'celeste.visibility') { hostHidden = !!data.hidden; sync(); }
  });

  // 主题选项由 now-playing-themes.js 拥有；这里只知道"被切成 terminal 就接管"
  const start = () => { build(); observe(); sync(); };
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start); else start();

  window.CelesteTerminal = { sync };
})();
