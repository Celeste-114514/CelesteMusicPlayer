/* Adapted from ECHO src/renderer/pages/DspPage.tsx and components/audio:
   Headroom budget, channel bias, room route, safety route, compressor GR and spatial presets.
   Data is supplied by Celeste; unsupported ECHO telemetry is explicitly left unavailable. */
(() => {
  'use strict';
  const el=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
  const db=n=>Number.isFinite(Number(n))?(Number(n)>0?'+':'')+Number(n).toFixed(1)+' dB':'—';
  const pct=n=>Math.min(100,Math.max(0,n));
  window.CelesteDspVisuals={mount(host,module,controls,info,request,refresh){
    const values=new Map(controls.map(c=>[c.name,c.kind==='combo'?c.index:c.value])),root=el('section',undefined,'echo-dsp-visual');root.dataset.visual=module;host.append(root);
    const v=(name,fallback=0)=>values.get(name)??fallback;
    let telemetry={},stopped=false;
    function cards(entries,cls='dsp-module-metrics'){const box=el('div',undefined,cls);entries.forEach(([label,text])=>{const card=el('span',undefined,cls==='dsp-safety-route'?'':'dsp-module-metric');card.append(el('em',label),el('strong',text));box.append(card);});return box;}
    function button(text,action){const b=el('button',text,'eq-pill');b.addEventListener('click',async()=>{b.disabled=true;try{await action();refresh();}catch(e){window.CelesteShell.notify(e.message);}finally{b.disabled=false;}});return b;}
    async function set(name,value){await request('setControl',{module,name,value});values.set(name,value);draw();}
    function draw(){
      if(stopped)return;root.replaceChildren();
      if(module==='headroom'){
        const headroom=Number(v('AudioFxSafetyHeadroomSlider')),peak=telemetry.session?Number(telemetry.peakDb):null;
        root.append(cards([['保留余量',db(headroom)],['输出峰值',peak===null?'—':db(peak)],['削波次数',String(telemetry.clipCount??0)],['保护限制器',v('AudioFxSafetyLimiterToggle')?'待命 / 保护':'关闭']]));
        const budget=el('div',undefined,'dsp-headroom-budget');budget.setAttribute('aria-label','Headroom 预算');
        [['输入峰值','当前内核未提供输入测点',null],['输出峰值',peak===null?'未播放':db(peak),peak],['保留余量',db(headroom),-headroom-24]].forEach(([name,text,value])=>{const row=el('span');row.append(el('em',name),el('strong',text));if(value!==null)row.style.width=Math.max(6,Math.min(100,(value+24)*3.3))+'%';budget.append(row);});root.append(budget);
        const actions=el('div',undefined,'echo-actions');[0,-3,-6,-9].forEach(value=>actions.append(button(db(value),()=>set('AudioFxSafetyHeadroomSlider',value))));root.append(actions);
      }else if(module==='channel'){
        // ECHO's bias card with Celeste's linear balance law and channel gains.
        const balance=Number(v('AudioFxChannelBalanceSlider')),left=Number(v('AudioFxChannelLeftGainSlider'))+20*Math.log10(Math.max(.000001,balance>0?1-balance:1)),right=Number(v('AudioFxChannelRightGainSlider'))+20*Math.log10(Math.max(.000001,balance<0?1+balance:1));
        const card=el('div',undefined,'dsp-channel-bias-card'),head=el('div',undefined,'dsp-channel-bias-head'),meter=el('div',undefined,'dsp-channel-bias-meter'),output=el('div',undefined,'dsp-channel-bias-values');head.append(el('span','左输出'),el('strong',Math.abs(left-right)<.05?'居中':(left>right?'偏左 ':'偏右 ')+Math.abs(left-right).toFixed(1)+' dB'),el('span','右输出'));
        const total=10**(left/20)+10**(right/20),l=el('span'),r=el('span');l.dataset.side='left';r.dataset.side='right';l.style.width=pct(10**(left/20)/total*100)+'%';r.style.width=pct(10**(right/20)/total*100)+'%';meter.append(l,el('i'),r);output.append(el('strong',db(left)),el('strong',db(right)));card.append(head,meter,output);root.append(card);
        root.append(cards([['左延迟',Number(v('AudioFxChannelLeftDelaySlider')).toFixed(2)+' ms'],['右延迟',Number(v('AudioFxChannelRightDelaySlider')).toFixed(2)+' ms'],['Crossfeed',v('AudioFxChannelCrossfeedToggle')?'已启用':'关闭']]));
      }else if(module==='comp'){
        const meter=el('div',undefined,'dsp-compressor-meter');meter.dataset.active=String(!!v('DspCompToggle'));meter.append(el('span','当前增益衰减'),el('strong',db(-(Number(telemetry.compressorGr)||0))));root.append(meter);
        root.append(cards([['阈值',db(v('DspCompThresholdSlider'))],['压缩比',Number(v('DspCompRatioSlider',1)).toFixed(1)+':1'],['启动 / 释放',Number(v('DspCompAttackSlider')).toFixed(1)+' / '+Number(v('DspCompReleaseSlider')).toFixed(0)+' ms']]));
      }else if(module==='stereo'){
        root.append(cards([['声场宽度',Math.round(v('DspFieldWidthSlider',100))+'%'],['中心增益',db(v('DspFieldCenterSlider'))],['侧声道增益',db(v('DspFieldSideSlider'))]]));
      }else if(module==='matrix'){
        root.append(cards([['L → L',Number(v('DspMatrixLlSlider',1)).toFixed(2)],['R → L',Number(v('DspMatrixRlSlider')).toFixed(2)],['L → R',Number(v('DspMatrixLrSlider')).toFixed(2)],['R → R',Number(v('DspMatrixRrSlider',1)).toFixed(2)]]));
        const actions=el('div',undefined,'dsp-spatial-presets');[['直通',[1,0,0,1]],['左右交换',[0,1,1,0]],['等增益单声道',[.5,.5,.5,.5]]].forEach(([name,coefs])=>actions.append(button(name,async()=>{for(const [i,key] of ['DspMatrixLlSlider','DspMatrixRlSlider','DspMatrixLrSlider','DspMatrixRrSlider'].entries())await set(key,coefs[i]);})));root.append(actions);
      }else if(module==='src'){
        const rate=controls.find(c=>c.name==='SrcRateCombo'),quality=controls.find(c=>c.name==='SrcQualityCombo'),dither=controls.find(c=>c.name==='SrcDitherCombo');
        root.append(cards([['当前播放链路',info.src||'未播放'],['目标采样率',rate?.items[v('SrcRateCombo')]||'原采样率'],['重采样质量',quality?.items[v('SrcQualityCombo')]||'—'],['抖动',dither?.items[v('SrcDitherCombo')]||'—']]));
        root.append(el('p','采样率设置在下次起播生效；当前播放链路显示的是实际状态。','eq-hint'));
      }else if(module==='fir'){
        root.append(cards([['IR 文件',info.irName||'尚未导入'],['声道',info.channels?info.channels+' 声道':'—'],['脉冲长度',info.taps?info.taps+' samples':'—'],['采样率',info.sampleRate?info.sampleRate+' Hz':'—']]));
        const actions=el('div',undefined,'echo-actions');actions.append(button('导入 IR…',()=>request('firImport')),button('清除 IR',()=>request('firClear')));root.append(actions);
        const route=el('div',undefined,'dsp-room-route');route.append(el('strong','处理路径'),cards([['卷积',info.enabled?'已启用':'关闭'],['输入余量',db(info.headroom)],['输出增益',db(v('RoomIrGainSlider'))],['预衰减',db(v('RoomTrimSlider'))]],'dsp-safety-route'));root.append(route);
      }else if(module==='rg'){
        const mode=controls.find(c=>c.name==='AudioFxRgModeCombo');root.append(cards([['响度模式',mode?.items[v('AudioFxRgModeCombo')]||'关闭'],['预增益',db(v('AudioFxRgPreampSlider'))],['峰值保护',v('AudioFxRgPreventClippingToggle')?'开启':'关闭']]));root.append(el('p','按文件中的 ReplayGain 标签调整响度；缺少标签的曲目沿用原生引擎策略。','eq-hint'));
      }
    }
    draw();return {update(name,value){values.set(name,value);draw();},telemetry(data){telemetry=data;if(module==='src')info.src=data.chain;draw();},dispose(){stopped=true;}};
  }};
})();
