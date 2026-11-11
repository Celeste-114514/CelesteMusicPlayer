// 网页壳入口：把唱片室做成覆盖整个客户区的一层，按需创建、可关闭。
import { startRoom } from './main.js';
import { buildRoomData } from './shell-data.js';

let active = null;

function open() {
  if (active) return active;
  const shell = window.CelesteShell;
  if (!shell) { console.warn('[music-room] CelesteShell 还没就绪'); return null; }

  let data;
  try { data = buildRoomData(shell); }
  catch (err) { console.error('[music-room] 曲库数据构造失败', err); return null; }
  if (!data.artists.length) {
    shell.notify?.('曲库里还没有专辑，先添加音乐再来唱片室。');
    return null;
  }

  const ctrl = startRoom(data, {
    mode: 'shell',
    onPlay(album) {
      if (!album || !album.trackIds || !album.trackIds.length) return;
      shell.playTracks(album.trackIds);
    },
    onControl(command, album) {
      const state = shell.getState();
      if (command === 'togglePlay' && !album.trackIds.includes(state.trackId)) shell.playTracks(album.trackIds);
      else shell.send(command);
    },
    onClose: () => close(),
  });
  active = ctrl;
  document.documentElement.classList.add('rr-open');

  // 播放状态驱动盘片转：只认"属于这张专辑的曲目正在播"
  const onState = ev => {
    const st = ev.detail || {};
    ctrl.setPlaying(!!st.playing, st.trackId);
  };
  document.addEventListener('celeste:state', onState);
  onState({ detail: shell.getState() });
  ctrl._offState = () => document.removeEventListener('celeste:state', onState);
  return ctrl;
}

function close() {
  if (!active) return;
  active._offState?.();
  active.destroy();
  active = null;
  document.documentElement.classList.remove('rr-open');
}

function boot() {
  const btn = document.getElementById('open-music-room');
  if (btn) btn.addEventListener('click', () => open());
}

if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', boot);
else boot();

window.CelesteMusicRoom = { open, close, isOpen: () => !!active, back: () => active?.back(), forward: () => active?.forward() };
