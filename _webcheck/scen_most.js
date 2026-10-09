/* 场景：播放最多 */
setTimeout(function(){
  __T.nav('MostPlayed');
  var songs = __T.songs(7, {album:'专辑 4', artist:'歌手 2'});
  __T.pushData(songs, {Songs:12, MostPlayed:7, Albums:4});
  __T.now(3);
  __T.dump('mostplayed');
}, 200);
