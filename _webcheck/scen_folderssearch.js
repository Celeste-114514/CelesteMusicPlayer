/* 场景：媒体库搜索筛选 + 重复点已展开文件夹会重新请求歌曲
   （原生每次点文件夹都重新加载右栏；网页点行=选中+展开+加载，不走缓存） */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  __T.nav('Folders');
  window.__send({kind:'folderroots', empty:false, roots:[
    {name:'D:\\音乐\\华语', path:'D:\\音乐\\华语', isFolder:true}
  ]});
  document.querySelectorAll('#foldertree .fnode')[0].click();
  window.__send({kind:'folderchildren', path:'D:\\音乐\\华语', items:[]});
  var songs = [
    {t:'01 - 晴天.flac', sub:'周杰伦 · 叶惠美', dur:269, fav:false, path:'D:\\音乐\\华语\\01 - 晴天.flac'},
    {t:'02 - 七里香.flac', sub:'周杰伦 · 七里香', dur:299, fav:true, path:'D:\\音乐\\华语\\02 - 七里香.flac'}
  ];
  window.__send({kind:'foldersongs', path:'D:\\音乐\\华语', header:'D:\\音乐\\华语', songs:songs});
  // 再点一次已展开的根（点行、不是箭头）：应再次上报 folder 重load右栏
  document.querySelectorAll('#foldertree .fnode')[0].click();
  // C# 重新枚举完再推一遍（模拟第二次响应）
  window.__send({kind:'foldersongs', path:'D:\\音乐\\华语', header:'D:\\音乐\\华语', songs:songs});
  // 搜索框筛文件名（原生 Folders 分类同口径），等 120ms 防抖再 dump
  var q = document.getElementById('q');
  q.value = '七里香';
  q.dispatchEvent(new Event('input', {bubbles:true}));
  setTimeout(function(){ __T.dump('folderssearch'); }, 300);
}, 200);
