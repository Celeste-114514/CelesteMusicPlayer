/* 场景：标签排序空态——曲库没歌时墙显「曲库里还没有歌曲」；
   分组浏览（墙上入口，空树）显「没有可分组的内容」；
   切曲目/专辑视角（空分类）显「这个分类下没有歌曲」。 */
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
  // 空曲库：一条分类都没有
  window.__send({kind:'tagsortcats', field:'Artist', fields:F, cards:[], total:0, shown:0, advice:''});
  // 墙上「分组浏览」入口：空树
  document.getElementById('tsgroupbtn').click();
  window.__send({kind:'tagsortpanel', mode:'GroupBy', title:'分组浏览', fields:F,
    group:{presets:[
             {tag:'__custom__', label:'自定义（已保存）', on:true},
             {tag:'Artist,Album', label:'艺术家 / 专辑'},
             {tag:'Artist,Album,Year', label:'艺术家 / 专辑 / 年份'},
             {tag:'Artist,Album,Title', label:'艺术家 / 专辑 / 标题'},
             {tag:'Album,Year', label:'专辑 / 年份'},
             {tag:'Genre,Artist', label:'流派 / 艺术家'},
             {tag:'Year,Album', label:'年份 / 专辑'},
             {tag:'Format,DepthRate', label:'格式 / 位深采样率'}],
           rows:[]}});
  // 切曲目视角：空分类
  document.querySelector('#tsmodes .caps[data-m="Songs"]').click();
  window.__send({kind:'tagsortpanel', mode:'Songs', title:'分组浏览', fields:F,
    cols:[{key:'Title', label:'标题', w:3, on:false, asc:true},
          {key:'Duration', label:'时长', w:1, on:false, asc:true}],
    songs:[]});
  // 切专辑视角：空
  document.querySelector('#tsmodes .caps[data-m="Albums"]').click();
  window.__send({kind:'tagsortpanel', mode:'Albums', title:'分组浏览', fields:F, grid:[]});
  __T.dump('tagsortempty');
}, 200);
