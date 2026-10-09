/* 场景：媒体库（文件夹浏览）全流程——根行完整路径/展开一层/右栏歌曲/
   行点击播放/文件节点播放/箭头折叠只收树不动右栏 */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  __T.nav('Folders');
  // 根目录（原生 RefreshFolderBrowserRoots 口径：根行显示完整路径）
  window.__send({kind:'folderroots', empty:false, roots:[
    {name:'D:\\音乐\\华语', path:'D:\\音乐\\华语', isFolder:true},
    {name:'D:\\音乐\\欧美', path:'D:\\音乐\\欧美', isFolder:true}
  ]});
  // 点第一个根：上报 folder，右栏先显示加载中，该行选中
  document.querySelectorAll('#foldertree .fnode')[0].click();
  // C# 推一层子项（原生排序：子文件夹在前、音频文件在后）
  window.__send({kind:'folderchildren', path:'D:\\音乐\\华语', items:[
    {name:'周杰伦', path:'D:\\音乐\\华语\\周杰伦', isFolder:true},
    {name:'01 - 晴天.flac', path:'D:\\音乐\\华语\\01 - 晴天.flac', isFolder:false}
  ]});
  // C# 推右栏歌曲（原生行口径：序号/文件名/艺术家·专辑/时长）
  window.__send({kind:'foldersongs', path:'D:\\音乐\\华语', header:'D:\\音乐\\华语', songs:[
    {t:'01 - 晴天.flac', sub:'周杰伦 · 叶惠美', dur:269, fav:false, path:'D:\\音乐\\华语\\01 - 晴天.flac'},
    {t:'02 - 七里香.flac', sub:'周杰伦 · 七里香', dur:299, fav:true, path:'D:\\音乐\\华语\\02 - 七里香.flac'}
  ]});
  // 点右栏第 2 行：上报 folderplay（原生双击口径，网页单击；不换队列）
  document.querySelectorAll('#frows .frow')[1].click();
  // 点左树里的文件节点（第 3 个 .fnode）：也上报 folderplay
  document.querySelectorAll('#foldertree .fnode')[2].click();
  // 折叠：点已展开根的箭头——树收起，右栏保持（原生收起箭头不动右栏）
  document.querySelectorAll('#foldertree .fnode')[0].querySelector('.chev').click();
  __T.dump('folders');
}, 200);
