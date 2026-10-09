/* 场景：歌曲面板（基线回归） */
setTimeout(function(){
  __T.nav('Songs');
  var songs = __T.songs(12, {album:'专辑 1', artist:'歌手 0'});
  songs[2].dsd = true;
  for (var i = 0; i < 3; i++) songs[i].fav = true;
  __T.pushData(songs, {Songs:12, Albums:4, Artists:3});
  __T.now(2);
  __T.dump('songs');
}, 200);
