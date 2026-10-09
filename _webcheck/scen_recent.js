/* 场景：最近播放（行小字换成「播放于… · 播放… · 播完」，原生口径） */
setTimeout(function(){
  __T.nav('Recent');
  var songs = __T.songs(4, {album:'专辑 3', artist:'歌手 2'});
  songs[0].sub = '播放于 10-09 14:30 · 播放 3:45 · 播完';
  songs[1].sub = '播放于 10-08 09:12 · 播放 45 秒 · 未播完';
  songs[2].sub = '播放于 10-07 22:03 · 播放 12:01 · 播完';
  songs[3].sub = '播放于 — · 播放 — · 未播完';
  __T.pushData(songs, {Songs:12, Recent:4, Albums:4});
  __T.now(0);
  __T.dump('recent');
}, 200);
