/* 场景：媒体库空态——没配置媒体库目录（folderroots 空列表，
   原生退「请选择文件夹」提示；页头副标题 = 未配置） */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  __T.nav('Folders');
  window.__send({kind:'folderroots', empty:true, roots:[]});
  __T.dump('foldersempty');
}, 200);
