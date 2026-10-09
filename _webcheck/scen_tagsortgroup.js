/* 场景：标签排序「分组浏览」视角——两级/三级树、缩进 depth×22、组头折叠/展开、
   行尾播放钮不触发展折、组内歌曲上报、预设下拉切三级、全部展开/折叠后收尾在折叠态。 */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  __T.nav('TagSort');
  var F = [
    {key:'Artist', label:'艺术家', on:true},
    {key:'AlbumArtist', label:'专辑艺术家'},
    {key:'Album', label:'专辑'},
    {key:'Genre', label:'流派'},
    {key:'Year', label:'年份'}
  ];
  window.__send({kind:'tagsortcats', field:'Artist', fields:F,
                 cards:[{name:'歌手 0', n:2}, {name:'歌手 1', n:1}], total:2, shown:2, advice:''});
  document.querySelectorAll('#tsgrid .acard')[0].click();
  window.__send({kind:'tagsortpanel', mode:'Songs', title:'艺术家：歌手 0', fields:F,
    cols:[{key:'Title', label:'标题', w:3, on:false, asc:true}],
    songs:[{n:1, cells:['歌曲 1']}, {n:2, cells:['歌曲 2']}]});

  var gPresets = function(onTag){
    return [
      {tag:'__custom__', label:'自定义（已保存）'},
      {tag:'Artist,Album', label:'艺术家 / 专辑'},
      {tag:'Artist,Album,Year', label:'艺术家 / 专辑 / 年份'},
      {tag:'Artist,Album,Title', label:'艺术家 / 专辑 / 标题'},
      {tag:'Album,Year', label:'专辑 / 年份'},
      {tag:'Genre,Artist', label:'流派 / 艺术家'},
      {tag:'Year,Album', label:'年份 / 专辑'},
      {tag:'Format,DepthRate', label:'格式 / 位深采样率'}
    ].map(function(p){ return {tag:p.tag, label:p.label, on:p.tag === onTag}; });
  };
  var song = function(path, group, title, dur, file, artist){
    // 歌曲缩进 = 末级组头 depth+1；组头 depth = 路径段数-1，故歌曲 depth = 路径段数
    return {h:0, path:path, group:group, depth:path.split('.').length,
            title:title, artist:artist || '歌手 0', dur:dur, file:file};
  };
  /* 两级树（Artist,Album）：歌手 0 → 专辑 1 → 两首；歌手 1 → 专辑 1 → 一首 */
  var rows2 = function(open0){
    var rows = [{h:1, path:'0', value:'歌手 0', label:'艺术家', n:2, depth:0, open:open0}];
    if (open0){
      rows.push({h:1, path:'0.0', value:'专辑 1', label:'专辑', n:2, depth:1, open:true});
      rows.push(song('0.0', '专辑 1', '歌曲 1', '3:00', 'D:\\音乐\\a.flac', '歌手 0'));
      rows.push(song('0.0', '专辑 1', '歌曲 2', '3:01', 'D:\\音乐\\b.flac', '歌手 0'));
    }
    rows.push({h:1, path:'1', value:'歌手 1', label:'艺术家', n:1, depth:0, open:true});
    rows.push({h:1, path:'1.0', value:'专辑 1', label:'专辑', n:1, depth:1, open:true});
    rows.push(song('1.0', '专辑 1', '歌曲 3', '3:02', 'D:\\音乐\\c.flac', '歌手 1'));
    return rows;
  };
  /* 三级树（Artist,Album,Year）：歌手 0 → 专辑 1 → 2020 → 两首；歌手 1 → … → 2021 → 一首 */
  var rows3 = function(open0){
    var rows = [{h:1, path:'0', value:'歌手 0', label:'艺术家', n:2, depth:0, open:open0}];
    if (open0){
      rows.push({h:1, path:'0.0', value:'专辑 1', label:'专辑', n:2, depth:1, open:true});
      rows.push({h:1, path:'0.0.0', value:'2020', label:'年份', n:2, depth:2, open:true});
      rows.push(song('0.0.0', '2020', '歌曲 1', '3:00', 'D:\\音乐\\a.flac', '歌手 0'));
      rows.push(song('0.0.0', '2020', '歌曲 2', '3:01', 'D:\\音乐\\b.flac', '歌手 0'));
    }
    rows.push({h:1, path:'1', value:'歌手 1', label:'艺术家', n:1, depth:0, open:true});
    rows.push({h:1, path:'1.0', value:'专辑 1', label:'专辑', n:1, depth:1, open:true});
    rows.push({h:1, path:'1.0.0', value:'2021', label:'年份', n:1, depth:2, open:true});
    rows.push(song('1.0.0', '2021', '歌曲 3', '3:02', 'D:\\音乐\\c.flac', '歌手 1'));
    return rows;
  };
  var panel = function(rows, onTag){
    window.__send({kind:'tagsortpanel', mode:'GroupBy', title:'分组浏览', fields:F,
                   group:{presets: gPresets(onTag), rows: rows}});
  };

  document.querySelector('#tsmodes .caps[data-m="GroupBy"]').click();
  panel(rows2(true), 'Artist,Album');
  // 组头点击：折叠歌手 0（子树整体隐藏）
  document.querySelectorAll('#tsgrows .tgh')[0].click();
  panel(rows2(false), 'Artist,Album');
  // 再点：展开
  document.querySelectorAll('#tsgrows .tgh')[0].click();
  panel(rows2(true), 'Artist,Album');
  // 全部折叠 / 全部展开（整树只剩组头）
  document.getElementById('tsgcol').click();
  panel([{h:1, path:'0', value:'歌手 0', label:'艺术家', n:2, depth:0, open:false},
         {h:1, path:'1', value:'歌手 1', label:'艺术家', n:1, depth:0, open:false}], 'Artist,Album');
  document.getElementById('tsgexp').click();
  panel(rows2(true), 'Artist,Album');
  // 组头行尾播放钮：播整组，不触发展折（fold 后子树还在）
  document.querySelectorAll('#tsgrows .tgh')[0].querySelector('.gplay').click();
  // 组内歌曲行：按末级字段过滤整库从该首开始（path+group+file）
  document.querySelectorAll('#tsgrows .tgr')[0].click();
  // 预设下拉切「艺术家 / 专辑 / 年份」：三级树
  var gsel = document.getElementById('tsgsel');
  gsel.value = 'Artist,Album,Year';
  gsel.dispatchEvent(new Event('change', {bubbles:true}));
  panel(rows3(true), 'Artist,Album,Year');
  // 收尾：折叠歌手 0（三级树第一层收起，二级/三级行跟着隐藏）
  document.querySelectorAll('#tsgrows .tgh')[0].click();
  panel(rows3(false), 'Artist,Album,Year');
  __T.dump('tagsortgroup');
}, 200);
