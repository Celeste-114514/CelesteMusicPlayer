/* 背景资源来自受控目录。视频不进入音频播放链路。 */
(() => {
  'use strict';
  const root = document.documentElement;
  const layer = document.getElementById('bg-layer');
  if (!layer) return;
  const video = document.createElement('video');
  video.id = 'bg-video';
  video.muted = true;
  video.defaultMuted = true;
  video.loop = true;
  video.playsInline = true;
  video.preload = 'metadata';
  video.tabIndex = -1;
  video.setAttribute('aria-hidden', 'true');
  layer.append(video);
  let hostHidden = false, pageHidden = false, source = '', imageSource = '', imageProbe;
  let playbackAttempt = 0;

  function report(message = '') {
    root.dataset.bgState = message ? 'error' : 'ready';
    window.CelesteBackground.status = message;
    document.dispatchEvent(new CustomEvent('celeste:background-status', { detail: { message } }));
  }
  function assetUrl(value) {
    try {
      const url = new URL(value);
      return url.protocol === 'https:' && url.hostname === 'celeste-asset.internal'
        && !url.username && !url.password && !url.port ? url.href : '';
    } catch { return ''; }
  }
  async function syncPlayback() {
    const attempt = ++playbackAttempt;
    if (!source || root.dataset.bg !== 'video' || document.hidden || hostHidden || pageHidden) {
      video.pause();
      return;
    }
    try { await video.play(); }
    catch {
      if (attempt === playbackAttempt && source && !document.hidden && !hostHidden && !pageHidden)
        report('视频暂未播放：可点“播放视频背景”重试，或更换 MP4（H.264）/WebM 文件。');
    }
    if (document.hidden || hostHidden || pageHidden || root.dataset.bg !== 'video') video.pause();
  }
  function apply() {
    const style = getComputedStyle(root);
    const mode = style.getPropertyValue('--bg-mode').trim();
    root.dataset.bg = ['gradient', 'cover', 'image', 'video'].includes(mode) ? mode : 'solid';
    if (mode === 'cover') {
      const state = window.CelesteShell?.getState?.();
      const cover = state && typeof state.coverUrl === 'string' ? state.coverUrl : '';
      root.style.setProperty('--bg-image', cover ? 'url("' + cover.replace(/["\\\r\n]/g, '') + '")' : 'none');
    }
    const next = mode === 'video' ? assetUrl(style.getPropertyValue('--bg-video').trim()) : '';
    if (next !== source) {
      source = next;
      video.pause();
      if (source) video.src = source;
      else video.removeAttribute('src');
      video.load();
      report();
    }
    if (mode === 'video' && !source) report('尚未选择视频背景。');
    if (mode === 'image') {
      const cssUrl = style.getPropertyValue('--bg-image').trim();
      const match = /^url\(["']?(.*?)["']?\)$/.exec(cssUrl);
      const nextImage = assetUrl(match ? match[1] : '');
      if (nextImage !== imageSource) {
        imageSource = nextImage;
        report();
        if (nextImage) {
          const probe = imageProbe = new Image();
          probe.onerror = () => { if (imageProbe === probe && root.dataset.bg === 'image') report('背景图片无法读取，请重新选择图片。'); };
          probe.src = nextImage;
        }
      }
      if (!nextImage) report('尚未选择图片背景。');
    } else { imageSource = ''; imageProbe = null; if (mode !== 'video') report(); }
    syncPlayback();
  }
  video.addEventListener('playing', () => report());
  video.addEventListener('error', () => { if (source) report('视频无法解码或文件已丢失，请重新选择 MP4（H.264）或 WebM 视频。'); });
  document.addEventListener('visibilitychange', syncPlayback);
  window.addEventListener('pagehide', () => { pageHidden = true; syncPlayback(); });
  window.addEventListener('pageshow', () => { pageHidden = false; syncPlayback(); });
  document.addEventListener('celeste:theme-applied', apply);
  document.addEventListener('celeste:theme', apply);
  document.addEventListener('celeste:message', event => {
    const message = event.detail || {};
    if (message.kind === 'celeste.visibility') { hostHidden = !!message.hidden; syncPlayback(); }
    else if (message.kind === 'celeste.state' && root.dataset.bg === 'cover') apply();
  });
  window.CelesteBackground = { apply, resume: syncPlayback, status: '' };
  apply();
})();
