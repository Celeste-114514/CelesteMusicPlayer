/* 场景：专辑详情（左栏大封面+质量行+DSD 提示+添加至播放队列；右栏音轨号表头 + CD 分组） */
setTimeout(function(){
  __T.nav('Albums');
  __T.pushAlbums(6);
  /* 碟号序列 1,1,2,2,0 —— 验证 CD1/CD2/CD? 三个分组头 */
  __T.pushAlbumDetail([1, 1, 2, 2, 0], {tech:'FLAC | 24bit/96kHz | 2.3 Mbps | 25:00', dsd:true});
  /* 点「添加至播放队列」：应上报 enqueue */
  var btn = document.getElementById('btnEnqueue');
  if (btn) btn.click();
  __T.dump('albumdetail');
}, 300);
