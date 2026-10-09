/* 场景：正在播放页（播放条/控制钮 SVG 图标 + 歌词区 + 播放顺序图标切换） */
setTimeout(function(){
  __T.nav('Songs');
  var songs = __T.songs(12, {album:'专辑 1', artist:'歌手 0'});
  __T.pushData(songs, {Songs:12, Albums:4, Artists:3});
  __T.now(2, window.covS(0), 'D:\\music\\song3.flac');

  /* 歌词就绪（C# 在 BuildLyricsUi 之后推的形状：path + lines[{t,x,tr}]） */
  var lines = [];
  for (var i = 0; i < 8; i++){
    lines.push({t: i * 12, x: '第 ' + (i + 1) + ' 句歌词，故意写长一点测试换行效果', tr: false});
    lines.push({t: i * 12 + 0.5, x: '（译文 ' + (i + 1) + '）', tr: true});
  }
  window.__send({kind:'lyrics', path:'D:\\music\\song3.flac', lines:lines});

  /* 进正在播放页（点播放条封面） */
  document.getElementById('pbCov').click();

  /* 双击第 5 行（data-i=4, t=24）→ 应发 seek 24 */
  var rows = document.querySelectorAll('#npLyrIn .ll');
  if (rows[4]) rows[4].dispatchEvent(new MouseEvent('dblclick', {bubbles:true}));
  /* 单击第 7 行（data-i=6）→ 选中（sel），不应发 seek */
  if (rows[6]) rows[6].dispatchEvent(new MouseEvent('click', {bubbles:true}));

  /* 换播放顺序 + 暂停 + 收藏 → 三个图标都应切换（data-cur） */
  window.__send({kind:'now', index:2, playing:false, position:65, duration:227,
                 volume:0.7, path:'D:\\music\\song3.flac', order:'Random', rate:'1.5x', love:true});

  /* 无头截图拍不到 position:fixed 覆盖层（已知限制）：探针样式把 .win 藏掉、
     np 改静态，让它进普通流——只为截图，DOM 断言不受影响 */
  var probe = document.createElement('style');
  probe.textContent = '.win{display:none !important}'
    + '.np{position:static !important;inset:auto !important;height:980px}';
  document.head.appendChild(probe);

  __T.dump('np');

  /* 扩展快照（np 专属状态） */
  var st = window.__state;
  var cur = document.querySelector('#npLyrIn .ll.cur');
  var sel = document.querySelector('#npLyrIn .ll.sel');
  st.np = {
    npOn: document.getElementById('npPage').className,
    lyrCount: document.querySelectorAll('#npLyrIn .ll').length,
    lyrEmptyVisible: getComputedStyle(document.getElementById('npLyrEmpty')).display !== 'none',
    lyrCurIdx: cur ? cur.getAttribute('data-i') : null,
    lyrCurText: cur ? cur.textContent : null,
    lyrSelIdx: sel ? sel.getAttribute('data-i') : null,
    lyrTrCount: document.querySelectorAll('#npLyrIn .ll.tr').length,
    lyrScrollH: document.getElementById('npLyrIn').scrollHeight,
    lyrClientH: document.getElementById('npLyrIn').clientHeight,
    lyrScrollTop: document.getElementById('npLyrIn').scrollTop,
    lyrFirstLineWrapped: (function(){
      var l = document.querySelector('#npLyrIn .ll');
      return l ? l.getBoundingClientRect().height : 0;
    })(),
    /* 图标是否真的换成了 SVG（data-cur 记录当前图标名；静态占位图标查 svg 在不在） */
    svgIn: function(ids){
      var out = {};
      for (var i = 0; i < ids.length; i++)
        out[ids[i]] = !!document.getElementById(ids[i]).querySelector('svg');
      return out;
    }(['btnPlay','btnOrder','btnFav','btnPrev','btnSeekBack','btnSeekFwd','btnNext',
       'btnQueue','btnMore','btnMini','btnLyrics','npClose','npPlay','npOrder',
       'npPrev','npNext','npFav']),
    icoPlayPause: document.getElementById('btnPlay').getAttribute('data-cur'),
    icoPlayPauseNp: document.getElementById('npPlay').getAttribute('data-cur'),
    icoOrder: document.getElementById('btnOrder').getAttribute('data-cur'),
    icoOrderNp: document.getElementById('npOrder').getAttribute('data-cur'),
    icoFav: document.getElementById('btnFav').getAttribute('data-cur'),
    icoFavNp: document.getElementById('npFav').getAttribute('data-cur'),
    orderBtnClass: document.getElementById('btnOrder').className,
    orderBtnTitle: document.getElementById('btnOrder').title,
    npOrderClass: document.getElementById('npOrder').className,
    btnOrderText: document.getElementById('btnOrder').textContent,
    rateText: document.getElementById('btnRate').textContent,
    seekPosted: window.__posted.filter(function(m){ return m.kind === 'seek'; }),
    lyricsReqPosted: window.__posted.filter(function(m){ return m.kind === 'lyricsreq'; }).length,
    /* 计算样式（替代看图）：三列布局 + 歌词四档字号/颜色 */
    npBodyCols: getComputedStyle(document.querySelector('.npBody')).gridTemplateColumns,
    npLyrW: document.getElementById('npLyrIn').getBoundingClientRect().width,
    npArtW: document.querySelector('.npArt').getBoundingClientRect().width,
    npInfoW: document.querySelector('.npInfo').getBoundingClientRect().width,
    curFont: (function(){ var e = document.querySelector('#npLyrIn .ll.cur'); return e ? getComputedStyle(e).fontSize : null; })(),
    curColor: (function(){ var e = document.querySelector('#npLyrIn .ll.cur'); return e ? getComputedStyle(e).color : null; })(),
    curWeight: (function(){ var e = document.querySelector('#npLyrIn .ll.cur'); return e ? getComputedStyle(e).fontWeight : null; })(),
    nearFont: (function(){ var e = document.querySelector('#npLyrIn .ll.near:not(.tr)'); return e ? getComputedStyle(e).fontSize : null; })(),
    nearFontRule: (function(){
      for (var i = 0; i < document.styleSheets.length; i++){
        try {
          var rs = document.styleSheets[i].cssRules;
          for (var j = 0; j < rs.length; j++)
            if (rs[j].selectorText === '.npLyr .ll.near') return rs[j].style.fontSize;
        } catch(e){}
      }
      return null;
    })(),
    npCtrlsOverflow: (function(){
      var e = document.querySelector('.npCtrls');
      return {sw: e.scrollWidth, cw: e.clientWidth};
    })(),
    farFont: (function(){ var e = document.querySelector('#npLyrIn .ll:not(.cur):not(.near):not(.tr)'); return e ? getComputedStyle(e).fontSize : null; })(),
    trFont: (function(){ var e = document.querySelector('#npLyrIn .ll.tr'); return e ? getComputedStyle(e).fontSize : null; })(),
    selBg: (function(){ var e = document.querySelector('#npLyrIn .ll.sel'); return e ? getComputedStyle(e).backgroundColor : null; })(),
    lyrScrollbarHidden: (function(){
      var e = document.getElementById('npLyrIn');
      return getComputedStyle(e).scrollbarWidth;
    })(),
    pbCoverRadius: getComputedStyle(document.getElementById('pbCov')).borderRadius,
    npCoverRadius: getComputedStyle(document.getElementById('npCov')).borderRadius,
    navOnRadius: (function(){
      var e = document.querySelector('.nav.on');
      return e ? getComputedStyle(e).borderRadius : null;
    })(),
    rowCoverRadius: (function(){
      var e = document.querySelector('#rows .row .a');
      return e ? getComputedStyle(e).borderRadius : null;
    })()
  };
  var sd = document.getElementById('__statedump');
  sd.textContent = '/*STATE*/' + JSON.stringify(st) + '/*END*/';
}, 300);
