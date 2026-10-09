/* 场景：播放队列 */
setTimeout(function(){
  __T.nav('UserPlaylist');
  var songs = __T.songs(6, {album:'专辑 2', artist:'歌手 1'});
  songs[1].fav = true;
  __T.pushData(songs, {Songs:12, UserPlaylist:6, Albums:4});
  __T.now(1);
  __T.dump('queue');
}, 200);
