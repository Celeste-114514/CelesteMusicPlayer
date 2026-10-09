/* 场景：播放列表详情态收尾——行内容/收藏心/now 按 path 高亮/重命名输入框交互/plmsg。
   （plwall 场景一路走到删完回墙；这个场景停在详情中，验详情页自身的状态） */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats({PlaylistWall:2}), songs:[], total:0, cur:-1});
  __T.nav('PlaylistWall');
  window.__send({kind:'plwall', empty:false, back:false, items:[
    {name:'华语经典', n:2, cover:window.covS(3)},
    {name:'睡前纯音乐', n:1, cover:''}
  ]});
  document.querySelectorAll('#plgrid .acard')[0].click();
  window.__send({kind:'pldetail', name:'华语经典', songs:[
    {t:'晴天', sub:'周杰伦 · 叶惠美', dur:269, fav:false, path:'D:\\音乐\\a.flac'},
    {t:'七里香', sub:'周杰伦 · 七里香', dur:299, fav:true, path:'D:\\音乐\\b.flac'}
  ]});
  // now：index 故意给不匹配的值，验证按 path 高亮
  __T.now(5, null, 'D:\\音乐\\b.flac');
  // 重命名：开输入框 → 改名 → 确定（C# 推 plrenamed）
  document.getElementById('btnPlRename').click();
  document.getElementById('plnameInput').value = '华语经典2';
  document.getElementById('plnameOk').click();
  window.__send({kind:'plrenamed', old:'华语经典', name:'华语经典2'});
  // 删除第一下（待确认态，不上报）后取消态由 3 秒计时器自然收起——不断言中间态
  document.getElementById('btnPlDel').click();
  // plmsg：失败提示显示在顶栏下方
  window.__send({kind:'plmsg', text:'重命名失败：这个名字可能已被占用'});
  __T.dump('plwalldetail');
}, 200);
