/* 场景：标签排序「排序方式」视角——预设切换/升降序/自定义状态文本。
   守卫口径：点已激活的升降序钮不再上报（原生点已选中项结果相同，幂等）；
   自定义排序链的状态文本照 WriteTagSortStatus：「当前排序依据：预设（字段↑ → 字段↓）」。 */
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
    cols:[{key:'Title', label:'标题', w:3, on:false, asc:true},
          {key:'Duration', label:'时长', w:1, on:false, asc:true}],
    songs:[{n:1, cells:['歌曲 1', '3:00']}, {n:2, cells:['歌曲 2', '3:01']}]});
  document.querySelector('#tsmodes .caps[data-m="Sort"]').click();
  // 带自定义排序链的状态文本（_tagSortCustom 非空时预设后跟字段链）
  window.__send({kind:'tagsortpanel', mode:'Sort', title:'艺术家：歌手 0', fields:F,
    sort:{presets:[
            {label:'专辑', on:true},
            {label:'专辑艺术家 / 专辑'},
            {label:'专辑艺术家 / 年份 / 专辑'},
            {label:'艺术家 / 专辑'},
            {label:'流派 / 专辑'},
            {label:'年份 / 专辑'}],
          asc:true,
          status:'当前排序依据：专辑（流派↑ → 年份↓）'}});
  // 点已激活的「升序」：不上报（守卫）
  document.getElementById('tsasc').click();
  // 点「降序」：上报一次
  document.getElementById('tsdesc2').click();
  window.__send({kind:'tagsortpanel', mode:'Sort', title:'艺术家：歌手 0', fields:F,
    sort:{presets:[
            {label:'专辑', on:true},
            {label:'专辑艺术家 / 专辑'},
            {label:'专辑艺术家 / 年份 / 专辑'},
            {label:'艺术家 / 专辑'},
            {label:'流派 / 专辑'},
            {label:'年份 / 专辑'}],
          asc:false,
          status:'当前排序依据：专辑（流派↑ → 年份↓）'}});
  // 再点已激活的「降序」：也不上报（守卫）
  document.getElementById('tsdesc2').click();
  // 切预设「流派 / 专辑」：上报 tagsortsort
  document.querySelector('#tspresets .caps[data-p="流派 / 专辑"]').click();
  window.__send({kind:'tagsortpanel', mode:'Sort', title:'艺术家：歌手 0', fields:F,
    sort:{presets:[
            {label:'专辑'},
            {label:'专辑艺术家 / 专辑'},
            {label:'专辑艺术家 / 年份 / 专辑'},
            {label:'艺术家 / 专辑'},
            {label:'流派 / 专辑', on:true},
            {label:'年份 / 专辑'}],
          asc:false,
          status:'当前排序依据：流派 / 专辑'}});
  // 自定义排序…：开原生配置窗
  document.getElementById('tscustom').click();
  __T.dump('tagsortsort');
}, 200);
