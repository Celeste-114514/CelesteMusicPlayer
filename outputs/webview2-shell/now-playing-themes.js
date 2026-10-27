/* Instrument theme. Real post-DSP spectrum; render frames are independent of host sampling. */
(() => {
  'use strict';
  const $ = id => document.getElementById(id), page = $('now-playing'), shell = window.CelesteShell;
  const selector = $('np-theme'), visual = $('np-visual'), progress = $('np-progress').closest('.np-progress');
  const live = typeof window.chrome?.webview?.postMessage === 'function', NS = 'http://www.w3.org/2000/svg';
  const svg = (tag, attrs) => { const e = document.createElementNS(NS, tag); Object.entries(attrs).forEach(([k,v])=>e.setAttribute(k,v)); return e; };
  const text = (id,value) => { const e=$(id); if(e&&e.textContent!==value)e.textContent=value; };
  const fmt = seconds => `${Math.floor((seconds||0)/60)}:${String(Math.floor((seconds||0)%60)).padStart(2,'0')}`;
  let theme='default', timer=0, frame=0, busy=false, generation=0, hostHidden=false, trackId=null, previousTime=0, receivedAt=0;
  let target=[], displayed=[], waveformRequested='', audioFacts=new Map(), hasSignal=false;
  try { const saved=localStorage.getItem('celeste.now-playing.theme');
    if(saved==='terminal')theme='terminal';
    else if(['instrument','twilight'].includes(saved))theme='instrument'; } catch {}
  const dial=svg('svg',{viewBox:'0 0 600 600','aria-hidden':'true'});
  [200,250,298].forEach((r,i)=>dial.append(svg('circle',{cx:300,cy:300,r,class:i===1?'np-dial-dashed':''})));
  for(let i=0;i<72;i++) {const a=i*Math.PI/36,r=i%6===0?280:290;dial.append(svg('line',{x1:300+Math.sin(a)*r,y1:300+Math.cos(a)*r,x2:300+Math.sin(a)*296,y2:300+Math.cos(a)*296}));}
  dial.append(svg('path',{d:'M0 300H600M300 0V600M88 88L512 512M88 512L512 88',class:'np-dial-axis'}));
  [[300,46,'0°'],[555,305,'90°'],[300,562,'180°'],[42,305,'270°']].forEach(([x,y,label])=>{const n=svg('text',{x,y,'text-anchor':'middle',class:'np-dial-label'});n.textContent=label;dial.append(n);});
  const orbit=svg('g',{'class':'np-orbit-marker'});orbit.append(svg('circle',{cx:300,cy:2,r:3,class:'np-dial-marker'}));dial.append(orbit);$('np-radar').append(dial);
  document.querySelectorAll('.np-frequency-labels span').forEach((node,i)=>node.style.left=`${Math.log10([20,200,1000,2000,5000,10000,20000][i]/20)/3*100}%`);
  const spectrum=$('np-spectrum'),scope=$('np-scope');
  for(let i=0;i<=5;i++)spectrum.append(svg('path',{d:`M0 ${i*51+2}H800`,class:'np-chart-grid'}));
  const signal=svg('path',{d:'M0 257H800',class:'np-spectrum-signal'}),overview=svg('path',{d:'M0 54H800',class:'np-scope-signal'});
  spectrum.append(signal);scope.append(overview);
  const num=(v,min=0,max=1)=>Number.isFinite(v)?Math.max(min,Math.min(max,v)):0;
  function drawOverview(peaks){
    if(!Array.isArray(peaks)||!peaks.length){overview.setAttribute('d','M0 54H800');return;}
    overview.setAttribute('d',peaks.map((v,i)=>`${i?'L':'M'}${(i/Math.max(1,peaks.length-1)*800).toFixed(1)} ${(54-num(v)*40).toFixed(1)}`).join(''));
  }
  function draw(now){
    frame=0;if(!active())return;
    if(previousTime&&now-previousTime<1000/60-.5){frame=requestAnimationFrame(draw);return;}
    const delta=Math.min(64,now-(previousTime||now-16));previousTime=now;
    const reduce=window.CelesteMotion&&!window.CelesteMotion.canMove();
    const count=target.length;
    if(displayed.length!==count)displayed=new Array(count).fill(0);
    let path='';
    for(let i=0;i<count;i++){
      const rate=target[i]>displayed[i]?58:150;
      displayed[i]=reduce?target[i]:displayed[i]+(target[i]-displayed[i])*(1-Math.exp(-delta/rate));
      const x=(i+.5)*800/count,y=257-displayed[i]*252;
      path+=`M${x.toFixed(1)} 257L${x.toFixed(1)} ${y.toFixed(1)}`;
    }
    signal.setAttribute('d',path||'M0 257H800');
    signal.style.strokeWidth=String(count?Math.max(1,800/count*.68):1);
    const state=shell.getState();
    if(!reduce)orbit.setAttribute('transform',`rotate(${((state.position||0)+Math.min(1,(now-receivedAt)/1000))*6%360} 300 300)`);
    frame=requestAnimationFrame(draw);
  }
  function active(){return theme==='instrument'&&window.CelesteNowPlaying.isOpen()&&!document.hidden&&!hostHidden&&shell.getState().playing;}
  // 立体声采样是引擎侧的一个抽头，跟着"当前真的有人在看"开关：
  // 详情页关掉/暂停/宿主隐藏都要还回去，不然引擎一直留着采集。
  let stereoSubscribed=false;
  function setStereoSampling(on){
    if(on===stereoSubscribed)return;
    stereoSubscribed=on;
    shell.send('stereoSampling',{ enabled:on });
  }
  function accept(data){
    hasSignal=!!data.available;
    target=hasSignal&&Array.isArray(data.bands)?data.bands.map(v=>num(v)):[];
    text('np-visual-status',hasSignal?'LIVE · 实时信号':'当前输出暂无采样信号');
    const samples=data.samplesAvailable&&Array.isArray(data.samples)?data.samples:[];
    let energy=0,peak=0;for(const s of samples){const v=num(s,-1,1);energy+=v*v;peak=Math.max(peak,Math.abs(v));}
    const rms=samples.length?Math.sqrt(energy/samples.length):0;
    text('np-visual-level',rms>0?`${(20*Math.log10(rms)).toFixed(1)} dBFS`:'—');
    text('np-crest',rms>0?`${(20*Math.log10(Math.max(rms,peak)/rms)).toFixed(1)} dB`:'—');
    if(!hasSignal){displayed=[];signal.setAttribute('d','M0 257H800');}
  }
  async function poll(){
    if(busy||!live||!active())return;
    busy=true;const ticket=generation,id=shell.getState().trackId;
    try{const data=await window.CelesteTransport.request('nowPlayingVisual',{});
      if(ticket===generation&&active()&&shell.getState().trackId===id&&data.trackId===id)accept(data);
    }catch{if(ticket===generation&&active())accept({});}finally{busy=false;}
  }
  function facts(state){
    const a=audioFacts.get(state.trackId)||{};
    text('np-fact-rate',a.sampleRate?`${(a.sampleRate/1000).toFixed(a.sampleRate%1000?1:0)} kHz`:'—');
    text('np-fact-bits',a.bitDepth?`${a.bitDepth}-bit`:'—');
    text('np-fact-channels',a.channels===2?'Stereo':a.channels===1?'Mono':a.channels?`${a.channels} ch`:'—');
    text('np-fact-bitrate',a.kbps?`${a.kbps} kbps`:'—');
    text('np-fact-size',a.fileSize?`${(a.fileSize/1048576).toFixed(1)} MB`:'—');
    text('np-fact-format',state.quality?.split(' · ')[0]||'—');
  }
  function sync(){
    const state=shell.getState();receivedAt=performance.now();
    if(trackId!==state.trackId){trackId=state.trackId;generation++;target=[];displayed=[];hasSignal=false;signal.setAttribute('d','M0 257H800');drawOverview([]);}
    $('np-artist').textContent=(theme==='instrument'?state.artist:[state.artist,state.album].filter(Boolean).join(' · '))||'—';
    $('np-title').dataset.album=state.album?`FROM · ${state.album}`:'CELESTE · AUDIO INSTRUMENT';
    text('np-visual-source',state.source||'—');text('np-visual-quality',state.quality||'—');text('np-visual-duration',fmt(state.duration));facts(state);
    $('np-overview').style.setProperty('--position',`${num((state.position||0)/Math.max(1,state.duration||1))*100}%`);
    const ruler=$('np-time-ruler');if(ruler.dataset.duration!==String(state.duration)){ruler.dataset.duration=state.duration;ruler.replaceChildren();for(let i=0;i<6;i++){const span=document.createElement('span');span.textContent=fmt((state.duration||0)*i/5);ruler.append(span);}}
    if(theme==='instrument'&&window.CelesteNowPlaying.isOpen()){
      const peaks=window.CelesteWaveform?.getPeaks(state.trackId);if(peaks?.length)drawOverview(peaks);
      else if(state.trackId&&waveformRequested!==state.trackId){waveformRequested=state.trackId;shell.send('waveform',{trackId:state.trackId});}
    }
    if(active()){
      setStereoSampling(true);
      if(!timer){poll();timer=setInterval(poll,50);}
      if(!frame){previousTime=0;frame=requestAnimationFrame(draw);}
    }else{
      setStereoSampling(false);
      clearInterval(timer);timer=0;cancelAnimationFrame(frame);frame=0;generation++;
      if(!state.playing){text('np-visual-status','已暂停');text('np-visual-level','—');text('np-crest','—');}
    }
  }
  function apply(value){
    // 'terminal' 由 terminal-player.js 接管渲染，这里只负责登记与让位（藏掉仪表视图）
    theme=value==='instrument'?'instrument':value==='terminal'?'terminal':'default';generation++;
    selector.value=theme;page.dataset.playerTheme=theme;visual.hidden=theme!=='instrument';
    const npLeft=page.querySelector('.np-left'),controls=page.querySelector('.np-controls');
    if(theme==='instrument')$('np-overview').append(progress);
    // 终端模式下控制条被搬进终端区了，此时它不在 .np-left 里，别硬插（会抛 NotFoundError）
    else if(npLeft&&controls&&controls.parentElement===npLeft)npLeft.insertBefore(progress,controls);
    else if(npLeft)npLeft.append(progress);
    try{localStorage.setItem('celeste.now-playing.theme',theme);}catch{}
    sync();if(window.CelesteNowPlaying.isOpen())window.CelesteMotion?.reveal(page.querySelector('.np-body'),'cover',8);
  }
  selector.addEventListener('change',()=>apply(selector.value));
  document.addEventListener('celeste:state',sync);
  document.addEventListener('celeste:message',event=>{
    const data=event.detail;
    if(data.kind==='celeste.visibility'){hostHidden=!!data.hidden;sync();}
    if(data.kind==='celeste.waveform'&&data.trackId===trackId)drawOverview(data.peaks);
    if(data.kind==='celeste.reply'&&data.data?.audio&&data.data.trackId){if(audioFacts.size>30)audioFacts.clear();audioFacts.set(data.data.trackId,data.data.audio);facts(shell.getState());}
  });
  $('np-progress').addEventListener('input',()=>{$('np-overview').style.setProperty('--position',`${num(Number($('np-progress').value)/Math.max(1,Number($('np-progress').max)))*100}%`);});
  document.addEventListener('celeste:now-playing',sync);document.addEventListener('visibilitychange',sync);apply(theme);
})();
