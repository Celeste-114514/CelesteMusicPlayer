/* 场景：音效处理（DSP）网页面板——全量状态落地 + 12 页控件 + 全部交互上报 + 播放条 + 旁路 + 主题。
   协议照 dsp.html：
     C#→网页 dspstate（全量）/ dspmon（每秒监控）/ now（播放状态）/ theme / dspmsg
     网页→C# ready / dspnav / dsppower / dspset / dspact / pause·next·prev·seek·volume / exit
   本文件在 common.js 之后注入；main.html 的 __T.dump 不适用这里的 DOM，故自带 __dumpDsp。
   交互后同步改 JS 侧真值再推 dspstate——模拟 C# 写原生控件后的回推（网页永远显示真值）。 */
setTimeout(function(){
  /* ---------- C# 侧当前真值 ---------- */
  var PRESETS = [{t:'默认'},{t:'流行'},{t:'摇滚'}];
  var PERFECT_DSP = '输出非 bit-perfect（DSP 生效）';
  var curMode = 0, curPreset = 1, curSel = 1;
  /* 频段频率一律取 2 的幂——log2 后正好落在滑杆步进（0.01）上，免得步进吸附把读数带偏 */
  var curBands = [
    {f:1024, g:3,   q:1,   ty:'Peaking',   on:true},
    {f:64,   g:-4,  q:0.7, ty:'LowShelf',  on:true},
    {f:8192, g:2.5, q:1.2, ty:'HighShelf', on:false}
  ];
  var curStrip = {status:'EQ 生效中', perfect:PERFECT_DSP,
                  preamp:'预增益 -2.5 dB', headroom:'余量 -6.0 dB', preset:'预设：流行'};
  var curPower = {
    headroom:{on:true,  dis:false, hint:''},
    rg:      {on:true,  dis:false, hint:''},
    src:     {on:false, dis:false, hint:''},
    eq:      {on:true,  dis:false, hint:''},
    opra:    {on:false, dis:true,  hint:'未安装 OPRA 数据库'},
    comp:    {on:true,  dis:false, hint:''},
    xfeed:   {on:false, dis:false, hint:''},
    channel: {on:false, dis:false, hint:''},
    field:   {on:false, dis:false, hint:''},
    matrix:  {on:false, dis:false, hint:''},
    fir:     {on:false, dis:true,  hint:'未导入 IR'}
  };
  var curCtrl = {
    headroom:-6, limiter:true, rgmode:2, rgpreamp:0, rgclip:true,
    srcrate:4, srcquality:1, srcdither:1,
    eqpreamp:-2.5, eqbass:0.5, eqvocal:0.4, eqair:0.6, eqwarm:0.3,
    compthreshold:-24, compratio:4, compattack:10, comprelease:120, compknee:6,
    compmakeup:2, compmix:80, comppeak:true,
    chbalance:0, chlgain:0, chrgain:0, chldelay:0, chrdelay:0, chmono:0,
    chswap:false, chinvertl:false, chinvertr:false,
    xfeedlevel:25, xfeedcutoff:700,
    fieldwidth:100, fieldcenter:0, fieldside:0,
    mll:1, mrl:0, mlr:0, mrr:1, roomtrim:0
  };
  var curProfiles = {
    status:'当前方案：客厅耳机',
    savehint:'保存会把整套 DSP 设置存成快照，随时可以切回来。',
    sel:1,
    rows:[
      {name:'默认',     stamp:'2026-10-01 10:24', on:false},
      {name:'客厅耳机', stamp:'2026-10-09 21:03', on:true}
    ]
  };
  var curRack = {
    sel:1,
    hint:'响度补偿：把整体电平拉回平均线，响度模式见输入段。',
    rows:[
      {slot:'1.', n:'输入余量',        st:'生效中', on:true,  hint:'先把整体电平压低一点再进后续处理，给后面留头顶空间。'},
      {slot:'2.', n:'响度 · ReplayGain', st:'生效中', on:true,  hint:'响度补偿：把整体电平拉回平均线，响度模式见输入段。'},
      {slot:'3.', n:'SRC · 升频',      st:'已关闭', on:false, hint:'目标采样率与质量档位，改完下次开播生效。'},
      {slot:'4.', n:'参数 EQ',         st:'生效中', on:true,  hint:'31 段图示 EQ，含预增益与简单模式。'},
      {slot:'5.', n:'动态压缩器',      st:'生效中', on:true,  hint:'阈值以下不动声音，超过部分按压缩比压低。'},
      {slot:'6.', n:'声道工具',        st:'已关闭', on:false, hint:'平衡/增益/延迟/单声道，Crossfeed 父开关也在这里。'},
      {slot:'7.', n:'立体声场',        st:'已关闭', on:false, hint:'宽度改变中/侧向比例，100% 不改变原始声场。'},
      {slot:'8.', n:'声道矩阵',        st:'已关闭', on:false, hint:'四系数混音矩阵，±2 之间微调。'}
    ]
  };
  var curMon = {peak:'-2.1 dBFS', clip:'0 次', over:'正常', active:'EQ · 压缩', chain:'44.1 kHz 直出'};
  var curTexts = {
    srcstate:'当前 44.1 kHz 直出；目标 96 kHz，均衡档，TPDF 抖动。',
    rg:'专辑模式 · 预增益 0 dB · 防削波开。',
    fir:'未导入 IR；导入后可按当前 trim 试算安全余量。',
    cliprisk:'按当前 trim 试算：安全。',
    mxphase:'矩阵当前无异常相位。'
  };
  var curBypass = false, curPerfect = PERFECT_DSP;

  function setBypass(on){
    curBypass = on;
    curPerfect = on ? 'bit-perfect 直通（DSP 已旁路）' : PERFECT_DSP;
    curStrip.perfect = curPerfect;
    for (var k in curPower){
      if (curPower[k].dis) continue;      // 禁用原因优先，旁路提示让位（同 C# 口径）
      curPower[k].hint = on ? '已旁路' : '';
    }
  }

  var NAV_TITLES = ['听音方案','DSP 机架编排','输入余量','响度 · ReplayGain','SRC / 升频','参数 EQ',
                    '动态压缩器','声道工具','立体声场','声道矩阵','FIR / 房间校正','输出安全'];
  function dspState(page){
    var nav = [];
    for (var i = 0; i < 12; i++) nav.push({t:NAV_TITLES[i], g:'', dot:(i === 5 || i === 6)});
    return {
      kind:'dspstate', page:page, nav:nav, power:curPower, ctrl:curCtrl,
      eq:{mode:curMode, presets:PRESETS, preset:curPreset, bands:curBands, sel:curSel,
          simple:{bass:curCtrl.eqbass, vocal:curCtrl.eqvocal, air:curCtrl.eqair, warm:curCtrl.eqwarm},
          strip:curStrip, opra:false},
      profiles:curProfiles, rack:curRack, mon:curMon,
      bypass:curBypass, perfect:curPerfect, texts:curTexts,
      device:{fmt:'FLAC · 24bit/96kHz'}
    };
  }

  /* ---------- 交互小工具（拨原生控件同款：赋值 → 触发 input/change） ---------- */
  function fireChange(el){ el.dispatchEvent(new Event('change', {bubbles:true})); }
  function setRange(id, v){
    var el = document.getElementById(id);
    el.value = String(v);
    el.dispatchEvent(new Event('input', {bubbles:true}));   // 读数即时更新
    fireChange(el);                                          // 上报意图
  }
  function setCheck(id, on){
    var el = document.getElementById(id);
    el.checked = on;
    fireChange(el);
  }
  function setSelect(id, v){
    var el = document.getElementById(id);
    el.value = String(v);
    fireChange(el);
  }
  function setRadio(name, v){
    var el = document.querySelector('#eqMode input[value="' + v + '"]');
    el.checked = true;
    fireChange(el);
  }
  function firePointer(el, ratio){
    var r = el.getBoundingClientRect();
    var x = r.left + r.width * ratio, ev;
    try { ev = new PointerEvent('pointerdown', {clientX:x, bubbles:true}); }
    catch(e){ ev = new MouseEvent('pointerdown', {clientX:x, bubbles:true}); }
    el.dispatchEvent(ev);
  }

  /* ================= 流程 ================= */
  // —— 初始全量：停在听音方案页 ——
  window.__send(dspState(0));
  // —— 每秒监控 + 播放状态 ——
  window.__send({kind:'dspmon', mon:{peak:'-1.8 dBFS', clip:'2 次', over:'正常',
                                     active:'EQ · 压缩', chain:'44.1 → 96 kHz'},
                 perfect:PERFECT_DSP});
  window.__send({kind:'now', playing:true, position:65, duration:227, volume:0.7,
                 cover:window.covS(0), track:{t:'晴天', s:'周杰伦 · 叶惠美'}});
  // —— dspmsg 一句操作结果 ——
  window.__send({kind:'dspmsg', text:'已应用选中方案'});
  window.__dmsgSeen = {vis:document.getElementById('dmsg').style.display,
                       text:document.getElementById('dmsg').textContent};

  // —— ① 切到参数 EQ 页 ——
  document.getElementById('ni_5').click();
  window.__send(dspState(5));
  // ② 选中第 1 频段（当前 sel=1）
  document.querySelectorAll('#eqBands .bchip')[0].click();
  curSel = 0;
  window.__send(dspState(5));
  // ③ 预设切到「摇滚」
  setSelect('c_eqpreset', 2);
  curPreset = 2; curStrip.preset = '预设：摇滚';
  window.__send(dspState(5));
  // ④ 选中段增益 3 → 6 dB
  setRange('c_eqbandgain', 6);
  curBands[0].g = 6;
  window.__send(dspState(5));
  // ⑤ 滤波器类型切高通
  setSelect('c_eqbandtype', 'HighPass');
  curBands[0].ty = 'HighPass';
  window.__send(dspState(5));
  // ⑥ 本段停用
  setCheck('c_eqbandon', false);
  curBands[0].on = false;
  window.__send(dspState(5));
  // ⑦ 频率拖到 4096 Hz（log2=12，步进对齐）
  setRange('c_eqbandfreq', 12);
  curBands[0].f = 4096;
  window.__send(dspState(5));
  // ⑧ Q 拖到 2.5
  setRange('c_eqbandq', 2.5);
  curBands[0].q = 2.5;
  window.__send(dspState(5));
  // ⑨ 添加频段（C# 加在末尾并选中新段）
  document.getElementById('btnEqAddBand').click();
  curBands.push({f:16384, g:0, q:1, ty:'Peaking', on:true});
  curSel = 3;
  window.__send(dspState(5));
  // ⑩ 删除本段（回到 3 段）
  document.getElementById('btnEqDelBand').click();
  curBands.pop();
  curSel = 2;
  window.__send(dspState(5));
  // ⑪ EQ 按钮动作一串
  document.getElementById('btnEqUndo').click();
  document.getElementById('btnEqRedo').click();
  document.getElementById('btnEqAb').click();
  document.getElementById('btnEqAutoGain').click();
  document.getElementById('btnEqSpectrum').click();
  document.getElementById('btnEqSavePreset').click();
  document.getElementById('btnEqImportApo').click();
  document.getElementById('btnEqExportApo').click();
  // ⑫ 切简单模式 → 拨低音 → 恢复平坦 → 切回专业
  setRadio('eqmode', 1);
  curMode = 1;
  window.__send(dspState(5));
  setRange('c_eqbass', 0.8);
  curCtrl.eqbass = 0.8;
  window.__send(dspState(5));
  document.getElementById('btnEqFlat').click();
  setRadio('eqmode', 0);
  curMode = 0;
  window.__send(dspState(5));
  // ⑬ 页头电源：EQ 关 → 开
  setCheck('pw_eq', false);
  curPower.eq.on = false;
  window.__send(dspState(5));
  setCheck('pw_eq', true);
  curPower.eq.on = true;
  window.__send(dspState(5));
  // ⑭ 收尾再选第 1 段（编辑器显示被改过的那段）
  document.querySelectorAll('#eqBands .bchip')[0].click();
  curSel = 0;
  window.__send(dspState(5));

  // —— 听音方案页：保存/选中/应用/更新/删除 ——
  document.getElementById('ni_0').click();
  window.__send(dspState(0));
  document.getElementById('pfName').value = '睡前耳机';
  document.getElementById('btnPfSave').click();               // 上报后输入框清空
  curProfiles.rows.push({name:'睡前耳机', stamp:'2026-10-09 22:10', on:false});
  curProfiles.sel = 2;
  window.__send(dspState(0));
  document.querySelectorAll('#pfList .pfrow')[2].click();     // 选中新行
  document.getElementById('btnPfApply').click();
  document.getElementById('btnPfUpdate').click();
  document.getElementById('btnPfDelete').click();
  curProfiles.rows.splice(2, 1);                              // C# 删完回推
  curProfiles.sel = 1;
  window.__send(dspState(0));

  // —— 机架编排页：选中/上移/下移/恢复默认 ——
  document.getElementById('ni_1').click();
  window.__send(dspState(1));
  document.querySelectorAll('#rkList .rkrow')[0].click();     // 选中第 1 行（上移随即禁用）
  curRack.sel = 0;
  window.__send(dspState(1));
  document.getElementById('btnRkDown').click();               // sel=0 只能下移
  curRack.sel = 1;
  window.__send(dspState(1));
  document.getElementById('btnRkUp').click();
  curRack.sel = 0;
  window.__send(dspState(1));
  document.querySelectorAll('#rkList .rkrow')[2].click();     // 选中第 3 行看说明
  curRack.sel = 2;
  curRack.hint = curRack.rows[2].hint;
  window.__send(dspState(1));
  document.getElementById('btnRkReset').click();

  // —— 输入余量页 ——
  document.getElementById('ni_2').click();
  window.__send(dspState(2));
  setRange('c_headroom', -3);
  curCtrl.headroom = -3;
  window.__send(dspState(2));
  setCheck('c_limiter', false);
  curCtrl.limiter = false;
  window.__send(dspState(2));

  // —— 响度 · ReplayGain 页 ——
  document.getElementById('ni_3').click();
  window.__send(dspState(3));
  setSelect('c_rgmode', 0);
  curCtrl.rgmode = 0;
  window.__send(dspState(3));
  setRange('c_rgpreamp', 3);
  curCtrl.rgpreamp = 3;
  window.__send(dspState(3));
  setCheck('c_rgclip', false);
  curCtrl.rgclip = false;
  window.__send(dspState(3));

  // —— SRC · 升频页 ——
  document.getElementById('ni_4').click();
  window.__send(dspState(4));
  setSelect('c_srcrate', 2);
  curCtrl.srcrate = 2;
  window.__send(dspState(4));
  setSelect('c_srcquality', 2);
  curCtrl.srcquality = 2;
  window.__send(dspState(4));
  setSelect('c_srcdither', 3);
  curCtrl.srcdither = 3;
  window.__send(dspState(4));

  // —— 动态压缩器页（八控件全拨） ——
  document.getElementById('ni_6').click();
  window.__send(dspState(6));
  setRange('c_compthreshold', -18); curCtrl.compthreshold = -18;
  setRange('c_compratio', 2.5);     curCtrl.compratio = 2.5;
  setRange('c_compattack', 25);     curCtrl.compattack = 25;
  setRange('c_comprelease', 350);   curCtrl.comprelease = 350;
  setRange('c_compknee', 12);       curCtrl.compknee = 12;
  setRange('c_compmakeup', 4);      curCtrl.compmakeup = 4;
  setRange('c_compmix', 60);        curCtrl.compmix = 60;
  setCheck('c_comppeak', false);    curCtrl.comppeak = false;
  window.__send(dspState(6));

  // —— 声道工具页（含 Crossfeed） ——
  document.getElementById('ni_7').click();
  window.__send(dspState(7));
  setRange('c_chbalance', -0.35);  curCtrl.chbalance = -0.35;
  setRange('c_chlgain', -2);       curCtrl.chlgain = -2;
  setRange('c_chrgain', 2);        curCtrl.chrgain = 2;
  setRange('c_chldelay', 2.5);     curCtrl.chldelay = 2.5;
  setRange('c_chrdelay', 0);       curCtrl.chrdelay = 0;
  setSelect('c_chmono', 3);        curCtrl.chmono = 3;
  setCheck('c_chswap', true);      curCtrl.chswap = true;
  setCheck('c_chinvertl', true);   curCtrl.chinvertl = true;
  setCheck('c_chinvertr', false);  curCtrl.chinvertr = false;
  setRange('c_xfeedlevel', 40);    curCtrl.xfeedlevel = 40;
  setRange('c_xfeedcutoff', 500);  curCtrl.xfeedcutoff = 500;
  window.__send(dspState(7));

  // —— 立体声场页 ——
  document.getElementById('ni_8').click();
  window.__send(dspState(8));
  setRange('c_fieldwidth', 120);   curCtrl.fieldwidth = 120;
  setRange('c_fieldcenter', -3);   curCtrl.fieldcenter = -3;
  setRange('c_fieldside', 2);      curCtrl.fieldside = 2;
  window.__send(dspState(8));

  // —— 声道矩阵页 ——
  document.getElementById('ni_9').click();
  window.__send(dspState(9));
  setRange('c_mll', 1.05);   curCtrl.mll = 1.05;
  setRange('c_mrl', -0.2);   curCtrl.mrl = -0.2;
  setRange('c_mlr', 0.15);   curCtrl.mlr = 0.15;
  setRange('c_mrr', 0.95);   curCtrl.mrr = 0.95;
  window.__send(dspState(9));

  // —— FIR · 房间校正页 ——
  document.getElementById('ni_10').click();
  window.__send(dspState(10));
  document.getElementById('btnOpenRoom').click();
  document.getElementById('btnFirSafe').click();
  setRange('c_roomtrim', -3);
  curCtrl.roomtrim = -3;
  window.__send(dspState(10));

  // —— 输出安全页 ——
  document.getElementById('ni_11').click();
  window.__send(dspState(11));
  document.getElementById('btnMonReset').click();
  window.__send(dspState(11));

  // —— 回到 EQ 页收尾；总旁路开 → 关 ——
  document.getElementById('ni_5').click();
  window.__send(dspState(5));
  setCheck('c_bypass', true);
  setBypass(true);
  window.__send(dspState(5));
  window.__bypassSeen = {
    hint:document.getElementById('pwh_eq').textContent,
    bp:document.getElementById('bpStatus').textContent + '|' + document.getElementById('bpStatus').className,
    pillCls:(document.querySelectorAll('#eqStrip .pill')[1] || {}).className
  };
  setCheck('c_bypass', false);
  setBypass(false);
  window.__send(dspState(5));
  // —— 第三种口径：DSP 全关 → 输出 bit-perfect 直出（琥珀转绿） ——
  curPerfect = '输出 bit-perfect 直出';
  curStrip.perfect = curPerfect;
  window.__send(dspState(5));
  window.__directSeen = {
    bp:document.getElementById('bpStatus').textContent + '|' + document.getElementById('bpStatus').className,
    pillCls:(document.querySelectorAll('#eqStrip .pill')[1] || {}).className
  };
  curPerfect = PERFECT_DSP;
  curStrip.perfect = PERFECT_DSP;
  window.__send(dspState(5));
  // 监控刷新到过载态
  window.__send({kind:'dspmon', mon:{peak:'-0.2 dBFS', clip:'7 次', over:'过载',
                                     active:'EQ · 压缩 · 限幅', chain:'44.1 → 96 kHz'},
                 perfect:PERFECT_DSP});
  // dspmsg 收起
  window.__send({kind:'dspmsg', text:''});

  // —— 播放条：播放/暂停 + 上/下一首 + 拖进度 + 拖音量 ——
  document.getElementById('btnPlay').click();
  document.getElementById('btnNext').click();
  document.getElementById('btnPrev').click();
  firePointer(document.getElementById('track'), 0.5);   // seek = 0.5*227
  firePointer(document.getElementById('vol'), 0.3);     // volume = 0.3
  window.__send({kind:'now', playing:false, position:113.5, duration:227, volume:0.3,
                 track:{t:'晴天', s:'周杰伦 · 叶惠美'}});
  // 主题：深色见一下 → 回浅色（截图保持浅色）
  window.__send({kind:'theme', dark:true, css:''});
  window.__themeDarkSeen = document.documentElement.className;
  window.__send({kind:'theme', dark:false, css:''});
  // 返回主界面
  document.getElementById('btnBack').click();

  window.__dumpDsp('dsp');
}, 200);

