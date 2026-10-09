/* 场景：播放列表全流程——墙（先进加载中→卡片→封面回填）→ 点卡进详情 →
   行播放（整单替换从该首）/ 整单入队 / 重命名（原地输入框）/ 删除两级确认 → 回墙。
   now 消息的 path 高亮：index 故意给个不匹配的值，验证详情行按路径亮而不是按 index。 */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats({PlaylistWall:3}), songs:[], total:0, cur:-1});
  __T.nav('PlaylistWall');
  // 墙数据还没到（C# 正在查 SQLite）：先记下加载态，最后一起断言
  window.__plLoadingEmpty = (document.querySelector('#plgrid .empty') || {}).textContent || null;
  window.__plLoadingSub = document.getElementById('pgSub').textContent;
  // C# 推墙（原生 ApplyPlaylistWallCategory 口径：内建「我喜欢的音乐」不过墙）
  window.__send({kind:'plwall', empty:false, back:false, items:[
    {name:'华语经典', n:2, cover:''},
    {name:'睡前纯音乐', n:1, cover:''},
    {name:'空单测试', n:0, cover:''}
  ]});
  // 封面预热按批补（plcovers 按下标回填；i:2 没封面 = 首字符兜底）
  window.__send({kind:'plcovers', items:[
    {i:0, cover:window.covS(0)},
    {i:1, cover:window.covS(1)}
  ]});
  // 点第一张卡：上报 plopen
  document.querySelectorAll('#plgrid .acard')[0].click();
  // C# 推详情（单内有序曲目，行序即单内顺序）
  window.__send({kind:'pldetail', name:'华语经典', songs:[
    {t:'晴天', sub:'周杰伦 · 叶惠美', dur:269, fav:false, path:'D:\\音乐\\a.flac'},
    {t:'七里香', sub:'周杰伦 · 七里香', dur:299, fav:true, path:'D:\\音乐\\b.flac'}
  ]});
  // now：index=5 是歌曲快照下标（对不上详情行），path 才该驱动高亮
  __T.now(5, null, 'D:\\音乐\\b.flac');
  // 点第 2 行：上报 plplay（整单替换队列、从该首起）
  document.querySelectorAll('#plrows .plrow')[1].click();
  // 顶栏「添加至播放队列」：上报 plqueue
  document.getElementById('btnPlQueue').click();
  // 重命名：点「重命名」→ 标题原地变输入框 → 改名字 → 确定
  document.getElementById('btnPlRename').click();
  document.getElementById('plnameInput').value = '华语经典2';
  document.getElementById('plnameOk').click();
  // C# 落库后推 plrenamed：标题换新名字
  window.__send({kind:'plrenamed', old:'华语经典', name:'华语经典2'});
  // 删除两级确认：第一下只进待确认态（按钮变「确定删除？」），不上报
  document.getElementById('btnPlDel').click();
  // 第二下真删：上报 pldel
  document.getElementById('btnPlDel').click();
  // C# 删完推 plwall（back=true）：回墙，被删的单不见了
  window.__send({kind:'plwall', empty:false, back:true, items:[
    {name:'睡前纯音乐', n:1, cover:''},
    {name:'空单测试', n:0, cover:''}
  ]});
  // 回墙后 C# 重新预热封面（plcovers 按新下标补）——截图要能看到封面
  window.__send({kind:'plcovers', items:[
    {i:0, cover:window.covS(4)},
    {i:1, cover:window.covS(5)}
  ]});
  var st = __T.dump('plwall');
  // 把加载态快照补进去（dump 时元素已被最终状态覆盖，单独带回）
  st.plLoadingEmpty = window.__plLoadingEmpty;
  st.plLoadingSub = window.__plLoadingSub;
  document.getElementById('__statedump').textContent
    = '/*STATE*/' + JSON.stringify(st) + '/*END*/';
}, 200);
