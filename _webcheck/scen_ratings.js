/* 场景：评分（星级胶囊行 + 点胶囊上报 rating） */
setTimeout(function(){
  __T.nav('Ratings');
  var songs = __T.songs(3, {album:'专辑 1', artist:'歌手 0'});
  __T.pushData(songs, {Songs:12, Ratings:3, Albums:4});
  __T.now(0);
  /* 点 ★3 胶囊：应立即高亮 + 上报 rating，然后 C# 按星级重推（这里模拟） */
  var caps = document.querySelectorAll('#ratingbar .caps');
  if (caps.length === 6 && caps[3].getAttribute('data-v') === '3') caps[3].click();
  __T.pushData(songs.slice(0, 1), {Songs:12, Ratings:1, Albums:4});
  __T.dump('ratings');
}, 200);