/* ---------- dsp 专用状态快照（main.html 的 __T.dump 不读这里的 DOM） ---------- */
window.__dumpDsp = function(tag){
  var $ = function(id){ return document.getElementById(id); };
  var list = function(sel){ return Array.prototype.slice.call(document.querySelectorAll(sel)); };
  var navOn = -1, pgOn = -1, navDots = [], i, el;
  for (i = 0; i < 12; i++){
    navDots.push((el = $('nd_' + i)) ? el.className.indexOf('live') >= 0 : null);
    if ((el = $('ni_' + i)) && el.className.indexOf(' on') >= 0) navOn = i;
    if ((el = $('pg_' + i)) && el.className.indexOf(' on') >= 0) pgOn = i;
  }
  var pwr = {};
  ['headroom','rg','src','eq','opra','comp','xfeed','channel','field','matrix','fir'].forEach(function(k){
    var cb = $('pw_' + k);
    pwr[k] = {on: cb ? cb.checked : null, dis: cb ? cb.disabled : null,
              hint: cb ? ($('pwh_' + k) || {}).textContent : null};
  });
  var ctrl = {};
  ['headroom','limiter','rgmode','rgpreamp','rgclip','srcrate','srcquality','srcdither',
   'eqpreamp','eqbass','eqvocal','eqair','eqwarm','eqpreset',
   'compthreshold','compratio','compattack','comprelease','compknee','compmakeup','compmix','comppeak',
   'chbalance','chlgain','chrgain','chldelay','chrdelay','chmono','chswap','chinvertl','chinvertr',
   'xfeedlevel','xfeedcutoff','fieldwidth','fieldcenter','fieldside',
   'mll','mrl','mlr','mrr','roomtrim'].forEach(function(k){
    var e2 = $('c_' + k);
    if (!e2) return;
    var o = {v: e2.value};
    if (e2.type === 'checkbox') o.ck = e2.checked;
    if (e2.type === 'range') o.rd = ($('v_' + k) || {}).textContent;
    if (e2.tagName === 'SELECT') o.opts = Array.prototype.map.call(e2.options, function(op){ return op.textContent; });
    ctrl[k] = o;
  });
  window.__state = {
    tag: tag,
    navOn: navOn, pgOn: pgOn, navDots: navDots,
    navItemCount: list('#dnav .ni').length,
    navGroupTexts: list('#dnav .grp').map(function(g){ return g.textContent; }),
    pwr: pwr, ctrl: ctrl,
    bypassCk: $('c_bypass') ? $('c_bypass').checked : null,
    bpStatus: ($('bpStatus') || {}).textContent,
    bpStatusCls: ($('bpStatus') || {}).className,
    eqMode: (document.querySelector('#eqMode input:checked') || {}).value,
    eqProVisible: $('eqPro') ? $('eqPro').style.display : null,
    eqSimpleVisible: $('eqSimple') ? $('eqSimple').style.display : null,
    eqBandBoxVisible: $('eqBandBox') ? $('eqBandBox').style.display : null,
    eqPresetSel: $('c_eqpreset') ? $('c_eqpreset').selectedIndex : null,
    eqPresetOpts: $('c_eqpreset') ? Array.prototype.map.call($('c_eqpreset').options, function(o){ return o.textContent; }) : null,
    eqStrip: list('#eqStrip .pill').map(function(p){
      return {t: p.textContent, c: p.className.replace(/\s*pill\s*/, ' ').trim()};
    }),
    eqChips: list('#eqBands .bchip').map(function(c){
      return {on: c.className.indexOf(' on') >= 0, off: !!c.querySelector('.bd.off'), n: c.textContent};
    }),
    eqBand: {
      freq: $('c_eqbandfreq') ? $('c_eqbandfreq').value : null,
      freqRd: ($('v_eqbandfreq') || {}).textContent,
      gain: $('c_eqbandgain') ? $('c_eqbandgain').value : null,
      gainRd: ($('v_eqbandgain') || {}).textContent,
      q: $('c_eqbandq') ? $('c_eqbandq').value : null,
      qRd: ($('v_eqbandq') || {}).textContent,
      ty: $('c_eqbandtype') ? $('c_eqbandtype').value : null,
      on: $('c_eqbandon') ? $('c_eqbandon').checked : null
    },
    pfStatus: ($('pfStatus') || {}).textContent,
    pfSaveHint: ($('pfSaveHint') || {}).textContent,
    pfRows: list('#pfList .pfrow').map(function(r){
      return {nm: (r.querySelector('.nm') || {}).textContent, st: (r.querySelector('.st') || {}).textContent,
              on: r.className.indexOf(' on') >= 0, cur: !!r.querySelector('.cur')};
    }),
    pfApplyDisabled: $('btnPfApply') ? $('btnPfApply').disabled : null,
    pfUpdateDisabled: $('btnPfUpdate') ? $('btnPfUpdate').disabled : null,
    pfDeleteDisabled: $('btnPfDelete') ? $('btnPfDelete').disabled : null,
    pfNameValue: $('pfName') ? $('pfName').value : null,
    rkHint: ($('rkHint') || {}).textContent,
    rkRows: list('#rkList .rkrow').map(function(r){
      return {slot: (r.querySelector('.slot') || {}).textContent, n: (r.querySelector('.nm') || {}).textContent,
              st: (r.querySelector('.stt') || {}).textContent, col: (r.querySelector('.stt') || {}).style.color,
              hint: (r.querySelector('.hint') || {}).textContent, on: r.className.indexOf(' on') >= 0};
    }),
    rkUpDisabled: $('btnRkUp') ? $('btnRkUp').disabled : null,
    rkDownDisabled: $('btnRkDown') ? $('btnRkDown').disabled : null,
    mon: {peak: ($('monPeak') || {}).textContent, clip: ($('monClip') || {}).textContent,
          over: ($('monOver') || {}).textContent, overCls: ($('monOver') || {}).className,
          active: ($('monActive') || {}).textContent, chain: ($('monChain') || {}).textContent},
    srcState: ($('srcState') || {}).textContent, rgInfo: ($('rgInfo') || {}).textContent,
    firStatus: ($('firStatus') || {}).textContent, firClip: ($('firClip') || {}).textContent,
    mxPhase: ($('mxPhase') || {}).textContent,
    dmsgVisible: $('dmsg') ? $('dmsg').style.display : null,
    dmsgText: ($('dmsg') || {}).textContent,
    pbTitle: ($('pbTitle') || {}).textContent, pbSub: ($('pbSub') || {}).textContent,
    pbCovImg: !!document.querySelector('#pbCov img'),
    playIcon: ($('btnPlay') || {}).innerHTML,
    trackFillW: ($('trackFill') || {}).style.width,
    tmCur: ($('tmCur') || {}).textContent, tmDur: ($('tmDur') || {}).textContent,
    volFillW: ($('volFill') || {}).style.width,
    htmlClass: document.documentElement.className,
    themeDarkSeen: window.__themeDarkSeen,
    dmsgSeen: window.__dmsgSeen,
    bypassSeen: window.__bypassSeen,
    directSeen: window.__directSeen,
    posted: window.__posted.slice(),
    errors: window.__wvErrors.slice()
  };
  var sd = document.getElementById('__statedump');
  if (!sd){
    sd = document.createElement('script');
    sd.id = '__statedump';
    sd.type = 'application/json';
    document.body.appendChild(sd);
  }
  sd.textContent = '/*STATE*/' + JSON.stringify(window.__state) + '/*END*/';
  return window.__state;
};
