(()=>{
 'use strict';const $=id=>document.getElementById(id);let coverUrl='';
 const send=action=>window.chrome?.webview?.postMessage({action});
 function render(message){
  const state=message.state||{},bg=message.background||'#252525';
  if(/^#[0-9a-f]{6}$/i.test(bg)){
   const rgb=[1,3,5].map(i=>parseInt(bg.slice(i,i+2),16));const light=rgb[0]*.2126+rgb[1]*.7152+rgb[2]*.0722>145;
   const root=document.documentElement;root.style.setProperty('--bg',bg);root.style.setProperty('--ink',light?'#38332e':'#eee8df');root.style.setProperty('--muted',light?'#746c60':'#b1a99d');root.style.setProperty('--accent',light?'#9b784b':'#d9b985');root.style.setProperty('--hover',light?'#0000000b':'#ffffff10');root.style.colorScheme=light?'light':'dark';
  }
  $('title').textContent=state.title||'未在播放';$('identity').title=($('title').textContent)+' · 双击恢复主界面';
  $('artist').textContent=[state.artist,state.album].filter(Boolean).join(' · ')||'Celeste';
  const next=typeof state.coverUrl==='string'&&state.coverUrl.startsWith('https://celeste-cover.internal/cover/')?state.coverUrl:'';
  if(next!==coverUrl){coverUrl=next;$('cover').hidden=!next;$('placeholder').hidden=!!next;if(next)$('cover').src=next;else $('cover').removeAttribute('src');}
  const playing=state.playing===true;$('play-path').setAttribute('d',playing?'M7 5v10M13 5v10':'M7 4l9 6-9 6z');$('play').title=playing?'暂停':'播放';$('play').setAttribute('aria-label',$('play').title);
  document.querySelectorAll('button').forEach(b=>b.disabled=state.busy===true);
  const fraction=Number(state.duration)>0?Math.min(1,Math.max(0,Number(state.position)/Number(state.duration))):0;$('progress').style.width=(Number.isFinite(fraction)?fraction*100:0)+'%';
 }
 $('cover').addEventListener('error',()=>{$('cover').hidden=true;$('placeholder').hidden=false;});
 document.querySelectorAll('button').forEach(b=>b.addEventListener('click',()=>send(b.dataset.action)));
 $('identity').addEventListener('dblclick',()=>send('showMain'));
 document.addEventListener('contextmenu',event=>{event.preventDefault();send('menu');});
 window.chrome?.webview?.addEventListener('message',event=>render(event.data||{}));
})();
