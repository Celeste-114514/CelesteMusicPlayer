(() => {
  'use strict';
  const shell=window.CelesteShell,canvas=document.getElementById('progress-waveform'),input=document.getElementById('progress'),container=document.getElementById('waveform-seek'),status=document.getElementById('waveform-status');
  const live=typeof window.chrome?.webview?.postMessage==='function',cache=new Map();
  let trackId='',peaks=[],scheduled=false,palette=null;
  // 画布不认 CSS 变量，得自己把当前主题色读出来；主题一变就作废重读
  function readPalette(){
    const styles=getComputedStyle(document.documentElement);
    const pick=(name,fallback)=>{const value=styles.getPropertyValue(name).trim();return value||fallback;};
    return {idle:pick('--wave-idle',pick('--muted','#dfd5dc')),played:pick('--wave-played',pick('--accent','#e66b96'))};
  }
  function draw(){
    scheduled=false;const width=container.clientWidth,height=container.clientHeight;if(!width||!height)return;
    const dpr=Math.min(devicePixelRatio||1,2);canvas.width=Math.round(width*dpr);canvas.height=Math.round(height*dpr);
    const context=canvas.getContext('2d');context.scale(dpr,dpr);context.clearRect(0,0,width,height);
    const ratio=Math.max(0,Math.min(1,Number(input.value)/Math.max(1,Number(input.max))));
    const count=96,pitch=5,barWidth=3;
    const paint=colour=>{context.fillStyle=colour;for(let i=0;i<count;i++){
      const from=Math.floor(i*peaks.length/count),to=Math.max(from+1,Math.floor((i+1)*peaks.length/count));
      let level=0;for(let j=from;j<to;j++)level=Math.max(level,Number(peaks[j])||0);
      const barHeight=peaks.length?Math.max(2,Math.min(1,level)*30):2;
      context.beginPath();context.roundRect(i*pitch+(pitch-barWidth)/2,(height-barHeight)/2,barWidth,barHeight,Math.min(1.2,barWidth/2));context.fill();
    }};
    paint((palette||(palette=readPalette())).idle);context.save();context.beginPath();context.rect(0,0,width*ratio,height);context.clip();paint((palette||(palette=readPalette())).played);context.restore();
  }
  function schedule(){if(!scheduled){scheduled=true;requestAnimationFrame(draw);}}
  function updateTrack(state){
    const id=state.trackId||'';if(id===trackId)return;trackId=id;peaks=cache.get(id)||[];schedule();
    if(!id){status.textContent='选择歌曲后显示波形';return;}
    if(cache.has(id)){status.textContent='歌曲波形';return;}
    status.textContent='正在加载歌曲波形';shell.send('waveform',{trackId:id});
  }
  document.addEventListener('celeste:message',event=>{
    const message=event.detail;
    if(message.kind==='celeste.state'){updateTrack(message.state||{});schedule();}
    if(message.kind==='celeste.waveform'&&message.trackId===trackId&&Array.isArray(message.peaks)){
      peaks=message.peaks.filter(value=>Number.isFinite(value)&&value>=0&&value<=1).slice(0,1024);
      if(cache.size>=30)cache.delete(cache.keys().next().value);cache.set(trackId,peaks);
      status.textContent=peaks.length?'歌曲波形':'这首歌曲暂无可用波形，可正常拖动定位';schedule();
    }
  });
  input.addEventListener('input',schedule);new ResizeObserver(schedule).observe(container);
  // 主题（含外观页的草稿预览）一变就重读色板并重画
  document.addEventListener('celeste:theme-applied',()=>{palette=null;schedule();});
  if(!live){peaks=Array.from({length:120},(_,i)=>.12+.65*Math.abs(Math.sin(i*.23)*Math.cos(i*.067))+.12*Math.abs(Math.sin(i*1.7)));status.textContent='演示波形';setInterval(schedule,250);}
  else updateTrack(shell.getState());
  window.CelesteWaveform = { getPeaks: id => (cache.get(id) || (id === trackId ? peaks : [])).slice() };
  schedule();
})();
