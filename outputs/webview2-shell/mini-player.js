(() => {
'use strict';
const $=id=>document.getElementById(id),web=window.chrome?.webview;
const send=(command,value)=>web?.postMessage({command,value,animate:!matchMedia("(prefers-reduced-motion:reduce)").matches});
const paths={minus:'M4 12h16',expand:'M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5M3 3l6 6m12-6-6 6',close:'M5 5l14 14M19 5L5 19',volume:'M11 4L5 9H2v6h3l6 5zm5 4q5 4 0 8m3-11q8 7 0 14',more:'M4 12h.1M12 12h.1M20 12h.1',previous:'M5 5v14M19 5l-12 7 12 7z',next:'M19 5v14M5 5l12 7-12 7z',play:'M7 4l13 8-13 8z',pause:'M8 5v14M16 5v14',lyrics:'M4 3h16v14H9l-5 4zM8 7h8M8 11h6',queue:'M8 5h13M8 12h13M8 19h13M3 5h.1M3 12h.1M3 19h.1'};
function icon(b,name){b.replaceChildren();const s=document.createElementNS('http://www.w3.org/2000/svg','svg'),p=document.createElementNS(s.namespaceURI,'path');s.setAttribute('viewBox','0 0 24 24');p.setAttribute('d',paths[name]);s.append(p);b.append(s);}
document.querySelectorAll('[data-icon]').forEach(b=>icon(b,b.dataset.icon));
document.querySelectorAll('[data-cmd]').forEach(b=>b.addEventListener('click',()=>send(b.dataset.cmd)));
let panel='',seeking=false,lastIndex=-2,queueKey='',coverGeneration=0,userScrollUntil=0;
function expand(name){panel=panel===name?'':name;$('lyrics').hidden=panel!=='lyrics';$('queue').hidden=panel!=='queue';if(panel)document.querySelector('main').hidden=false;for(const n of ['lyrics','queue'])$(n+'-button').setAttribute('aria-pressed',panel===n);send('expand',!!panel);lastIndex=-2;}
$('lyrics-button').onclick=()=>expand('lyrics');$('queue-button').onclick=()=>expand('queue');
$('progress').oninput=()=>{seeking=true;$('elapsed').textContent=time($('progress').value);updateProgress();};$('progress').onchange=()=>{send('seek',Number($('progress').value));seeking=false;};$('progress').onblur=()=>seeking=false;
$('volume').oninput=()=>{$('volume-value').value=$('volume').value+'%';send('volume',Number($('volume').value));};
for(const n of ['volume','more'])$(n+'-button').onclick=()=>{const open=$(n+'-panel').hidden;document.querySelectorAll('.popup').forEach(p=>p.hidden=true);$(n+'-panel').hidden=!open;};
document.addEventListener('pointerdown',e=>{if(!e.target.closest('.popup,#volume-button,#more-button'))document.querySelectorAll('.popup').forEach(p=>p.hidden=true);});
document.addEventListener('keydown',e=>{if(e.key==='Escape'){document.querySelectorAll('.popup').forEach(p=>p.hidden=true);if(panel)expand(panel);}});
function preference(key,value){try{if(value===undefined)return localStorage.getItem(key);localStorage.setItem(key,value);}catch{} }
let theme={mode:'light',light:{},dark:{}};
$('style').value=['apple','custom'].includes(preference('celeste.mini.style'))?preference('celeste.mini.style'):'celeste';
$('mode').value=['light','dark'].includes(preference('celeste.mini.mode'))?preference('celeste.mini.mode'):'follow';
for(const id of ['custom-bg','custom-accent']){const saved=preference('celeste.mini.'+id);if(/^#[0-9a-f]{6}$/i.test(saved||''))$(id).value=saved;}
function appearance(){
 const preset=$('style').value,mode=$('mode').value==='follow'?(preset==='apple'?'dark':theme.mode):$('mode').value;
 const v=theme[mode]||{},css=document.documentElement.style;
 document.documentElement.dataset.mode=mode;document.body.classList.toggle('apple',preset==='apple');
 const colors={'--mini-bg':v.shell|| (mode==='dark'?'#202024':'#fcf9f8'),'--mini-ink':v['player-ink']||v.ink||(mode==='dark'?'#f4eff4':'#302c38'),'--muted':v['player-muted']||v.muted||(mode==='dark'?'#bdb4be':'#97838e'),'--accent':v.accent||'#ee466c','--mini-track':v['accent-soft']||(mode==='dark'?'#674451':'#f5dce4'),'--mini-popup':v['menu-bg']||(mode==='dark'?'#302b32':'#fffdfb'),'--mini-on-accent':v['on-accent']||'#fff'};
 if(preset==='custom'){colors['--mini-bg']=$('custom-bg').value;colors['--accent']=$('custom-accent').value;colors['--mini-track']=`color-mix(in srgb, ${$('custom-accent').value} 22%, ${$('custom-bg').value})`;}
 for(const [key,value] of Object.entries(colors))css.setProperty(key,value);
 $('custom-colors').hidden=preset!=='custom';
 for(const [key,value] of [['style',preset],['mode',$('mode').value],['custom-bg',$('custom-bg').value],['custom-accent',$('custom-accent').value]])preference('celeste.mini.'+key,value);
}
for(const id of ['style','mode','custom-bg','custom-accent'])$(id).onchange=appearance;appearance();
const header=document.querySelector('header');
header.addEventListener('pointerdown',e=>{if(e.button!==0||e.target.closest('button,input,select'))return;e.preventDefault();header.setPointerCapture(e.pointerId);send('drag-start');});
for(const name of ['pointerup','pointercancel','lostpointercapture'])header.addEventListener(name,()=>send('drag-end'));
$('lyrics').addEventListener('wheel',()=>userScrollUntil=Date.now()+4000,{passive:true});
function time(v){v=Math.max(0,Math.floor(Number(v)||0));return Math.floor(v/60)+':'+String(v%60).padStart(2,'0');}
function row(text,action){const b=document.createElement('button');b.textContent=text;if(action)b.onclick=action;return b;}
function cover(url){const img=$('cover');img.hidden=!url;$('placeholder').hidden=!!url;if(url){img.src=url;img.onerror=()=>{img.hidden=true;$('placeholder').hidden=false;};}else img.removeAttribute('src');}
function updateProgress(){const p=$('progress');p.style.setProperty('--played',Math.min(100,Math.max(0,Number(p.value)/Number(p.max)*100))+'%');}
function receive(m){if(m.kind==='theme'){theme=m;appearance();}if(m.kind==='resized'&&!m.expanded&&!panel)document.querySelector('main').hidden=true;if(m.kind==='cover')cover(m.value);if(m.kind==='lyrics'){$('lyrics').replaceChildren();const lines=m.value||[],synced=lines.some(l=>l.time>0);if(!lines.length){const p=document.createElement('p');p.className='empty';p.textContent='暂无歌词';$('lyrics').append(p);}lines.forEach(l=>{const b=row(l.text,synced?()=>send('seek',l.time):null);if(!synced)b.disabled=true;$('lyrics').append(b);});lastIndex=-2;}
if(m.kind==='pin')$('pin').textContent=m.value?'取消始终置顶':'始终置顶';
if(m.kind!=='state')return;$('title').textContent=m.Title;$('artist').textContent=m.Artist||'Celeste Music Player';icon($('play'),m.playing?'pause':'play');document.body.classList.toggle('paused',!m.playing);$('pin').textContent=m.pinned?'取消始终置顶':'始终置顶';$('duration').textContent=time(m.duration);$('progress').disabled=!(m.duration>0);if(!seeking){$('progress').max=Math.max(1,m.duration);$('progress').value=m.position;$('elapsed').textContent=time(m.position);}updateProgress();if(document.activeElement!==$('volume'))$('volume').value=m.volume;$('volume-value').value=Math.round(m.volume)+'%';
const key=JSON.stringify([m.queue,m.queueIndex]);if(queueKey!==key){queueKey=key;$('queue').replaceChildren();(m.queue||[]).forEach((t,i)=>{const b=row(t,()=>send('queue',i));b.classList.toggle('current',i===m.queueIndex);$('queue').append(b);});if(!m.queue?.length){const p=document.createElement('p');p.className='empty';p.textContent='播放队列为空';$('queue').append(p);}}
if(lastIndex!==m.lyricIndex){lastIndex=m.lyricIndex;const rows=$('lyrics').querySelectorAll('button');rows.forEach((b,i)=>b.classList.toggle('current',i===m.lyricIndex));if(panel==='lyrics'&&Date.now()>userScrollUntil)rows[m.lyricIndex]?.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion:reduce)').matches?'instant':'smooth',block:'center'});}}
web?.addEventListener('message',e=>receive(e.data));document.addEventListener('visibilitychange',()=>document.body.classList.toggle('hidden',document.hidden));send('ready');
})();
