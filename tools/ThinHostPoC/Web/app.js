(()=>{
 'use strict';let sequence=0;const pending=new Map();
 const log=text=>{const element=document.getElementById('log');element.textContent=(text+'\n'+element.textContent).slice(0,6000);};
 function request(action,extra={}){
  return new Promise((resolve,reject)=>{const requestId='poc-'+(++sequence);const timer=setTimeout(()=>{pending.delete(requestId);reject(Error('Host reply timed out'));},5000);pending.set(requestId,{resolve,reject,timer});window.chrome.webview.postMessage({kind:'celeste.poc.command',version:1,requestId,action,...extra});});
 }
 function render(state){if(!state||!state.trackId)return;document.getElementById('track').textContent=state.title;document.getElementById('state').textContent=(state.playing?'模拟播放中':'模拟暂停')+' · '+state.position+' / '+state.duration+' 秒';}
 window.chrome.webview.addEventListener('message',event=>{
  const message=event.data;if(message.kind==='celeste.poc.state'){render(message.state);return;}
  if(message.kind!=='celeste.poc.reply'||message.version!==1)return;const item=pending.get(message.requestId);if(!item)return;pending.delete(message.requestId);clearTimeout(item.timer);
  if(message.ok){render(message.data);item.resolve(message.data);}else item.reject(Error(message.data.error||'Host rejected'));
 });
 document.querySelectorAll('[data-action]').forEach(button=>button.addEventListener('click',()=>request(button.dataset.action).then(()=>log('OK '+button.dataset.action)).catch(error=>log(error.message))));
 request('snapshot').then(()=>document.getElementById('connection').textContent='桥接已连接 · 模拟会话').catch(error=>log(error.message));
 window.runHostTests=async()=>{
  try{
   const a=await request('play'),b=await request('play');if(!a.playing||!b.playing)throw Error('play idempotency');
   const c=await request('next'),d=await request('previous');if(c.trackId===d.trackId)throw Error('track navigation');
   const paused=await request('pause');if(paused.playing)throw Error('pause');
   let rejected=false;try{await request('not-allowed');}catch{rejected=true;}if(!rejected)throw Error('unknown action accepted');
   await request('selftestComplete',{passed:true});log('SELF TEST PASS');
  }catch(error){log(error.message);await request('selftestComplete',{passed:false});}
 };
})();
