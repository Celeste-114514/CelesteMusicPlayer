/* ECHO visual layers, rebuilt on Celeste's real kernel data.
   Source geometry: ECHO-main src/renderer/pages/DspPage.tsx (HeadroomPanel, RoomCorrectionPanel,
   ChannelPanel, EchoSrcPanel, SafetyPanel) and components/audio/CompressorPanel.tsx.
   Rule kept from the native page: a reading the kernel cannot supply is not rendered at all,
   instead of showing a fake number. */
(() => {
  'use strict';
  const el=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
  const NS='http://www.w3.org/2000/svg';
  const db=n=>Number.isFinite(Number(n))?(Number(n)>0?'+':'')+Number(n).toFixed(1)+' dB':'—';
  const level=n=>Number.isFinite(Number(n))?Number(n).toFixed(1)+' dB':'—';
  const pct=n=>Math.min(100,Math.max(0,n));
  const clamp=(n,a,b)=>Math.min(b,Math.max(a,n));
  const round1=n=>Math.round(n*10)/10;
  // Minimal stroke glyphs (lucide geometry) so ECHO's icon slots render without a new icon set.
  const GLYPH={
    shield:'M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10z|M9 12l2 2 4-4',
    route:'M4 6h7a4 4 0 0 1 0 8H9a4 4 0 0 0 0 8h10|M17 19l3 3-3 3',
    info:'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z|M12 16v-5|M12 8h.01',
    zap:'M13 2 4 14h7l-1 8 9-12h-7z',
    gauge:'M12 21a9 9 0 1 1 0-18 9 9 0 0 1 0 18z|M12 13l4-4',
    activity:'M3 12h4l3 9 4-18 3 9h4',
    file:'M14 3H7a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h10a2 2 0 0 0 2-2V8z|M14 3v5h5',
    clock:'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z|M12 8v5l3 2',
    wave:'M4 12v-2|M8 12V7|M12 12V4|M16 12V8|M20 12v-3',
    check:'M20 6 9 17l-5-5',
    cpu:'M6 6h12v12H6z|M9 2v2|M15 2v2|M9 20v2|M15 20v2|M2 9h2|M2 15h2|M20 9h2|M20 15h2',
    waves:'M2 12h2l2-4 2 8 2-12 2 16 2-8 2 4h2',
    sliders:'M4 21v-7|M4 10V3|M12 21v-9|M12 8V3|M20 21v-5|M20 12V3|M2 14h4|M10 8h4|M18 16h4',
    speaker:'M6 3h12a1 1 0 0 1 1 1v16a1 1 0 0 1-1 1H6a1 1 0 0 1-1-1V4a1 1 0 0 1 1-1z|M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z|M9 7h.01',
    layers:'M12 2 2 7l10 5 10-5-10-5z|M2 17l10 5 10-5|M2 12l10 5 10-5',
    target:'M12 21a9 9 0 1 0 0-18 9 9 0 0 0 0 18z|M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6z',
    grip:'M9 5h.01|M9 12h.01|M9 19h.01|M15 5h.01|M15 12h.01|M15 19h.01'
  };
  function glyph(name,size=15){const s=document.createElementNS(NS,'svg');s.setAttribute('viewBox','0 0 24 24');s.setAttribute('width',size);s.setAttribute('height',size);s.setAttribute('fill','none');s.setAttribute('stroke','currentColor');s.setAttribute('stroke-width','1.7');s.setAttribute('stroke-linecap','round');s.setAttribute('stroke-linejoin','round');s.setAttribute('aria-hidden','true');((GLYPH[name]||'').split('|')).forEach(d=>{if(!d)return;const p=document.createElementNS(NS,'path');p.setAttribute('d',d);s.append(p);});return s;}

  window.CelesteDspGlyphs=glyph;
  window.CelesteDspVisuals={mount(host,module,controls,info,request,refresh){
    const root=el('section',undefined,'echo-dsp-visual dsp-module-panel dsp-module-panel--'+({fir:'room',comp:'compressor',stereo:'spatial',matrix:'spatial',rg:'headroom'}[module]||module));
    root.dataset.visual=module;host.append(root);
    const values=new Map(controls.map(c=>[c.name,c.kind==='combo'?c.index:c.value]));
    const control=name=>controls.find(c=>c.name===name);
    const v=(name,fallback=0)=>values.get(name)??control(name)?.value??fallback;
    let telemetry={},stopped=false;

    // ---- shared builders -----------------------------------------------------------------
    function metric(label,text,tone){
      const card=el('span',undefined,'dsp-module-metric');
      if(tone)card.dataset.tone=tone;
      card.append(el('em',label),el('strong',text));
      return card;
    }
    function metrics(list,cls='dsp-module-metrics'){
      const box=el('div',undefined,cls);
      list.filter(Boolean).forEach(item=>box.append(item.nodeType===1?item:metric(item[0],item[1],item[2])));
      return box;
    }
    function routeCell(label,text){
      const cell=el('span');cell.append(el('em',label),el('strong',text));return cell;
    }
    function actions(list,cls='dsp-module-actions'){
      const box=el('div',undefined,cls);
      list.filter(Boolean).forEach(([text,fn,opts={}])=>{
        const b=el('button',undefined);b.type='button';
        if(opts.icon)b.append(glyph(opts.icon,14));
        b.append(el('span',text));
        if(opts.active!=null)b.dataset.active=String(!!opts.active);
        if(opts.disabled)b.disabled=true;
        b.addEventListener('click',async()=>{b.disabled=true;try{await fn();}catch(e){window.CelesteShell.notify(e.message);}finally{if(b.isConnected)b.disabled=!!opts.disabled;}});
        box.append(b);
      });
      return box;
    }
    function rangeRow(label,name,write){
      const c=control(name),row=el('label',undefined,'dsp-module-range');
      const input=el('input');input.type='range';
      input.min=c?.min??-24;input.max=c?.max??0;input.step=c?.step??.1;input.value=Number(v(name,c?.value??0));
      input.setAttribute('aria-label',label);input.disabled=c?c.enabled===false:false;
      const out=el('strong',db(input.value));
      input.addEventListener('input',()=>{out.textContent=db(input.value);});
      input.addEventListener('change',async()=>{try{await write(Number(input.value));}catch(e){window.CelesteShell.notify(e.message);}});
      row.append(el('span',label),input,out);
      return row;
    }
    async function set(name,value){
      const c=control(name);
      await request('setControl',{module,name,...(c?.kind==='combo'?{index:value}:{value})});
      values.set(name,value);
    }
    function hint(text){return el('p',text,'dsp-module-note');}

    // ---- Headroom ------------------------------------------------------------------------
    // ECHO: status + 7 metrics + reserve slider + budget, with an assist column carrying the
    // next step, a recommendation, the two safe actions and three reserve modes.
    function headroom(){
      const hdb=Number(v('AudioFxSafetyHeadroomSlider',0)),limiter=!!v('AudioFxSafetyLimiterToggle');
      const playing=!!telemetry.session,peak=playing&&Number.isFinite(Number(telemetry.peakDb))?Number(telemetry.peakDb):null;
      const clips=Number(telemetry.clipCount)||0,live=peak===null?null:-peak;
      const tone=clips>0||(peak!==null&&peak>-1)?'risk':(peak!==null&&peak>-6)?'warn':'good';
      const statusTitle=telemetry.bypass?'总旁路':!limiter?'保护关闭':peak===null?'待命 · 等待信号':(tone==='risk'?'余量不足':'保护已就绪');
      const reason=peak===null?'开始播放后显示实时输出电平。':peak>=0?'输出已达到满刻度，请增加余量。':peak>-6?'余量偏紧，再留 3 dB 更稳。':'当前输出未达到满刻度。';
      const recommended=peak===null?null:round1(Math.min(0,Math.max(-24,-peak-6)));

      const controlBox=el('div',undefined,'dsp-headroom-control');
      const status=el('div',undefined,'dsp-headroom-status');status.dataset.tone=tone;
      const statusText=el('span');statusText.append(el('em','当前状态'),el('strong',statusTitle));
      status.append(statusText,el('p',reason));
      controlBox.append(status);

      controlBox.append(metrics([
        ['输入余量',db(hdb)],
        ['输出峰值',peak===null?'—':level(peak),tone==='risk'?'risk':undefined],
        ['实时余量',live===null?'—':level(live),live!==null&&live<1?'risk':live!==null&&live<6?'warn':'good'],
        ['削波次数',String(clips),clips>0?'risk':'good']
      ],'dsp-module-metrics dsp-headroom-metrics'));

      controlBox.append(rangeRow('输入余量','AudioFxSafetyHeadroomSlider',async value=>{await set('AudioFxSafetyHeadroomSlider',value);refresh();}));

      const budget=el('div',undefined,'dsp-headroom-budget');budget.setAttribute('aria-label','Headroom 预算');
      const inputPeak=peak===null?null:round1(peak+hdb);
      [['输入峰值',inputPeak===null?'未播放':level(inputPeak),inputPeak,null],
       ['输出峰值',peak===null?'未播放':level(peak),peak,tone],
       ['实时余量',live===null?'未播放':level(live),live,live!==null&&live<1?'risk':live!==null?'good':undefined]].forEach(([label,text,value,rowTone])=>{
        const row=el('span');row.append(el('em',label),el('strong',text));
        if(value!==null)row.style.width=Math.max(12,pct((value+24)*3.3))+'%';
        if(rowTone)row.dataset.tone=rowTone;
        budget.append(row);
      });
      controlBox.append(budget,actions([['0 dB',()=>set('AudioFxSafetyHeadroomSlider',0).then(refresh),{active:Math.abs(hdb)<=.05}],['-3 dB',()=>set('AudioFxSafetyHeadroomSlider',-3).then(refresh),{active:Math.abs(hdb+3)<=.05}],['-6 dB',()=>set('AudioFxSafetyHeadroomSlider',-6).then(refresh),{active:Math.abs(hdb+6)<=.05}],['-9 dB',()=>set('AudioFxSafetyHeadroomSlider',-9).then(refresh),{active:Math.abs(hdb+9)<=.05}]]));

      const assist=el('aside',undefined,'dsp-headroom-assist');
      const next=el('div',undefined,'dsp-headroom-next-step');next.dataset.tone=tone;
      const nextText=el('span');nextText.append(el('em','建议下一步'),el('strong',peak===null?'开始播放以读取电平':tone==='risk'?'降低前级增益':tone==='warn'?'再留 3 dB 余量':'保持当前设置'));
      next.append(nextText,el('p',peak===null?'播放后这里会给出基于实时峰值的建议。':tone==='risk'?'输出已经贴近满刻度，先把保留余量拉到 -6 dB 以下。':tone==='warn'?'当前余量偏紧，后续模块一加增益就会顶到 0 dBFS。':'限制器只在超过满刻度时动作，当前信号安全。'));
      next.append(actions([['设为 -6 dB',()=>set('AudioFxSafetyHeadroomSlider',-6).then(refresh),{icon:'shield',disabled:Math.abs(hdb+6)<=.05}],['刷新',()=>refresh(),{icon:'activity'}]]));
      assist.append(next);

      const recommend=el('div',undefined,'dsp-headroom-recommendation');recommend.dataset.active=String(recommended!==null&&Math.abs(recommended-hdb)>.2);
      recommend.append(el('em','推荐余量'),el('strong',recommended===null?'暂无风险':'安全值 '+db(recommended)));
      recommend.append(actions([['应用推荐值',()=>set('AudioFxSafetyHeadroomSlider',recommended).then(refresh),{icon:'gauge',disabled:recommended===null||Math.abs(recommended-hdb)<=.2}]]));
      assist.append(recommend);

      const safe=el('div',undefined,'dsp-headroom-safe-actions');
      const safeText=el('span');safeText.append(el('em','安全动作'),el('strong','宁可少 3 dB，也不要削波'));
      safe.append(safeText,actions([
        [limiter?'限幅器 开':'限幅器 关',async()=>{await set('AudioFxSafetyLimiterToggle',!limiter);refresh();},{icon:'shield',active:limiter}],
        ['设为 -9 dB（保守）',()=>set('AudioFxSafetyHeadroomSlider',-9).then(refresh),{icon:'shield',active:Math.abs(hdb+9)<=.05}],
        ['设为 -12 dB（极保守）',()=>set('AudioFxSafetyHeadroomSlider',-12).then(refresh),{icon:'shield',active:Math.abs(hdb+12)<=.05}]
      ]));
      assist.append(safe);

      const modes=el('div',undefined,'dsp-headroom-modes');modes.setAttribute('role','group');modes.setAttribute('aria-label','余量模式');
      [['干净',-9,'动态大的电子与管风琴几乎不会顶到 0 dB'],['平衡',-6,'大多数流行、摇滚与人声的默认选择'],['响亮',-3,'安静的老录音可以少留一点']].forEach(([title,value,detail])=>{
        const b=el('button');b.type='button';b.dataset.active=String(Math.abs(hdb-value)<=.05);
        b.append(el('strong',title),el('em',db(value)),el('span',detail));
        b.addEventListener('click',async()=>{b.disabled=true;try{await set('AudioFxSafetyHeadroomSlider',value);refresh();}catch(e){window.CelesteShell.notify(e.message);b.disabled=false;}});
        modes.append(b);
      });
      assist.append(modes,hint('保留余量在进入处理链之前生效；后面的 EQ、压缩器加的增益都从这里扣。'));

      const main=el('div',undefined,'dsp-headroom-main');main.append(controlBox,assist);root.append(main);
    }

    // ---- Room correction (FIR) -----------------------------------------------------------
    function room(){
      const enabled=!!v('RoomIrEnabledToggle'),hasIr=!!info.irName,risk=!!info.clippingRisk;
      const tone=risk?'risk':enabled?'good':hasIr?'warn':'good';
      const trim=Number(v('RoomTrimSlider',0));
      const heroTitle=enabled?'卷积正在处理':hasIr?'脉冲响应已就绪':'尚未导入脉冲响应';
      const heroDetail=enabled?'房间校正已接入处理链，输出前请确认没有削波。':hasIr?'启用前会先把预衰减放到安全值。':'选择 PCM WAV 脉冲响应，校正房间或耳机响应。';
      const nextTitle=risk?'先降低预衰减':enabled?'听一下确认效果':hasIr?'安全启用卷积':'导入脉冲响应';
      const nextDetail=risk?'当前输出有削波风险，把预衰减拉到 -6 dB 以下再看。':enabled?'对比开启与旁路的差别，确认低频没有被过度吸收。':hasIr?'安全启用会自动带一段预衰减，避免卷积后过载。':'支持单声道与立体声 WAV，长度越长解析力越高。';

      const main=el('div',undefined,'dsp-room-main'),hero=el('div',undefined,'dsp-room-hero'),primary=el('div',undefined,'dsp-room-primary');
      const heroSpan=el('span');heroSpan.append(el('em','卷积状态'),el('strong',heroTitle),el('small',heroDetail));
      primary.append(heroSpan,actions([
        ['导入 IR…',()=>request('firImport').then(refresh),{icon:'file'}],
        ['安全启用',()=>set('RoomIrEnabledToggle',true).then(()=>set('RoomTrimSlider',Math.min(trim,-6))).then(refresh),{icon:'shield',disabled:!hasIr||enabled}],
        [enabled?'停用卷积':'启用卷积',()=>set('RoomIrEnabledToggle',!enabled).then(refresh),{icon:'zap',active:enabled,disabled:!hasIr}],
        ['清除 IR',()=>request('firClear').then(refresh),{disabled:!hasIr}]
      ]));
      hero.append(el('p',heroDetail),primary);main.append(hero);

      main.append(rangeRow('预衰减','RoomTrimSlider',async value=>{await set('RoomTrimSlider',value);refresh();}));
      const trimTools=el('div',undefined,'dsp-room-trim-tools');
      trimTools.append(el('span','快捷预衰减'));
      [-6,-3,0].forEach(value=>{const b=el('button',db(value));b.type='button';b.dataset.active=String(Math.abs(trim-value)<=.05);b.disabled=!hasIr;b.addEventListener('click',async()=>{b.disabled=true;try{await set('RoomTrimSlider',value);refresh();}catch(e){window.CelesteShell.notify(e.message);b.disabled=false;}});trimTools.append(b);});
      main.append(trimTools);

      main.append(rangeRow('输出增益','RoomIrGainSlider',async value=>{await set('RoomIrGainSlider',value);refresh();}));
      main.append(metrics([
        ['IR 文件',info.irName||'尚未导入',hasIr?'good':undefined],
        ['声道',info.channels?info.channels+' 声道':'—'],
        ['脉冲长度',info.taps?info.taps+' taps':'—'],
        ['采样率',info.sampleRate?info.sampleRate+' Hz':'—']
      ],'dsp-module-metrics dsp-room-metrics'),hint('卷积会带来延迟与电平变化；预衰减在卷积之前，输出增益在之后。'));

      const side=el('aside',undefined,'dsp-room-side');
      const status=el('div',undefined,'dsp-room-status');status.dataset.tone=tone;
      const statusHead=el('span');statusHead.append(glyph('shield',17),el('em','安全状态'));
      status.append(statusHead,el('strong',risk?'检测到削波风险':enabled?'信号受保护':'尚未介入'),el('p',risk?'卷积后的输出接近满刻度，先降低预衰减。':enabled?'限制器在末端兜底，卷积已经参与处理。':'导入并启用后，这里会显示实时保护状态。'));
      side.append(status);

      const route=el('div',undefined,'dsp-room-route');
      const routeHead=el('span');routeHead.append(glyph('route',16),el('em','处理路径'));
      const dl=el('dl');
      [['直通状态',enabled?'被 DSP 关闭':'直通'],['输入余量',db(telemetry.headroom??0)],['削波',risk?'检测到风险':'未发现'],['最新采样率',info.sampleRate?info.sampleRate+' Hz':'—']].forEach(([term,value])=>{const row=el('div');row.append(el('dt',term),el('dd',value));dl.append(row);});
      route.append(routeHead,dl);side.append(route);

      const next=el('div',undefined,'dsp-room-next');
      const nextHead=el('span');nextHead.append(glyph('info',16),el('em','建议下一步'));
      next.append(nextHead,el('strong',nextTitle),el('p',nextDetail));
      const nextActions=el('div',undefined,'dsp-room-next-actions');
      if(!hasIr){const b=el('button');b.append(glyph('file',14),el('span','导入 IR…'));b.addEventListener('click',()=>request('firImport').then(refresh));nextActions.append(b);}
      else if(risk){const b=el('button');b.append(glyph('gauge',14),el('span','压实到 -6 dB'));b.addEventListener('click',async()=>{await set('RoomTrimSlider',-6);refresh();});nextActions.append(b);}
      else if(enabled){const b=el('button');b.append(glyph('zap',14),el('span','旁路对比'));b.dataset.active='true';b.addEventListener('click',async()=>{await set('RoomIrEnabledToggle',false);refresh();});nextActions.append(b);}
      else{const b=el('button');b.append(glyph('shield',14),el('span','安全启用'));b.addEventListener('click',async()=>{await set('RoomIrEnabledToggle',true);await set('RoomTrimSlider',-6);refresh();});nextActions.append(b);}
      const again=el('button');again.append(glyph('activity',14),el('span','刷新'));again.addEventListener('click',()=>refresh());nextActions.append(again);
      next.append(nextActions);side.append(next);

      const expand=el('div',undefined,'dsp-room-expansion');
      const ex1=el('span');ex1.append(glyph('clock',15),el('em','最近的 IR'));ex1.append(el('small',info.irName?info.irName:'还没有使用记录'));
      const ex2=el('span');ex2.append(glyph('wave',15),el('em','实测响应'));ex2.append(el('small','内核未提供，暂不显示'));
      expand.append(ex1,ex2);side.append(expand);

      root.append(main,side);
    }

    // ---- Channel -------------------------------------------------------------------------
    function channel(){
      const balance=Number(v('AudioFxChannelBalanceSlider',0));
      const lg=Number(v('AudioFxChannelLeftGainSlider',0)),rg=Number(v('AudioFxChannelRightGainSlider',0));
      const left=lg+20*Math.log10(Math.max(.000001,balance>0?1-balance:1)),right=rg+20*Math.log10(Math.max(.000001,balance<0?1+balance:1));
      const mono=control('AudioFxChannelMonoCombo'),monoIndex=Number(v('AudioFxChannelMonoCombo',0));
      const skew=right-left,delaySkew=Number(v('AudioFxChannelRightDelaySlider',0))-Number(v('AudioFxChannelLeftDelaySlider',0));

      const main=el('div',undefined,'dsp-channel-main'),hero=el('div',undefined,'dsp-channel-hero');
      const primary=el('div',undefined,'dsp-channel-primary');
      primary.append(el('strong','左右声道增益、平衡与延迟'),el('small','偏移条按当前增益与平衡参数合算，不改动声音。'));
      hero.append(primary);main.append(hero);

      const card=el('div',undefined,'dsp-channel-bias-card'),head=el('div',undefined,'dsp-channel-bias-head'),meter=el('div',undefined,'dsp-channel-bias-meter'),valueRow=el('div',undefined,'dsp-channel-bias-values');
      head.append(el('span','左输出'),el('strong',Math.abs(skew)<.05?'居中':(left>right?'偏左 ':'偏右 ')+Math.abs(skew).toFixed(1)+' dB'),el('span','右输出'));
      const total=10**(left/20)+10**(right/20),lb=el('span'),rb=el('span');
      lb.dataset.side='left';rb.dataset.side='right';
      lb.style.width=pct(10**(left/20)/total*100)+'%';rb.style.width=pct(10**(right/20)/total*100)+'%';
      meter.append(lb,el('i'),rb);
      valueRow.append(el('strong',db(left)),el('strong',db(right)));
      card.append(head,meter,valueRow);main.append(card);

      main.append(metrics([
        ['左延迟',Number(v('AudioFxChannelLeftDelaySlider',0)).toFixed(2)+' ms'],
        ['右延迟',Number(v('AudioFxChannelRightDelaySlider',0)).toFixed(2)+' ms'],
        ['延迟差',(delaySkew>0?'+':'')+delaySkew.toFixed(2)+' ms',Math.abs(delaySkew)>.05?'warn':undefined]
      ],'dsp-module-metrics dsp-channel-metrics'));

      const side=el('aside',undefined,'dsp-channel-side');
      side.append(metrics([['左输出',db(left)],['右输出',db(right)],['延迟差',(delaySkew>0?'+':'')+delaySkew.toFixed(2)+' ms']],'dsp-module-metrics dsp-channel-summary'));

      const tools1=el('div',undefined,'dsp-channel-tools');
      const t1=el('span');t1.append(glyph('info',15),el('em','耳机交叉馈送'));
      tools1.append(t1,el('p',(v('AudioFxChannelCrossfeedToggle')?'Crossfeed 已启用：':'Crossfeed 关闭：')+'用截止 '+(Number(v('AudioFxChannelCrossfeedCutoffSlider',700))|0)+' Hz、强度 '+Math.round(Number(v('AudioFxChannelCrossfeedSlider',0))*100)+'%，只在低频收窄左右差异。'));
      side.append(tools1);

      const monoBox=el('div',undefined,'dsp-channel-tools');
      monoBox.append(el('em','单声道'));
      const monoHint=el('span');monoHint.append(glyph('info',15),el('em','合成为单声道后左右完全相同，用来检查相位问题'));
      monoBox.append(monoHint);
      if(mono)monoBox.append(actions((mono.items||[]).map((text,index)=>[text,async()=>{await set('AudioFxChannelMonoCombo',index);refresh();},{active:monoIndex===index}])));
      side.append(monoBox);

      const phase=el('div',undefined,'dsp-channel-tools');
      phase.append(el('em','相位与交换'));
      phase.append(actions([
        ['交换左右',async()=>{await set('AudioFxChannelSwapToggle',!v('AudioFxChannelSwapToggle'));refresh();},{active:!!v('AudioFxChannelSwapToggle')}],
        ['左反相',async()=>{await set('AudioFxChannelInvertLToggle',!v('AudioFxChannelInvertLToggle'));refresh();},{active:!!v('AudioFxChannelInvertLToggle')}],
        ['右反相',async()=>{await set('AudioFxChannelInvertRToggle',!v('AudioFxChannelInvertRToggle'));refresh();},{active:!!v('AudioFxChannelInvertRToggle')}]
      ]));
      side.append(phase,hint('偏移条只是读数：声音仍然由左边的增益、平衡与延迟滑块决定。'));

      root.append(main,side);
    }

    // ---- SRC -----------------------------------------------------------------------------
    function src(){
      const rate=control('SrcRateCombo'),quality=control('SrcQualityCombo'),dither=control('SrcDitherCombo');
      const rateIndex=Number(v('SrcRateCombo',0));
      const panel=el('div',undefined,'dsp-src-advanced-panel');

      const head=el('div',undefined,'dsp-src-advanced-head');
      const headSpan=el('span');headSpan.append(glyph('waves',16),el('strong','采样率转换'));
      head.append(headSpan,el('small',telemetry.chain||info.src||'未播放'));
      panel.append(head);

      const sw=el('div',undefined,'dsp-src-mode-switch');
      const swText=el('span');swText.append(glyph('info',15),el('em','原采样率 = 直通，内核不做任何重采样'));
      sw.append(swText,actions([
        ['跟随源文件',async()=>{await set('SrcRateCombo',0);refresh();},{active:rateIndex===0}],
        ['固定输出',async()=>{await set('SrcRateCombo',Math.max(1,rateIndex));refresh();},{active:rateIndex!==0}]
      ],'dsp-module-actions'));
      panel.append(sw);

      if(rate){
        const group=el('div',undefined,'dsp-src-advanced-group');
        const title=el('div',undefined,'dsp-src-advanced-title');title.append(glyph('cpu',16),el('strong','目标采样率'));
        const grid=el('div',undefined,'dsp-src-compute-grid');
        grid.setAttribute('role','group');grid.setAttribute('aria-label','目标采样率');
        rate.items.forEach((text,index)=>{
          const b=el('button',undefined,'dsp-src-compute-card');b.type='button';b.dataset.active=String(rateIndex===index);b.disabled=rate.enabled===false;
          const top=el('span');top.append(glyph('cpu',15),el('strong',text));
          if(rateIndex===index)top.append(glyph('check',15));
          b.append(top,el('small',index===0?'不重采样，保持位深与采样率原样':text+' 目标；升频不增加信息量，只改变重采样路径'));
          b.addEventListener('click',async()=>{b.disabled=true;try{await set('SrcRateCombo',index);refresh();}catch(e){window.CelesteShell.notify(e.message);b.disabled=false;}});
          grid.append(b);
        });
        group.append(title,grid);panel.append(group);
      }

      [['重采样质量',quality,'SrcQualityCombo','waves'],['抖动',dither,'SrcDitherCombo','activity']].forEach(([label,c,name,icon])=>{
        if(!c)return;
        const group=el('div',undefined,'dsp-src-advanced-group');
        const title=el('div',undefined,'dsp-src-advanced-title');title.append(glyph(icon,16),el('strong',label));
        const grid=el('div',undefined,'dsp-src-filter-grid');grid.setAttribute('role','group');grid.setAttribute('aria-label',label);
        c.items.forEach((text,index)=>{
          const b=el('button',undefined,'dsp-src-filter-card');b.type='button';b.dataset.active=String(Number(v(name,0))===index);b.disabled=c.enabled===false;
          const top=el('span');top.append(el('strong',text));
          if(Number(v(name,0))===index)top.append(glyph('check',15));
          b.append(top);b.addEventListener('click',async()=>{b.disabled=true;try{await set(name,index);refresh();}catch(e){window.CelesteShell.notify(e.message);b.disabled=false;}});
          grid.append(b);
        });
        group.append(title,grid);panel.append(group);
      });

      panel.append(metrics([
        ['当前播放链路',telemetry.chain||info.src||'未播放'],
        ['目标采样率',rate?.items[rateIndex]||'原采样率'],
        ['重采样质量',quality?.items[Number(v('SrcQualityCombo',0))]||'—'],
        ['抖动',dither?.items[Number(v('SrcDitherCombo',0))]||'—']
      ]));
      panel.append(hint('采样率设置在下次起播生效；上方显示的是当前播放实际链路。'));
      root.append(panel);
    }

    // ---- Compressor ----------------------------------------------------------------------
    function comp(){
      const off=!v('DspCompToggle'),threshold=Number(v('DspCompThresholdSlider',-12)),ratio=Number(v('DspCompRatioSlider',1));
      const gr=Math.abs(Number(telemetry.compressorGr)||0),grDb=gr;
      const header=el('div',undefined,'dsp-compressor-header');
      const headText=el('div');headText.append(el('span','动态'),el('h2','增益衰减表'),el('p','柱高就是当前被压掉的 dB 数；压缩器关闭时保持为空。'));
      const power=el('button',undefined,'dsp-compressor-power');power.type='button';power.dataset.active=String(!off);
      power.append(glyph('activity',16),el('span',off?'已旁路':'已启用'));
      power.addEventListener('click',async()=>{power.disabled=true;try{await set('DspCompToggle',off);refresh();}catch(e){window.CelesteShell.notify(e.message);power.disabled=false;}});
      header.append(headText,power);root.append(header);

      const meter=el('div',undefined,'dsp-compressor-meter');meter.dataset.active=String(!off&&telemetry.session);
      meter.append(el('span','当前增益衰减'),el('strong',off?'旁路':grDb>0.05?'-'+grDb.toFixed(1)+' dB':'0.0 dB'));
      root.append(meter);

      const bars=el('div',undefined,'dsp-compressor-scale');bars.setAttribute('aria-label','增益衰减刻度');
      [0,3,6,9,12,18].forEach(mark=>{
        const cell=el('span');cell.dataset.active=String(grDb>=mark);
        const bar=el('i');bar.style.height=Math.max(11,pct(mark/18*100))+'%';
        cell.append(bar,el('em',mark===0?'0':'-'+mark));
        bars.append(cell);
      });
      root.append(bars);

      root.append(metrics([
        ['阈值',db(threshold)],
        ['压缩比',ratio.toFixed(1)+':1'],
        ['启动 / 释放',Number(v('DspCompAttackSlider',0)).toFixed(1)+' / '+Number(v('DspCompReleaseSlider',0)).toFixed(0)+' ms'],
        ['拐点',db(v('DspCompKneeSlider',0))],
        ['补偿增益',db(v('DspCompMakeupSlider',0))],
        ['干湿比',Math.round(Number(v('DspCompMixSlider',100)))+' %']
      ]));
      root.append(actions([['恢复默认',async()=>{await set('DspCompThresholdSlider',-12);await set('DspCompRatioSlider',4);refresh();}],['峰值检测',async()=>{await set('DspCompPeakToggle',!v('DspCompPeakToggle'));refresh();},{active:!!v('DspCompPeakToggle')}]],'dsp-compressor-actions'));
    }

    // ---- Stereo field / Matrix / ReplayGain ----------------------------------------------
    function stereo(){
      const width=Number(v('DspFieldWidthSlider',100)),center=Number(v('DspFieldCenterSlider',0)),side=Number(v('DspFieldSideSlider',0));
      const field=el('div',undefined,'dsp-field-stage');field.setAttribute('aria-label','声场宽度示意');
      const spread=el('div',undefined,'dsp-field-spread');
      const left=el('i'),right=el('i');
      const half=clamp(width/2,6,50);
      left.style.left=(50-half)+'%';right.style.left=(50+half-6)+'%';
      spread.append(left,el('b'),right);
      field.append(spread,el('p','宽度 '+Math.round(width)+'% 决定左右展开幅度；中置与侧向增益再微调比例。'));
      root.append(field);
      root.append(metrics([['声场宽度',Math.round(width)+'%'],['中置增益',db(center)],['侧向增益',db(side)]]));
      root.append(actions([['100%（原始）',async()=>{await set('DspFieldWidthSlider',100);refresh();},{active:Math.abs(width-100)<.5}],['130%（加宽）',async()=>{await set('DspFieldWidthSlider',130);refresh();},{active:Math.abs(width-130)<.5}],['70%（收窄）',async()=>{await set('DspFieldWidthSlider',70);refresh();},{active:Math.abs(width-70)<.5}]]));
      root.append(hint('加宽会同时抬高高频侧向能量，过头时人声会显得空。'));
    }
    function matrix(){
      const keys=['DspMatrixLlSlider','DspMatrixRlSlider','DspMatrixLrSlider','DspMatrixRrSlider'];
      const [ll,rl,lr,rr]=keys.map(k=>Number(v(k,0)));
      const grid=el('div',undefined,'dsp-matrix-map');grid.setAttribute('aria-label','2 × 2 路由系数');
      [['输入 L',ll,lr],['输入 R',rl,rr]].forEach(([rowLabel,a,b])=>{
        const row=el('div');
        row.append(el('em',rowLabel),el('span',Number(a).toFixed(2)),el('span',Number(b).toFixed(2)));
        grid.append(row);
      });
      const header=el('div',undefined,'dsp-matrix-head');
      header.append(el('span',''),el('em','→ 输出 L'),el('em','→ 输出 R'));
      const box=el('div',undefined,'dsp-matrix-box');box.append(header,grid);
      root.append(box);
      root.append(metrics([['L → L',ll.toFixed(2)],['R → L',rl.toFixed(2)],['L → R',lr.toFixed(2)],['R → R',rr.toFixed(2)]]));
      root.append(actions([['直通',async()=>{await Promise.all([[keys[0],1],[keys[1],0],[keys[2],0],[keys[3],1]].map(([k,val])=>set(k,val)));refresh();},{active:ll===1&&rl===0&&lr===0&&rr===1}],['左右交换',async()=>{await Promise.all([[keys[0],0],[keys[1],1],[keys[2],1],[keys[3],0]].map(([k,val])=>set(k,val)));refresh();},{active:ll===0&&rl===1&&lr===1&&rr===0}],['等增益单声道',async()=>{await Promise.all(keys.map(k=>set(k,.5)));refresh();},{active:ll===.5&&rl===.5&&lr===.5&&rr===.5}]],'dsp-spatial-presets'));
      root.append(hint('系数 1.0 表示原样通过，0 表示不参与，-1 表示反相；超过 1 会放大。'));
    }
    function rg(){
      const mode=control('AudioFxRgModeCombo'),index=Number(v('AudioFxRgModeCombo',0));
      const preamp=Number(v('AudioFxRgPreampSlider',0)),guard=!!v('AudioFxRgPreventClippingToggle');
      const bar=el('div',undefined,'dsp-rg-meter');
      const track=el('div',undefined,'dsp-rg-track'),fill=el('i');
      fill.style.width=pct(Math.abs(preamp)/24*100)+'%';
      fill.dataset.sign=preamp<0?'minus':'plus';
      track.append(el('b'),fill);
      bar.append(el('em','预增益'),track,el('strong',db(preamp)));
      root.append(bar);
      root.append(metrics([['响度模式',mode?.items[index]||'关闭'],['预增益',db(preamp)],['峰值保护',guard?'开启':'关闭',guard?'good':undefined]]));
      root.append(actions((mode?.items||[]).map((text,i)=>[text,async()=>{await set('AudioFxRgModeCombo',i);refresh();},{active:index===i}])),hint('按文件中的 ReplayGain 标签调整响度；缺少标签的曲目沿用原生引擎策略。'));
    }

    const BUILDERS={headroom,channel,comp,stereo,matrix,src,fir:room,rg};
    function draw(){
      if(stopped)return;
      root.replaceChildren();
      const build=BUILDERS[module];
      if(build)build();
    }
    draw();
    return {
      update(name,value){values.set(name,value);if(!root.contains(document.activeElement))draw();},
      telemetry(data){telemetry=data;if(module==='src')info.src=data.chain;if(!root.contains(document.activeElement))draw();},
      dispose(){stopped=true;}
    };
  }};
})();
