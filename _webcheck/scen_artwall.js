/* 场景：艺术家墙 + 网络头像只带 name 的补推（防下标张冠李戴） */
setTimeout(function(){
  __T.nav('Artists');
  __T.pushArtists(4);
  /* 只带 name（没有 i）：网页按名字匹配更新卡片 */
  window.__send({kind:'artistcovers', mode:'artist', items:[
    {name:'歌手 1', cover:window.covS(5)},
    {name:'歌手 3', cover:window.covS(7)}
  ]});
  __T.dump('artwall');
}, 300);
