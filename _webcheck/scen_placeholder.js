/* 场景：没做的面板（网络音乐库）→ 占位页（nav ok:false）。
   用户 2026-10-09 说要参考 ECHO 扩展其他源类型、暂时不动，入口保留。
   先推一份 data（真实流程里 ready 之后分类早就推过来了，占位页靠它拿中文标签）。 */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  window.__send({kind:'nav', id:'WebDav', ok:false});
  __T.dump('placeholder');
}, 200);
