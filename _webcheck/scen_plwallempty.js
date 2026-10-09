/* 场景：播放列表空态 + 新建——空墙（加载完还没有单）/ 点「新建播放列表」上报 plnew /
   C# 推空单详情（0 首，显示「这个播放列表还是空的」）/ 失败消息 plmsg 显示在顶栏下方 */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats({PlaylistWall:0}), songs:[], total:0, cur:-1});
  __T.nav('PlaylistWall');
  // 空墙：C# 推 plwall（empty=true，一个单都没有）
  window.__send({kind:'plwall', empty:true, back:false, items:[]});
  // 点墙头「＋ 新建播放列表」：上报 plnew（C# 建默认名去重的单后推 pldetail）
  document.getElementById('btnPlNew').click();
  // C# 推新单详情（空单）
  window.__send({kind:'pldetail', name:'新建播放列表', songs:[]});
  // 失败消息（这里模拟重命名重名被拒）：显示在顶栏下方，输入框场景由 plwall 场景覆盖
  window.__send({kind:'plmsg', text:'重命名失败：这个名字可能已被占用'});
  __T.dump('plwallempty');
}, 200);
