/* 场景公共数据构造：字段名/形状必须和 C# MainWindow.WebMain.cs 的推送一一对应。 */
window.__T = {
  /* 侧栏分类（照 C# WebMainCategories；流派/年份已按用户要求去掉） */
  cats: function(counts){
    counts = counts || {};
    var LBL = {Songs:'歌曲',Albums:'专辑',Artists:'艺术家',AlbumArtists:'专辑艺术家',
               Favorites:'我喜欢的音乐',Ratings:'评分',Recent:'最近播放',
               UserPlaylist:'播放队列',MostPlayed:'播放最多',Folders:'媒体库',
               WebDav:'网络音乐库',AudioFX:'音效处理',TagSort:'标签排序',PlaylistWall:'播放列表'};
    var GRP = {}; ['Songs','Albums','Artists','AlbumArtists','Favorites','Ratings','Recent','UserPlaylist','MostPlayed'].forEach(function(x){GRP[x]='music';});
    GRP.Folders='media'; GRP.WebDav='media'; GRP.AudioFX='tool'; GRP.TagSort='tool'; GRP.PlaylistWall='tool';
    var OK = {Songs:1,Albums:1,Artists:1,AlbumArtists:1,Favorites:1,Ratings:1,Recent:1,UserPlaylist:1,MostPlayed:1};
    var out = [];
    for (var id in LBL) out.push({id:id, t:LBL[id], g:GRP[id], ok:!!OK[id], n:counts[id]||0});
    return out;
  },
  /* 歌曲快照行（字段对齐 C# 分块推送：title/artist/album/duration/dsd/fmt/fav，最近播放加 sub） */
  song: function(i, o){
    o = o || {};
    return {
      title: o.title || ('歌曲 ' + (i + 1)),
      artist: o.artist || ('歌手 ' + (i % 3)),
      album: o.album || ('专辑 ' + (Math.floor(i / 3) + 1)),
      duration: o.duration || (180 + i),
      dsd: !!o.dsd,
      fmt: o.fmt || 'FLAC',
      fav: !!o.fav,
      sub: o.sub || ''
    };
  },
  songs: function(n, o){
    var a = [];
    for (var i = 0; i < n; i++) a.push(this.song(i, o));
    return a;
  },
  /* data 首包 + data/append 分块（照 C# 推送顺序） */
  pushData: function(songs, counts){
    window.__send({kind:'data', categories:this.cats(counts), songs:[], total:songs.length, cur:-1});
    window.__send({kind:'data/append', songs:songs});
  },
  nav: function(id){ window.__send({kind:'nav', id:id, ok:true}); },
  now: function(index, cover, path){
    window.__send({kind:'now', index:index, playing:true, position:65, duration:227,
                   volume:0.7, cover:cover || window.covS(0), path:path || ''});
  },
  albums: function(n){
    var items = [];
    for (var i = 0; i < n; i++)
      items.push({i:i, name:'专辑 ' + (i + 1), artist:'歌手 ' + (i % 3), year:String(2018 + i), n:3});
    return items;
  },
  pushAlbums: function(n){
    var items = this.albums(n);
    window.__send({kind:'albums', items:items, total:n});
    var cc = [];
    for (var i = 0; i < n; i++) cc.push({i:i, album:'专辑 ' + (i + 1), cover:window.covS(i)});
    window.__send({kind:'albumcovers', items:cc});
    return items;
  },
  /* 专辑详情曲目（disc 序列可传，用来验证 CD 分组） */
  albumTracks: function(discs, cover){
    var out = [];
    for (var i = 0; i < discs.length; i++){
      out.push({n:i + 1, disc:discs[i], title:'曲目 ' + (i + 1), artist:'歌手 0',
                album:'专辑 1', duration:200 + i, dsd:(i === 0), fmt:'FLAC',
                cover:cover, fav:(i === 1)});
    }
    return out;
  },
  pushAlbumDetail: function(discs, opt){
    opt = opt || {};
    var cover = window.covS(1);
    var tracks = this.albumTracks(discs, cover);
    window.__send({kind:'albumtracks',
      album:{name:'专辑 1', artist:'歌手 0', year:'2020', n:tracks.length, dur:'25:00',
             cover:cover, tech:opt.tech || 'FLAC | 24bit/96kHz | 2.3 Mbps | 25:00',
             dsd:!!opt.dsd},
      tracks:tracks, cur:1});
    return tracks;
  },
  artists: function(n){
    var items = [];
    for (var i = 0; i < n; i++) items.push({i:i, name:'歌手 ' + i, n:3 + i});
    return items;
  },
  pushArtists: function(n){
    var items = this.artists(n);
    window.__send({kind:'artists', mode:'artist', items:items, total:n});
    return items;
  },
  artistDetail: function(){
    var cover = window.covS(2);
    var albums = [];
    for (var i = 0; i < 4; i++)
      albums.push({i:i, name:'专辑 ' + (i + 1), artist:'歌手 0', year:String(2019 + i), n:3, cover:window.covS(i)});
    var tracks = [];
    for (var i = 0; i < 6; i++)
      tracks.push({n:i + 1, title:'歌曲 ' + (i + 1), artist:'歌手 0', album:'专辑 ' + (i % 2 + 1),
                   duration:190 + i, dsd:false, fmt:'FLAC', cover:cover, fav:(i === 0)});
    window.__send({kind:'artistdetail',
      artist:{name:'歌手 0', n:tracks.length, dur:'19:30', cover:cover},
      albums:albums, tracks:tracks, cur:0});
    return {albums:albums, tracks:tracks};
  },
  /* 页面状态快照（供 dump-dom 断言；同时塞进 DOM 的 json script 块里） */
  dump: function(tag){
    var list = function(sel){
      var out = [];
      var els = document.querySelectorAll(sel);
      for (var i = 0; i < els.length; i++) out.push(els[i]);
      return out;
    };
    var rows = list('#rows .row');
    var vis = function(id){
      var el = document.getElementById(id);
      return el ? getComputedStyle(el).display !== 'none' : null;
    };
    var bigcov = document.querySelector('#artmeta .bigcov img, #albmeta .bigcov img');
    /* 媒体库（文件夹浏览）状态：左树节点 + 右栏行 */
    var fnodes = list('#foldertree .fnode');
    var fnodeInfo = fnodes.map(function(el){
      var chev = el.querySelector('.chev');
      return {
        n: el.querySelector('.nm').textContent,
        pad: el.style.paddingLeft,
        sel: el.className.indexOf('sel') >= 0,
        root: el.className.indexOf('root') >= 0,
        open: !!chev && chev.textContent.indexOf('\u25BE') >= 0,   // ▾=展开
        f: el.getAttribute('data-f') === '1',
        p: el.getAttribute('data-p')
      };
    });
    var frows = list('#frows .frow');
    /* 标签排序（TagSort）状态：墙（字段钮+分类卡+溢出条）与面板五视角。
       曲目行高亮单独抠（#tssongs .tsrow.on）——Songs 视角的 now.index 按分类曲目算。 */
    var tsWallCards = list('#tsgrid .acard');
    var tsGridCards = list('#tscards .acard');
    var tsSongRows = list('#tssongs .tsrow');
    var tsGroupSongs = list('#tsgrows .tgr');
    var tsHeads = list('#tsgrows .tgh').map(function(el){
      return {
        path: el.getAttribute('data-path'),
        v: (el.querySelector('.gv') || {}).textContent,
        l: (el.querySelector('.gl') || {}).textContent,
        open: el.querySelector('.chev').textContent.indexOf('\u25BE') >= 0,   // ▾=展开
        n: (el.querySelector('.gn') || {}).textContent,
        pad: el.style.paddingLeft
      };
    });
    var tsAsc = document.getElementById('tsasc');
    var tsDesc = document.getElementById('tsdesc2');
    /* 播放列表（PlaylistWall）状态：墙卡片 + 详情行 + 顶栏按钮 + now 路径高亮 */
    var plCards = list('#plgrid .acard');
    var plRows = list('#plrows .plrow');
    var plOn = document.querySelector('#plrows .plrow.on');
    // 当前视角照 DOM 判（common.js 在页面 IIFE 外，读不到页面的 S）：
    // 面板里激活的视角胶囊 data-m；墙上没有胶囊 = Wall
    var tsModeEl = document.querySelector('#tsmodes .caps.on');
    window.__state = {
      tag: tag,
      title: document.getElementById('pgTitle').textContent,
      sub: document.getElementById('pgSub').textContent,
      cols: document.getElementById('view').className,
      rowCount: rows.length,
      discs: list('#rows .disc').map(function(d){ return d.textContent; }),
      theadN: document.getElementById('songTheadN').textContent,
      theadT: document.getElementById('songTheadT').textContent,
      listArt: document.getElementById('listwrap').className,
      ratingVisible: vis('ratingbar'),
      ratingOn: list('#ratingbar .caps.on').map(function(b){ return b.getAttribute('data-v'); }),
      albmetaVisible: vis('albmeta'),
      artmetaVisible: vis('artmeta'),
      bigcovImg: bigcov ? bigcov.getAttribute('src') : null,
      tech: (document.querySelector('#albmeta .tech') || {}).textContent || null,
      dsdhint: !!document.querySelector('#albmeta .dsdhint'),
      enqueueBtn: !!document.getElementById('btnEnqueue'),
      xtalbHead: (document.querySelector('#xtalbhead') || {}).textContent || null,
      xtalbCards: list('#xtalbgrid .acard').length,
      firstRowSub: rows.length ? (rows[0].querySelector('.s') || {}).textContent : null,
      firstRowFav: rows.length ? (rows[0].querySelector('.fav') || {}).className : null,
      artCardsWithImg: list('#artgrid .acard .av img').length,
      folderVisible: vis('folderwrap'),
      folderNodes: fnodeInfo,
      folderNodeCount: fnodeInfo.length,
      folderHead: document.getElementById('folderhead').textContent,
      folderRowCount: frows.length,
      folderFirstRow: frows.length ? {
        t: (frows[0].querySelector('.t') || {}).textContent,
        s: (frows[0].querySelector('.s') || {}).textContent,
        d: (frows[0].querySelector('.d') || {}).textContent,
        p: frows[0].getAttribute('data-p')
      } : null,
      folderEmptyText: (document.querySelector('#foldertree .empty, #frows .empty') || {}).textContent || null,
      folderSearchPh: document.getElementById('q').placeholder,
      tagsortVisible: vis('tagsortwrap'),
      searchVisible: vis('qwrap'),
      tagsortMode: tsModeEl ? tsModeEl.getAttribute('data-m') : 'Wall',
      tagsortPanelVisible: vis('tspanel'),
      tagsortFields: list('#tsbar .caps[data-k]').map(function(el){
        return {k: el.getAttribute('data-k'), on: el.className.indexOf(' on') >= 0};
      }),
      tagsortPlusBtn: !!document.getElementById('tsplus'),
      tagsortGroupBtn: !!document.getElementById('tsgroupbtn'),
      tagsortCardCount: tsWallCards.length,
      tagsortFirstCard: tsWallCards.length ? {
        n: tsWallCards[0].querySelector('.nm').textContent,
        a: tsWallCards[0].querySelector('.ar').textContent
      } : null,
      tagsortMoreVisible: vis('tsmore'),
      tagsortWallEmpty: (document.querySelector('#tsgrid .empty') || {}).textContent || null,
      tagsortSongsEmpty: (document.querySelector('#tssongs .empty') || {}).textContent || null,
      tagsortGridEmpty: (document.querySelector('#tscards .empty') || {}).textContent || null,
      tagsortGroupEmpty: (document.querySelector('#tsgrows .empty') || {}).textContent || null,
      tagsortTitle: document.getElementById('tstitle').textContent,
      tagsortCols: list('#tssongs .tshead .tsc[data-k]').map(function(el){
        return {k: el.getAttribute('data-k'), on: el.className.indexOf(' on') >= 0,
                txt: el.textContent};
      }),
      tagsortRowCount: tsSongRows.length,
      tagsortFirstRow: tsSongRows.length ? {
        n: tsSongRows[0].querySelector('.tsn').textContent,
        cells: Array.prototype.map.call(tsSongRows[0].querySelectorAll('.tsc'),
                 function(c){ return c.textContent; })
      } : null,
      tagsortOnIdx: (function(){
        var on = document.querySelector('#tssongs .tsrow.on');
        return on ? on.getAttribute('data-i') : null;
      })(),
      tagsortOnN: (function(){
        var on = document.querySelector('#tssongs .tsrow.on');
        return on ? (on.querySelector('.tsn') || {}).textContent : null;
      })(),
      tagsortGridCount: tsGridCards.length,
      tagsortFirstGrid: tsGridCards.length ? {
        n: tsGridCards[0].querySelector('.nm').textContent,
        a: tsGridCards[0].querySelector('.ar').textContent
      } : null,
      tagsortSortVisible: vis('tssort'),
      tagsortSortOn: list('#tspresets .caps.on').map(function(b){ return b.textContent; }),
      tagsortAscOn: tsAsc ? tsAsc.className.indexOf(' on') >= 0 : null,
      tagsortDescOn: tsDesc ? tsDesc.className.indexOf(' on') >= 0 : null,
      tagsortSortStatus: (document.querySelector('#tssort .tsstatus') || {}).textContent || null,
      tagsortGroupVisible: vis('tsgroupv'),
      tagsortGroupSel: (document.getElementById('tsgsel') || {}).value || null,
      tagsortGroupHeads: tsHeads,
      tagsortGroupSongCount: tsGroupSongs.length,
      tagsortFirstGroupSong: tsGroupSongs.length ? {
        t: tsGroupSongs[0].querySelector('.gt').textContent,
        a: tsGroupSongs[0].querySelector('.ga').textContent,
        d: tsGroupSongs[0].querySelector('.gd').textContent,
        pad: tsGroupSongs[0].style.paddingLeft,
        file: tsGroupSongs[0].getAttribute('data-file')
      } : null,
      playAllVisible: document.getElementById('btnPlayAll').style.visibility,
      plwallVisible: vis('plwall'),
      pldetailVisible: vis('pldetail'),
      plCardCount: plCards.length,
      plCardsWithImg: list('#plgrid .acard .cv img').length,
      plFirstCard: plCards.length ? {
        n: plCards[0].querySelector('.nm').textContent,
        a: plCards[0].querySelector('.ar').textContent,
        img: !!plCards[0].querySelector('.cv img')
      } : null,
      plRowCount: plRows.length,
      plFirstRow: plRows.length ? {
        n: plRows[0].querySelector('.n').textContent,
        t: plRows[0].querySelector('.t').textContent,
        s: plRows[0].querySelector('.s').textContent,
        d: plRows[0].querySelector('.d').textContent,
        fav: plRows[0].querySelector('.fav').className,
        p: plRows[0].getAttribute('data-p')
      } : null,
      plOnN: plOn ? (plOn.querySelector('.n') || {}).textContent : null,
      plOnP: plOn ? plOn.getAttribute('data-p') : null,
      plDelBtnText: document.getElementById('btnPlDel').textContent,
      plNameText: document.getElementById('plname').textContent,
      plNameInput: !!document.getElementById('plnameInput'),
      plMsgVisible: vis('plmsg'),
      plWallEmpty: (document.querySelector('#plgrid .empty') || {}).textContent || null,
      plDetailEmpty: (document.querySelector('#plrows .empty') || {}).textContent || null,
      posted: window.__posted.slice(),
      errors: window.__wvErrors.slice()
    };
    /* 快照同时写进 DOM（script type=application/json 内容不会被转义），
       无头 --dump-dom 时按标记抠出来断言 */
    var sd = document.getElementById('__statedump');
    if (!sd){
      sd = document.createElement('script');
      sd.id = '__statedump';
      sd.type = 'application/json';
      document.body.appendChild(sd);
    }
    sd.textContent = '/*STATE*/' + JSON.stringify(window.__state) + '/*END*/';
    return window.__state;
  }
};
