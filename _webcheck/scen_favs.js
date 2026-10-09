/* 场景：我喜欢的音乐（行样式同歌曲面板，收藏心默认实心） */
setTimeout(function(){
  __T.nav('Favorites');
  var songs = __T.songs(5, {album:'专辑 2', artist:'歌手 1'});
  for (var i = 0; i < songs.length; i++) songs[i].fav = true;
  __T.pushData(songs, {Songs:12, Favorites:5, Albums:4});
  __T.now(0);
  /* 再点第 3 行的心：应上报 love（C# 在收藏面板会整表重推） */
  var heart = document.querySelectorAll('#rows .row .fav')[2];
  if (heart) heart.click();
  __T.dump('favorites');
}, 200);
