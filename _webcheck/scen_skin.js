/* 场景：Windows 风（Fluent）皮肤运行时事实采集。
   main / winmain 都跑；断言按页面分流（assert_state.py 读 WB_PAGE）。
   口径（2026-10-10 用户拍板）：贴 Windows 标准、字体显式写出来、毛玻璃全上。
   几何一律 getComputedStyle 实采（不猜），样式表文本级扫描兜底 outline:0 /
   怪圆角 / 超长过渡——CSS 写了不等于生效，跑了才算数。 */
setTimeout(function(){
  __T.nav('Songs');
  var songs = __T.songs(10, {album:'专辑 1', artist:'歌手 0'});
  for (var i = 0; i < 3; i++) songs[i].fav = true;
  __T.pushData(songs, {Songs:10, Albums:4, Artists:3});
  __T.now(2);

  var st = __T.dump('skin');
  var q = function(sel){ return document.querySelector(sel); };
  var cs = function(sel, prop){
    var el = q(sel);
    return el ? getComputedStyle(el).getPropertyValue(prop) : null;
  };
  var px = function(v){
    if (v == null) return null;
    var m = /(-?[\d.]+)px/.exec(v);
    return m ? parseFloat(m[1]) : null;
  };
  var radiusOf = function(sel){ return px(cs(sel, 'border-radius')); };
  var durMs = function(sel){
    var v = cs(sel, 'transition-duration');
    if (!v) return null;
    var m = /([\d.]+)s/.exec(v.split(',')[0]);
    return m ? Math.round(parseFloat(m[1]) * 1000) : null;
  };
  var firstFont = function(sel){
    var ff = cs(sel, 'font-family') || '';
    return ff.split(',')[0].replace(/["']/g, '').trim();
  };
  /* 样式表文本级扫描（内联 <style> 同源可读）：
     outline:0 残留 / 字面怪圆角 / 字面过渡超 300ms */
  var cssText = '';
  for (var i = 0; i < document.styleSheets.length; i++){
    try {
      var rs = document.styleSheets[i].cssRules;
      for (var j = 0; j < rs.length; j++) cssText += rs[j].cssText + '\n';
    } catch(e){}
  }
  var oddRadius = [];
  var re = /border-radius:\s*([^;}]+)/g, m;
  while ((m = re.exec(cssText))){
    m[1].split(/\s+/).forEach(function(tok){
      var mm = /^([\d.]+)px$/.exec(tok);
      /* 16px=全圆胶囊（WinUI pill：caps/chip/bchip…）、10px=开关胶囊（.cwt .sw），
         这两个是 Fluent 标准件；其余 >8px 的字面圆角都算跑偏 */
      if (mm){
        var v = parseFloat(mm[1]);
        if (v > 8 && v !== 10 && v !== 16) oddRadius.push(tok);
      }
    });
  }
  var longDur = [];
  var re2 = /transition:[^;}]*/g;
  while ((m = re2.exec(cssText))){
    var mm = /([\d.]+)s/.exec(m[0]);
    if (mm && parseFloat(mm[1]) > 0.3) longDur.push(m[0]);
  }
  var giEls = document.querySelectorAll('.gi');
  st.skin = {
    bodyFont: firstFont('body'),
    giCount: giEls.length,
    giTag: giEls.length ? giEls[0].tagName : null,
    giFont: giEls.length ? firstFont('.gi') : null,
    giFontStyle: giEls.length ? cs('.gi', 'font-style') : null,
    /* 无头 Edge 实测 document.fonts.check 对系统字体可靠（缺字体回落宽度法不可靠，
       两者都是 32px）；这里作字形字体在位判据。
       ⚠ 必须写 document.fonts——window.fonts 不是规范挂点，本页面里取到 undefined */
    giFontAvail: (document.fonts && document.fonts.check) ? document.fonts.check("12px 'Segoe Fluent Icons'") : null,
    searchGi: !!q('.search .gi'),
    navGiCount: document.querySelectorAll('.nav .gi').length,
    favGiOn: !!q('.row .fav.on .gi'),
    tbBackdrop: cs('.tb', 'backdrop-filter') || cs('.tb', '-webkit-backdrop-filter'),
    sbBackdrop: cs('.sb', 'backdrop-filter') || cs('.sb', '-webkit-backdrop-filter'),
    pbBackdrop: cs('.pb', 'backdrop-filter') || cs('.pb', '-webkit-backdrop-filter'),
    theadBackdrop: cs('.thead', 'backdrop-filter') || cs('.thead', '-webkit-backdrop-filter'),
    npBackdrop: cs('.np', 'backdrop-filter') || cs('.np', '-webkit-backdrop-filter'),
    winBgImage: cs('.win', 'background-image'),
    navRadius: radiusOf('.nav'),
    btnRadius: radiusOf('.btn'),
    searchRadius: radiusOf('.search'),
    backRadius: radiusOf('.back'),
    npCollapseRadius: radiusOf('.npCollapse'),
    swRadius: radiusOf('.cwt .sw'),
    rowDur: durMs('.row'),
    btnDur: durMs('.btn'),
    navDur: durMs('.nav'),
    trackH: cs('.track', 'height'),
    knobW: cs('.track .knob', 'width'),
    rowH: cs('#rows .row', 'height'),
    tbH: cs('.tb', 'height'),
    pbH: cs('.pb', 'height'),
    hdrH: cs('.hdr', 'height'),
    cssOutlineZero: /outline:\s*0(?![\d.])/.test(cssText),
    cssOddRadius: oddRadius,
    cssLongDur: longDur,
    title: document.title,
    tbmid: (q('.tbmid') || {}).textContent || null
  };
  document.getElementById('__statedump').textContent = '/*STATE*/' + JSON.stringify(st) + '/*END*/';
}, 300);
