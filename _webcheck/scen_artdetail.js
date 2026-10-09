/* 场景：艺术家详情（左栏圆形大头像+添加至播放队列 / 中栏歌曲 / 右栏该艺术家的专辑 132px 卡）
   + 网络头像下载完成（artistavatar）更新左栏大头像 */
setTimeout(function(){
  __T.nav('Artists');
  __T.pushArtists(4);
  /* 预热批：带 i 按下标更新墙卡 */
  window.__send({kind:'artistcovers', mode:'artist', items:[
    {i:0, name:'歌手 0', cover:window.covS(0)},
    {i:1, name:'歌手 1', cover:window.covS(1)}
  ]});
  __T.artistDetail();
  /* 网络头像后到：只更新正在看的详情大头像 */
  window.__send({kind:'artistavatar', cover:window.covS(9)});
  var btn = document.getElementById('btnEnqueue');
  if (btn) btn.click();
  __T.dump('artistdetail');
}, 300);
