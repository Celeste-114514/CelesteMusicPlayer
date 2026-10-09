/* 场景：媒体库加载中——点根后、C# 还没推歌之前，右栏先显示「加载中…」
   （大文件夹递归枚举+读标签慢，不能白屏） */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  __T.nav('Folders');
  window.__send({kind:'folderroots', empty:false, roots:[
    {name:'D:\\音乐', path:'D:\\音乐', isFolder:true}
  ]});
  document.querySelectorAll('#foldertree .fnode')[0].click();
  __T.dump('foldersload');
}, 200);
