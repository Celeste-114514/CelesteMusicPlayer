/* 场景：标签排序全流程——分类墙（字段钮/切字段/分类卡/溢出加载更多/＋配置/分组浏览入口）
   → 钻取面板（曲目+列头排序+行播放 / 专辑钻取 / 艺术家钻取 / 排序方式 / 分组浏览）
   → 播放当前列表 → 返回墙 → 墙上进分组 → 回曲目视角收尾（now 高亮断言）。
   播放语义照原生：曲目行=不换队列（tagsortsong），面板播放全部/整组=换队列。 */
setTimeout(function(){
  window.__send({kind:'data', categories:__T.cats(), songs:[], total:0, cur:-1});
  __T.nav('TagSort');
  // 进页时墙还没有数据：显空态（S.tagsort.mode 还是上次的 Wall）
  // 分类墙首推：默认 5 个分类字段（原生 DefaultCategoryFields），按艺术家分 500 类只显示 200
  var F = [
    {key:'Artist', label:'艺术家', on:true},
    {key:'AlbumArtist', label:'专辑艺术家'},
    {key:'Album', label:'专辑'},
    {key:'Genre', label:'流派'},
    {key:'Year', label:'年份'}
  ];
  var wallCards = [];
  for (var i = 0; i < 400; i++) wallCards.push({name:'歌手 ' + i, n:3});
  window.__send({kind:'tagsortcats', field:'Artist', fields:F, cards:wallCards.slice(0, 200),
                 total:500, shown:200,
                 advice:'分类数量较多，仅显示部分卡片以避免内存占用过高。'});
  // 溢出条「加载全部剩余」：上报 tagsortmore，C# 重推（shown=400）
  document.getElementById('tsmorebtn').click();
  window.__send({kind:'tagsortcats', field:'Artist', fields:F, cards:wallCards,
                 total:500, shown:400,
                 advice:'分类数量较多，仅显示部分卡片以避免内存占用过高。'});
  // 切分类字段「流派」：上报 tagsortfield，C# 按新字段重推墙（3 个分类，无溢出条）
  document.querySelector('#tsbar .caps[data-k="Genre"]').click();
  var FG = F.map(function(f){ return {key:f.key, label:f.label, on:f.key === 'Genre'}; });
  var genreCards = [{name:'流行', n:3}, {name:'摇滚', n:1}, {name:'民谣', n:1}];
  window.__send({kind:'tagsortcats', field:'Genre', fields:FG, cards:genreCards,
                 total:3, shown:3, advice:''});

  /* ---------- 钻取面板：曲目视角 ---------- */
  document.querySelectorAll('#tsgrid .acard')[0].click();   // 分类卡「流行」→ tagsortopen
  var COLS = [
    {key:'Title', label:'标题', w:3, on:false, asc:true},
    {key:'Artist', label:'艺术家', w:2, on:true, asc:true},
    {key:'Album', label:'专辑', w:2, on:false, asc:true},
    {key:'Duration', label:'时长', w:1, on:false, asc:true}
  ];
  var panel = function(mode, title, extra){
    var m = {kind:'tagsortpanel', mode:mode, title:title, fields:FG};
    for (var k in extra) m[k] = extra[k];
    window.__send(m);
  };
  panel('Songs', '流派：流行', {
    cols: COLS,
    songs: [
      {n:1, cells:['歌曲 1', '歌手 0', '专辑 1', '3:00']},
      {n:2, cells:['歌曲 2', '歌手 1', '专辑 1', '3:01']},
      {n:3, cells:['歌曲 3', '歌手 2', '专辑 2', '3:02']}
    ]
  });
  // now 高亮：Songs 视角 index 按分类曲目算（第 2 行亮 ♪）
  window.__send({kind:'now', index:1, playing:true, position:30, duration:181, volume:0.7});
  // 列头「标题」：新列升序（on 从 Artist 移到 Title）
  document.querySelector('#tssongs .tshead .tsc[data-k="Title"]').click();
  var colsTitleAsc = COLS.map(function(c){
    return {key:c.key, label:c.label, w:c.w, on:c.key === 'Title', asc:true};
  });
  panel('Songs', '流派：流行', {
    cols: colsTitleAsc,
    songs: [
      {n:1, cells:['歌曲 1', '歌手 0', '专辑 1', '3:00']},
      {n:2, cells:['歌曲 2', '歌手 1', '专辑 1', '3:01']},
      {n:3, cells:['歌曲 3', '歌手 2', '专辑 2', '3:02']}
    ]
  });
  // 再点同一列：切降序（行序反转）
  document.querySelector('#tssongs .tshead .tsc[data-k="Title"]').click();
  var colsTitleDesc = colsTitleAsc.map(function(c){
    return {key:c.key, label:c.label, w:c.w, on:c.key === 'Title', asc:false};
  });
  panel('Songs', '流派：流行', {
    cols: colsTitleDesc,
    songs: [
      {n:1, cells:['歌曲 3', '歌手 2', '专辑 2', '3:02']},
      {n:2, cells:['歌曲 2', '歌手 1', '专辑 1', '3:01']},
      {n:3, cells:['歌曲 1', '歌手 0', '专辑 1', '3:00']}
    ]
  });
  // 曲目行：上报 tagsortsong（不换队列）
  document.querySelectorAll('#tssongs .tsrow')[0].click();

  /* ---------- 专辑视角 → 钻取到曲目 ---------- */
  document.querySelector('#tsmodes .caps[data-m="Albums"]').click();
  panel('Albums', '流派：流行', {
    grid: [{name:'专辑 1', n:2, sub:'Album'}, {name:'专辑 2', n:1, sub:'Album'}]
  });
  document.querySelectorAll('#tscards .acard')[0].click();   // → tagsortdrill name=专辑 1 sub=Album
  panel('Songs', '专辑：专辑 1', {
    cols: COLS,
    songs: [
      {n:1, cells:['歌曲 1', '歌手 0', '专辑 1', '3:00']},
      {n:2, cells:['歌曲 2', '歌手 1', '专辑 1', '3:01']}
    ]
  });

  /* ---------- 艺术家视角 → 钻取到曲目 ---------- */
  document.querySelector('#tsmodes .caps[data-m="Artists"]').click();
  panel('Artists', '流派：流行', {
    grid: [{name:'歌手 0', n:1, sub:'Artist'}, {name:'歌手 1', n:1, sub:'Artist'},
           {name:'歌手 2', n:1, sub:'Artist'}]
  });
  document.querySelectorAll('#tscards .acard')[1].click();   // → tagsortdrill name=歌手 1 sub=Artist
  panel('Songs', '艺术家：歌手 1', {
    cols: COLS,
    songs: [{n:1, cells:['歌曲 2', '歌手 1', '专辑 1', '3:01']}]
  });

  /* ---------- 排序方式视角 ---------- */
  document.querySelector('#tsmodes .caps[data-m="Sort"]').click();
  var presets = function(onLabel){
    return ['专辑', '专辑艺术家 / 专辑', '专辑艺术家 / 年份 / 专辑',
            '艺术家 / 专辑', '流派 / 专辑', '年份 / 专辑'].map(function(l){
      return {label:l, on:l === onLabel};
    });
  };
  panel('Sort', '流派：流行', {
    sort: {presets: presets('年份 / 专辑'), asc: true,
           status: '当前排序依据：年份 / 专辑'}
  });
  // 点预设「专辑」：上报 tagsortsort，C# 重推（高亮移动 + 状态文本）
  document.querySelector('#tspresets .caps[data-p="专辑"]').click();
  panel('Sort', '流派：流行', {
    sort: {presets: presets('专辑'), asc: true, status: '当前排序依据：专辑'}
  });
  // 降序：上报 tagsortorder asc=false
  document.getElementById('tsdesc2').click();
  panel('Sort', '流派：流行', {
    sort: {presets: presets('专辑'), asc: false, status: '当前排序依据：专辑'}
  });
  // 自定义排序…：开原生配置窗
  document.getElementById('tscustom').click();

  /* ---------- 分组浏览视角 ---------- */
  document.querySelector('#tsmodes .caps[data-m="GroupBy"]').click();
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
  /* 分组行（Artist,Album 两级：艺术家 → 专辑 → 歌曲；openSet 控展开态） */
  var gRows = function(openSet){
    var rows = [];
    rows.push({h:1, path:'0', value:'歌手 0', label:'艺术家', n:2, depth:0, open:!!openSet['0']});
    if (openSet['0']){
      rows.push({h:1, path:'0.0', value:'专辑 1', label:'专辑', n:2, depth:1, open:true});
      rows.push({h:0, path:'0.0', group:'专辑 1', depth:2, title:'歌曲 1', artist:'歌手 0', dur:'3:00', file:'D:\\音乐\\a.flac'});
      rows.push({h:0, path:'0.0', group:'专辑 1', depth:2, title:'歌曲 2', artist:'歌手 0', dur:'3:01', file:'D:\\音乐\\b.flac'});
    }
    rows.push({h:1, path:'1', value:'歌手 1', label:'艺术家', n:1, depth:0, open:!!openSet['1']});
    if (openSet['1']){
      rows.push({h:1, path:'1.0', value:'专辑 1', label:'专辑', n:1, depth:1, open:true});
      rows.push({h:0, path:'1.0', group:'专辑 1', depth:2, title:'歌曲 3', artist:'歌手 1', dur:'3:02', file:'D:\\音乐\\c.flac'});
    }
    return rows;
  };
  var allOpen = {'0':true, '1':true};
  panel('GroupBy', '分组浏览', {
    group: {presets: gPresets('Artist,Album'), rows: gRows(allOpen)}
  });
  // 组头点击：折叠（上报 tagsorttoggle path=0）
  document.querySelectorAll('#tsgrows .tgh')[0].click();
  panel('GroupBy', '分组浏览', {
    group: {presets: gPresets('Artist,Album'), rows: gRows({'0':false, '1':true})}
  });
  // 再点：展开
  document.querySelectorAll('#tsgrows .tgh')[0].click();
  panel('GroupBy', '分组浏览', {
    group: {presets: gPresets('Artist,Album'), rows: gRows(allOpen)}
  });
  // 全部折叠 / 全部展开
  document.getElementById('tsgcol').click();
  panel('GroupBy', '分组浏览', {
    group: {presets: gPresets('Artist,Album'), rows: gRows({})}
  });
  document.getElementById('tsgexp').click();
  panel('GroupBy', '分组浏览', {
    group: {presets: gPresets('Artist,Album'), rows: gRows(allOpen)}
  });
  // 组头行尾播放钮：播整组（不触发展折）
  document.querySelectorAll('#tsgrows .tgh')[0].querySelector('.gplay').click();
  // 组内歌曲行：按末级字段过滤整库从该首开始
  document.querySelectorAll('#tsgrows .tgr')[0].click();
  // 分组预设下拉切「艺术家 / 专辑 / 年份」：上报 tagsortgroup
  var gsel = document.getElementById('tsgsel');
  gsel.value = 'Artist,Album,Year';
  gsel.dispatchEvent(new Event('change', {bubbles:true}));
  panel('GroupBy', '分组浏览', {
    group: {
      presets: gPresets('Artist,Album,Year'),
      rows: [
        {h:1, path:'0', value:'歌手 0', label:'艺术家', n:2, depth:0, open:true},
        {h:1, path:'0.0', value:'专辑 1', label:'专辑', n:2, depth:1, open:true},
        {h:1, path:'0.0.0', value:'2020', label:'年份', n:2, depth:2, open:true},
        {h:0, path:'0.0.0', group:'2020', depth:3, title:'歌曲 1', artist:'歌手 0', dur:'3:00', file:'D:\\音乐\\a.flac'},
        {h:0, path:'0.0.0', group:'2020', depth:3, title:'歌曲 2', artist:'歌手 0', dur:'3:01', file:'D:\\音乐\\b.flac'},
        {h:1, path:'1', value:'歌手 1', label:'艺术家', n:1, depth:0, open:true},
        {h:1, path:'1.0', value:'专辑 1', label:'专辑', n:1, depth:1, open:true},
        {h:1, path:'1.0.0', value:'2021', label:'年份', n:1, depth:2, open:true},
        {h:0, path:'1.0.0', group:'2021', depth:3, title:'歌曲 3', artist:'歌手 1', dur:'3:02', file:'D:\\音乐\\c.flac'}
      ]
    }
  });

  /* ---------- 播放当前列表 → 返回墙 ---------- */
  document.getElementById('tspPlay').click();
  document.getElementById('tsback').click();
  window.__send({kind:'tagsortcats', field:'Genre', fields:FG, cards:genreCards,
                 total:3, shown:3, advice:''});

  /* ---------- 墙上：＋ 配置字段 / 「分组浏览」入口（分组字段=当前分类字段） ---------- */
  document.getElementById('tsplus').click();           // → tagsortcustom what=fields
  document.getElementById('tsgroupbtn').click();        // → tagsortmode mode=GroupBy
  panel('GroupBy', '分组浏览', {
    group: {
      presets: gPresets('__custom__'),   // Genre 不命中任何预设 → 高亮「自定义（已保存）」
      rows: [
        {h:1, path:'0', value:'流行', label:'流派', n:2, depth:0, open:true},
        {h:0, path:'0', group:'流行', depth:1, title:'歌曲 1', artist:'歌手 0', dur:'3:00', file:'D:\\音乐\\a.flac'},
        {h:0, path:'0', group:'流行', depth:1, title:'歌曲 2', artist:'歌手 1', dur:'3:01', file:'D:\\音乐\\b.flac'},
        {h:1, path:'1', value:'摇滚', label:'流派', n:1, depth:0, open:true},
        {h:0, path:'1', group:'摇滚', depth:1, title:'歌曲 3', artist:'歌手 2', dur:'3:02', file:'D:\\音乐\\c.flac'}
      ]
    }
  });

  /* ---------- 收尾：回曲目视角，now 高亮第 3 行 ---------- */
  document.querySelector('#tsmodes .caps[data-m="Songs"]').click();
  panel('Songs', '流派：流行', {
    cols: COLS,
    songs: [
      {n:1, cells:['歌曲 1', '歌手 0', '专辑 1', '3:00']},
      {n:2, cells:['歌曲 2', '歌手 1', '专辑 1', '3:01']},
      {n:3, cells:['歌曲 3', '歌手 2', '专辑 2', '3:02']}
    ]
  });
  window.__send({kind:'now', index:2, playing:true, position:60, duration:182, volume:0.7});
  __T.dump('tagsort');
}, 200);
