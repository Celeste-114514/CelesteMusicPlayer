/* Native Celeste EQ: MainWindow.EqDsp.cs geometry, palette, response and editing semantics. */
(() => {
  'use strict';
  // The native page draws against the control's real pixel size. Keeping the SVG in a fixed
  // 860x300 viewBox and letting CSS stretch it made the whole chart (text, nodes, strokes)
  // scale up with the window, which is not what the native EQ does. W/PW therefore follow
  // the host width at 1 user unit = 1 CSS px, while the height stays pinned at H.
  const NS = 'http://www.w3.org/2000/svg', H = 300, L = 52, R = 8, T = 14, B = 30, PH = H - T - B, MIN_W = 420;
  let W = 860, PW = W - L - R;
  const ticks = [32,64,125,250,500,1000,2000,4000,8000,16000], ladders = [12,18,24,36], steps = [3,6,6,12];
  const clamp = (v, a, b) => Math.min(b, Math.max(a, v));
  const el = (tag, text, cls) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n; };
  const svg = (tag, attrs = {}) => { const n = document.createElementNS(NS, tag); Object.entries(attrs).forEach(([k,v]) => n.setAttribute(k,v)); return n; };
  const fmt = n => (n > 0 ? '+' : '') + Number(n).toFixed(1) + ' dB';
  const frequencies = f => f >= 1000 ? (f / 1000).toFixed(f % 1000 ? 1 : 0) + 'k' : Math.round(f) + ' Hz';
  const types = [['Peaking','峰值'],['LowShelf','低架'],['HighShelf','高架'],['LowPass','低通'],['HighPass','高通'],['Notch','切除']];
  window.CelesteDspEq = { mount(host, { request, notify, bandGainAt, live }) {
    let state = { enabled:false, bands:[], preampDb:0, presetId:'flat' }, selected = 0, stopped = false, loading = false, revision = 0, editing = false, timer = 0;
    let drag = false, hover = -1, mode = 'pro', spectrumOn = true, spectrum = [], pollBusy = false, previewAt = 0, previewQueue = Promise.resolve();
    const root = el('div',undefined,'eq-workbench'), heading = el('div',undefined,'dsp-module-head'), title = el('div');
    title.append(el('h2','参数 EQ'),el('p','拖动频段节点调整频率与增益，Shift 可精细调整。'));
    const power = button('启用 EQ', async () => { revision++; state.enabled = !state.enabled; paintState(); await save(); }); heading.append(title,power); root.append(heading);
    const toolbar = el('div',undefined,'eq-toolbar'), preset = el('select',undefined,'dsp-select'); preset.setAttribute('aria-label','EQ 预设');
    preset.addEventListener('change',()=>action('preset',{presetId:preset.value}));
    const pro = button('高级',()=>{mode='pro';renderMode();}), simple = button('简单',()=>{mode='simple';renderMode();}); toolbar.append(pro,simple,preset); root.append(toolbar);
    const tools = el('div',undefined,'eq-toolbar eq-tools');
    const undo = button('撤销',()=>action('undo')), redo = button('重做',()=>action('redo')), ab = button('A/B 对比',()=>action('ab'));
    const spectrumBtn = button('实时频谱',()=>{spectrumOn=!spectrumOn;spectrumBtn.setAttribute('aria-pressed',spectrumOn);draw();}); spectrumBtn.setAttribute('aria-pressed','true');
    const add = button('添加频段',()=>{if(state.bands.length>=31)return;state.bands.push({freq:1000,gain:0,q:1,type:'Peaking',enabled:true});selected=state.bands.length-1;changed();renderBands();save();});
    tools.append(undo,redo,ab,spectrumBtn,button('自动增益',()=>action('autoGain')),add,button('重置',()=>action('reset')),button('导入 APO…',()=>action('importApo')),button('导出 APO…',()=>action('exportApo'))); root.append(tools);
    const saveForm = el('form',undefined,'dsp-profile-save'), name = el('input',undefined,'dsp-input'); name.placeholder='保存为我的 EQ 预设';name.maxLength=40;name.setAttribute('aria-label','EQ 预设名称'); const savePreset=el('button','保存预设','eq-pill'); saveForm.append(name,savePreset);saveForm.addEventListener('submit',async e=>{e.preventDefault();if(!name.value.trim())return;await action('savePreset',{name:name.value.trim()});name.value='';});root.append(saveForm);
    const summary = el('div',undefined,'eq-summary'); root.append(summary);
    const wrap = el('div',undefined,'eq-curve-wrap'), chart = svg('svg',{class:'eq-curve',viewBox:`0 0 ${W} ${H}`,tabindex:0,role:'application','aria-label':'EQ 频响曲线，可拖动频段节点'}), readout = el('output',undefined,'eq-readout');wrap.append(chart,readout);root.append(wrap);
    const preampRow=el('label',undefined,'eq-preamp eq-preamp-row'), preamp=el('input'), preampValue=el('output');preamp.type='range';preamp.min=-24;preamp.max=24;preamp.step=.1;preamp.setAttribute('aria-label','EQ 预增益');preampRow.append(el('span','预增益'),preamp,preampValue);root.append(preampRow);
    preamp.addEventListener('input',()=>{state.preampDb=Number(preamp.value);preampValue.value=fmt(state.preampDb);changed();});preamp.addEventListener('change',save);
    const editor=el('div',undefined,'eq-band-editor'), bands=el('div',undefined,'eq-bands'), simpleBox=el('div',undefined,'eq-simple');root.append(editor,bands,simpleBox);
    const status=el('p','正在读取 EQ…','eq-hint');status.setAttribute('role','status');root.append(status);host.append(root);
    function button(text, fn) {const b=el('button',text,'eq-pill');b.type='button';b.addEventListener('click',fn);return b;}
    function payload() {return {enabled:state.enabled,preampDb:state.preampDb,bands:state.bands.map(b=>({...b}))};}
    function changed() {revision++;editing=true;state.presetId='custom';draw();if(Date.now()-previewAt>85){previewAt=Date.now();const next=payload();previewQueue=previewQueue.catch(()=>{}).then(()=>request('preview',next)).catch(e=>{status.textContent=e.message;});}}
    async function save() {if(!live||stopped)return;const stamp=revision,data=payload();editing=false;try{await previewQueue;await request('set',data);status.textContent='已应用并保存';if(stamp===revision&&!editing)await load();}catch(e){status.textContent=e.message;notify(e.message);}}
    async function action(operation, payload={}) {if(!live)return notify('请在桌面播放器中使用此功能。');if(loading)return;loading=true;root.setAttribute('aria-busy','true');try{await previewQueue;const r=await request(operation,payload);if(r?.message)notify(r.message);await load();}catch(e){notify(e.message);status.textContent=e.message;}finally{loading=false;root.removeAttribute('aria-busy');}}
    async function load() {const stamp=revision;try {let data=live?await request('get'):{enabled:false,bands:ticks.map(freq=>({freq,gain:0,q:1,type:'Peaking',enabled:true})),preampDb:0,presetId:'flat',userPresets:[]};if(stopped||stamp!==revision||editing)return;state=data;selected=clamp(selected,0,state.bands.length-1);paintState();renderBands();draw();status.textContent=live?'已读取当前 EQ 设置':'界面预览 · 启动桌面播放器后可调节声音';}catch(e){status.textContent=e.message;}}
    function paintState() {
      power.textContent=state.enabled?'EQ 已启用':'EQ 已关闭';power.setAttribute('aria-pressed',state.enabled);undo.disabled=!state.undo;redo.disabled=!state.redo;ab.textContent=state.ab?(state.abShowingA?'回到 B（调整后）':'试听 A（参考）'):'A/B 对比';
      preset.replaceChildren();const presets=[['flat','平坦'],['pop','流行'],['rock','摇滚'],['jazz','爵士'],['classical','古典'],['bass','低音增强'],['soft','柔和'],['custom','自定义'],...(state.userPresets||[]).map(p=>[p.id,p.name])];presets.forEach(([id,text])=>{const o=el('option',text);o.value=id;preset.append(o);});preset.value=state.presetId;
      preamp.value=state.preampDb;preampValue.value=fmt(state.preampDb);summary.textContent=`${state.enabled?'已启用':'已关闭'} · ${state.bands.length} 段 · 预增益 ${fmt(state.preampDb)}`;
      add.disabled=state.bands.length>=31;renderMode();
    }
    function renderMode(){pro.setAttribute('aria-pressed',mode==='pro');simple.setAttribute('aria-pressed',mode==='simple');editor.hidden=bands.hidden=mode!=='pro';simpleBox.hidden=mode!=='simple';if(mode==='simple')renderSimple();}
    function renderSimple(){simpleBox.replaceChildren();const values=state.simple||{};[['bass','低频'],['vocal','人声'],['air','通透'],['warm','暖色']].forEach(([key,text])=>{const row=el('label'), input=el('input'), output=el('output');input.type='range';input.min=0;input.max=1;input.step=.05;input.value=values[key]??values[key[0].toUpperCase()+key.slice(1)]??0;input.setAttribute('aria-label',text);output.value=Math.round(Number(input.value)*100)+'%';row.append(el('span',text),input,output);simpleBox.append(row);input.addEventListener('input',()=>output.value=Math.round(Number(input.value)*100)+'%');input.addEventListener('change',()=>{const data={};simpleBox.querySelectorAll('input').forEach((n,i)=>data[['bass','vocal','air','warm'][i]]=Number(n.value));action('simple',data);});});}
    function renderBands(){
      editor.replaceChildren();bands.replaceChildren();const band=state.bands[selected];if(!band)return;
      const title=el('strong',`频段 ${selected+1}`),type=el('select',undefined,'dsp-select');type.setAttribute('aria-label','滤波器类型');types.forEach(([id,label])=>{const o=el('option',label);o.value=id;type.append(o);});type.value=band.type||'Peaking';type.addEventListener('change',()=>{band.type=type.value;changed();save();});editor.append(title,type);
      [['freq','频率',20,20000,.1],['gain','增益',-24,24,.1],['q','Q 值',.1,24,.1]].forEach(([key,text,min,max,step])=>{const label=el('label'),input=el('input');input.type='number';Object.assign(input,{min,max,step,value:band[key]});input.setAttribute('aria-label','选中频段'+text);input.addEventListener('change',()=>{band[key]=clamp(Number(input.value)||min,min,max);input.value=band[key];changed();renderBands();save();});label.append(el('span',text),input);editor.append(label);});
      editor.append(button(band.enabled?'停用此段':'启用此段',()=>{band.enabled=!band.enabled;changed();renderBands();save();}),button('删除此段',()=>{if(state.bands.length<=1)return;state.bands.splice(selected,1);selected=Math.min(selected,state.bands.length-1);changed();renderBands();save();}));
      state.bands.forEach((b,i)=>{const cell=el('div',undefined,'eq-band');cell.dataset.selected=i===selected;const choose=button(frequencies(b.freq),()=>{selected=i;renderBands();draw();}),input=el('input'),out=el('output',fmt(b.gain));input.type='range';Object.assign(input,{min:-24,max:24,step:.1,value:b.gain});input.setAttribute('aria-label',`${frequencies(b.freq)} 增益`);input.addEventListener('input',()=>{selected=i;b.gain=Number(input.value);out.value=fmt(b.gain);changed();});input.addEventListener('change',()=>{renderBands();save();});cell.append(choose,input,out);bands.append(cell);});
    }
    const x=f=>L+Math.log2(clamp(f,20,20000)/20)/Math.log2(1000)*PW;
    // Re-fit the viewBox to the real host width. A collapsed/hidden page reports 0 and is
    // skipped, so switching modules never resets the chart to the minimum width.
    function measure(){if(stopped)return false;const rect=chart.getBoundingClientRect();if(!rect.width)return false;const next=clamp(Math.round(rect.width),MIN_W,4096);if(Math.abs(next-W)<1)return false;W=next;PW=W-L-R;chart.setAttribute('viewBox',`0 0 ${W} ${H}`);return true;}
    function maxGain(){const peak=Math.max(0,...state.bands.filter(b=>b.enabled).map(b=>Math.abs(b.gain)));return ladders.find(n=>n>=peak+2)||36;}
    const y=(g,m)=>clamp(T+PH/2-g*PH/(2*m),T,T+PH);
    function response(f){return state.bands.reduce((sum,b)=>sum+(b.enabled?bandGainAt(f,b):0),0);}
    function draw(){
      if(stopped)return;measure();const max=maxGain(),step=steps[ladders.indexOf(max)];chart.replaceChildren();const defs=svg('defs'),gradient=svg('linearGradient',{id:'native-eq-stroke',x1:0,y1:0,x2:1,y2:0}),fill=svg('linearGradient',{id:'native-eq-fill',x1:0,y1:0,x2:0,y2:1});
      [0,.55,1].forEach((offset,i)=>gradient.append(svg('stop',{offset,'stop-color':`var(--eq-stop-${i+1})`})));fill.append(svg('stop',{offset:0,'stop-color':'var(--eq-fill-top)'}),svg('stop',{offset:1,'stop-color':'var(--eq-fill-bottom)'}));defs.append(gradient,fill);chart.append(defs);
      if(spectrumOn){const count=spectrum.length;for(let i=0;i<count;i++){const h=clamp(spectrum[i],0,1)*PH*.9;chart.append(svg('rect',{x:L+i*PW/count+1,y:T+PH-h,width:Math.max(1,PW/count-2),height:h,rx:1,fill:'var(--eq-spectrum,rgba(15,118,110,.102))'}));}}
      for(let db=-max;db<=max;db+=step){chart.append(svg('line',{class:db===0?'grid-zero':'grid-line',x1:L,y1:y(db,max),x2:W-R,y2:y(db,max)}));const t=svg('text',{class:'tick-label',x:L-8,y:y(db,max)+4,'text-anchor':'end'});t.textContent=(db>0?'+':'')+db;chart.append(t);}
      ticks.forEach(f=>{chart.append(svg('line',{class:'grid-line',x1:x(f),y1:T,x2:x(f),y2:H-B}));const t=svg('text',{class:'tick-label',x:x(f),y:H-B+18,'text-anchor':'middle'});t.textContent=f>=1000?f/1000+'k':f;chart.append(t);});
      const points=Array.from({length:241},(_,i)=>{const f=20*1000**(i/240);return `${x(f).toFixed(1)},${y(response(f),max).toFixed(1)}`;});const path='M'+points.join(' L');chart.append(svg('path',{d:path+` L${W-R},${H-B} L${L},${H-B} Z`,fill:'url(#native-eq-fill)'}),svg('path',{d:path,fill:'none',stroke:'url(#native-eq-stroke)','stroke-width':2,opacity:state.enabled?1:.35}));
      // Native page: dashed guides for selected/hovered nodes and frequency labels with
      // 42px collision suppression. Node radius stays identical for every band.
      new Set([selected,hover]).forEach(i=>{const b=state.bands[i];if(!b)return;const attrs={class:'eq-guide',stroke:'var(--eq-grid-strong)','stroke-width':1,'stroke-dasharray':'4 3','pointer-events':'none'};chart.append(svg('line',{...attrs,x1:x(b.freq),x2:x(b.freq),y1:T,y2:H-B}),svg('line',{...attrs,x1:L,x2:W-R,y1:y(b.gain,max),y2:y(b.gain,max)}));});
      state.bands.forEach((b,i)=>{const cx=x(b.freq),cy=y(b.gain,max);chart.append(svg('circle',{cx,cy,r:7,class:'node-ring','data-band':i,'data-selected':i===selected,opacity:b.enabled?1:.45}));const glyph=svg('text',{x:cx,y:cy,'text-anchor':'middle','dominant-baseline':'central',class:'eq-node-glyph','pointer-events':'none'});glyph.textContent=b.type==='Peaking'?'P':b.type==='Notch'?'N':/Shelf/.test(b.type)?'S':'F';chart.append(glyph);});
      const placed=[];
      state.bands.map((b,i)=>({b,i,priority:(i===selected||i===hover?100000:0)+Math.abs(b.gain)*10+x(b.freq)*.001})).sort((a,b)=>b.priority-a.priority).forEach(({b})=>{const cx=x(b.freq),cy=clamp(y(b.gain,max)-14,T+11,H-B-4);if(placed.some(p=>Math.abs(p.x-cx)<42&&Math.abs(p.y-cy)<16))return;placed.push({x:cx,y:cy});const label=svg('text',{class:'tick-label eq-node-frequency',x:cx,y:cy,'text-anchor':'middle','pointer-events':'none'});label.textContent=frequencies(b.freq);chart.append(label);});
    }
    function point(e){const p=chart.createSVGPoint();p.x=e.clientX;p.y=e.clientY;const m=chart.getScreenCTM();return m?p.matrixTransform(m.inverse()):{x:0,y:0};}
    chart.addEventListener('pointerdown',e=>{const p=point(e),m=maxGain();let distance=400,best=-1;state.bands.forEach((b,i)=>{const d=(x(b.freq)-p.x)**2+(y(b.gain,m)-p.y)**2;if(d<distance){distance=d;best=i;}});if(best<0)return;selected=best;drag=true;chart.setPointerCapture(e.pointerId);chart.focus();e.preventDefault();renderBands();draw();});
    chart.addEventListener('pointermove',e=>{const p=point(e),m=maxGain(),f=clamp(20*1000**((p.x-L)/PW),20,20000),gain=clamp((T+PH/2-p.y)*2*m/PH,-24,24);if(drag){const b=state.bands[selected],step=e.shiftKey?.1:.5;b.freq=Math.round(f*10)/10;b.gain=Math.round(gain/step)*step;changed();}else{let nearest=-1,distance=400;state.bands.forEach((b,i)=>{const d=(x(b.freq)-p.x)**2+(y(b.gain,m)-p.y)**2;if(d<distance){distance=d;nearest=i;}});if(nearest!==hover){hover=nearest;draw();}}readout.value=`${frequencies(f)} · 总响应 ${fmt(response(f))}`;});
    chart.addEventListener('pointerleave',()=>{hover=-1;readout.value='';draw();});
    function endDrag(){if(!drag)return;drag=false;renderBands();save();}chart.addEventListener('pointerup',endDrag);chart.addEventListener('pointercancel',endDrag);chart.addEventListener('lostpointercapture',endDrag);
    chart.addEventListener('keydown',e=>{const b=state.bands[selected];if(!b)return;if(e.key==='Delete'){if(state.bands.length>1)state.bands.splice(selected,1);selected=clamp(selected,0,state.bands.length-1);}else if(e.key==='ArrowUp'||e.key==='ArrowDown')b.gain=clamp(b.gain+(e.key==='ArrowUp'?1:-1)*(e.shiftKey?.1:.5),-24,24);else if(e.key==='ArrowLeft'||e.key==='ArrowRight')b.freq=clamp(b.freq*2**((e.key==='ArrowRight'?1:-1)/3),20,20000);else return;e.preventDefault();changed();renderBands();save();});
    timer=setInterval(async()=>{if(stopped||document.hidden||!live||!spectrumOn||pollBusy)return;pollBusy=true;try{const data=await request('spectrum');if(stopped)return;spectrum=data.available?data.bands:spectrum.map(v=>v*.8);draw();}catch{}finally{pollBusy=false;}},100);
    const disposeHeadphones=window.CelesteHeadphones.mount(root,{request,onApplied:load,bandGainAt,live});
    let resizeObserver=null;
    if(typeof ResizeObserver==='function'){resizeObserver=new ResizeObserver(()=>{if(!stopped&&measure())draw();});resizeObserver.observe(wrap);}
    load();return ()=>{if(editing)save();stopped=true;clearInterval(timer);resizeObserver?.disconnect();disposeHeadphones();};
  }};
})();
