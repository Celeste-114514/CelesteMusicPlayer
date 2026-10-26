/* Transport panels are HTML. C# supplies data and runs the existing audio session. */
(() => {
  'use strict';
  const $=id=>document.getElementById(id), shell=window.CelesteShell;
  const live=typeof window.chrome?.webview?.postMessage==='function';
  const dialog=$('transport-dialog'), status=$('transport-panel-status');
  const modes={Shared:'WASAPI 共享',WasapiShared:'WASAPI 共享',WasapiExclusive:'WASAPI 独占',Asio:'ASIO'};
  const pending=new Map(); let serial=0,view='',epoch=0,tab='output',busy=false,polling=false,settings=null,queue=null,opener=null,queueStamp='',moreTrackId='';
  let customEditing=false;
  let demo={selectedMode:'Shared',selectedDeviceId:'',actualMode:'',actualDevice:'',devices:[{id:'',name:'演示设备'}],rate:1,preservePitch:true,a:-1,b:-1,abActive:false,abHint:'先播放一首歌，再设置循环点。',sleepSelection:0,sleepTracks:1,deviceMemory:false,canSaveProfile:false,hasDeviceProfile:false};
  const time=value=>value<0?'--:--':`${Math.floor(value/60)}:${String(Math.floor(value%60)).padStart(2,'0')}`;
  function rpc(command,payload){
    if(!live)return Promise.reject(new Error('请在桌面预览版中使用此功能。'));
    const requestId='transport-'+(++serial);
    return new Promise((resolve,reject)=>{
      const timer=setTimeout(()=>{pending.delete(requestId);reject(new Error('操作响应超时，请刷新确认当前状态。'));},['collectionOverview','browse','trackAction','folders','tags','dsp','home','appSettings','sources'].includes(command)?300000:12000);
      pending.set(requestId,{resolve,reject,timer}); shell.send(command,{...payload,requestId});
    });
  }
  document.addEventListener('celeste:message',event=>{
    const message=event.detail;
    if(message.kind==='celeste.reply'){
      const item=pending.get(message.requestId);if(!item)return;
      clearTimeout(item.timer);pending.delete(message.requestId);
      message.ok?item.resolve(message.data):item.reject(new Error(message.error||'操作失败'));
    }
    if(message.kind==='celeste.state'&&queue&&view==='queue'){
      const current=message.state?.trackId;
      document.querySelectorAll('.web-queue-row').forEach(row=>row.setAttribute('aria-current',String(row.dataset.trackId===current)));
    }
  });
  // BiliPai Continuity: (.20,.90,.22,1). Small damped spring for panel entry;
  // sampled in Web Animations because Compose springs do not run in WebView2.
  // 时长与弹簧参数都走统一控制器（motion.js）—— 用户改速度/幅度/回弹风格，这里跟着变。
  const motion=window.CelesteMotion;
  function enter(element){
    element.getAnimations().forEach(animation=>animation.cancel());
    if(!motion||!motion.canFade())return;
    const frames=motion.spring({from:0,to:1});
    const keyframes=frames.map((progress,index)=>({
      offset:frames.length>1?index/(frames.length-1):1,
      opacity:Math.min(1,index/8),
      transform:`translateY(${8*(1-progress)}px) scale(${.985+.015*progress})`
    }));
    motion.animate(element,keyframes,{duration:motion.duration(420,'popup'),easing:'linear'});
  }
  function show(next,button){
    epoch++;opener=button||document.activeElement;view=next;status.textContent='';queueStamp='';
    $('transport-more-menu').hidden=true;
    $('audio-tabs').hidden=next!=='audio';$('audio-pages').hidden=next!=='audio';$('web-queue-page').hidden=next!=='queue';$('web-playlist-picker').hidden=next!=='playlists';
    $('transport-dialog-title').textContent={audio:'音频设置',queue:'播放队列',playlists:'加入播放列表'}[next];
    $('transport-dialog-eyebrow').textContent={audio:'MAKE YOURSELF AT HOME',queue:'UP NEXT',playlists:'KEEP IT TOGETHER'}[next];
    dialog.dataset.view=next;
    if(!dialog.open)dialog.show();placePanel();enter(dialog);if(next==='audio')setTab(tab);
    document.querySelector('.transport-dialog-body').scrollTop=0;
  }
  function placePanel(){
    if(!opener?.getBoundingClientRect)return;
    const anchor=opener.getBoundingClientRect(),width=dialog.offsetWidth,height=dialog.offsetHeight;
    // Centre the panel on the button's horizontal axis so it looks symmetric above the button.
    // The audio-settings button sits far right, so symmetry is not always possible there: once the
    // clamped value kicks in the panel simply stops at the screen edge, which is expected.
    const centred=anchor.left+anchor.width/2-width/2;
    const left=Math.max(16,Math.min(innerWidth-width-16,centred));
    const top=Math.max(16,Math.min(innerHeight-height-16,anchor.top-height-12));
    dialog.style.left=left+'px';dialog.style.top=top+'px';
  }
  addEventListener('resize',()=>{if(dialog.open)placePanel();});
  async function close(restoreFocus=true){
    const closing=++epoch;view='';$('audio-settings').setAttribute('aria-expanded','false');$('queue').setAttribute('aria-expanded','false');
    dialog.getAnimations().forEach(animation=>animation.cancel());
    if(motion&&motion.canFade())try{await motion.popupExit(dialog);}catch{}
    if(epoch!==closing)return;dialog.close();if(restoreFocus&&opener?.isConnected)opener.focus();
  }
  $('transport-dialog-close').addEventListener('click',close);dialog.addEventListener('cancel',event=>{event.preventDefault();close();});
  dialog.addEventListener('click',event=>{const r=dialog.getBoundingClientRect();if(event.target===dialog&&(event.clientX<r.left||event.clientX>r.right||event.clientY<r.top||event.clientY>r.bottom))close();});
  function setTab(next){tab=next;document.querySelectorAll('[data-audio-tab]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.audioTab===tab)));document.querySelectorAll('[data-audio-page]').forEach(page=>page.hidden=page.dataset.audioPage!==tab);document.querySelector('.transport-dialog-body').scrollTop=0;}
  document.querySelectorAll('[data-audio-tab]').forEach(button=>button.addEventListener('click',()=>{setTab(button.dataset.audioTab);const page=document.querySelector(`[data-audio-page="${tab}"]`);if(page&&motion&&motion.canFade())motion.animate(page,[{opacity:0,transform:'translateY(5px)'},{opacity:1,transform:'translateY(0)'}],{duration:motion.duration(180,'nav'),easing:motion.easings.continuity});}));
  function setBusy(value){busy=value;dialog.setAttribute('aria-busy',String(value));document.querySelectorAll('#audio-pages button,#audio-pages input,#audio-pages select').forEach(control=>control.disabled=value);if(!value&&settings)renderSettings(settings);}
  function renderSettings(data,force=false){
    settings=data;
    document.querySelectorAll('[data-output-mode]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.outputMode===data.selectedMode)));
    const select=$('audio-device'),signature=JSON.stringify([data.selectedMode,data.selectedDeviceId,data.devices]);
    if(select.dataset.signature!==signature){select.replaceChildren();data.devices.forEach(device=>{const option=document.createElement('option');option.value=device.id;option.textContent=device.name;select.append(option);});if(!data.devices.length){const option=document.createElement('option');option.textContent='没有可用设备，请刷新';option.value='__unavailable';select.append(option);}select.dataset.signature=signature;}
    const known=data.devices.some(device=>device.id===data.selectedDeviceId);
    if(!known&&data.selectedDeviceId&&!select.querySelector('[data-unavailable]')){const option=document.createElement('option');option.value=data.selectedDeviceId;option.dataset.unavailable='true';option.textContent='已保存的设备暂不可用';option.disabled=true;select.append(option);}
    if(force||document.activeElement!==select)select.value=data.selectedDeviceId;
    select.disabled=busy||!data.devices.length;
    $('audio-actual').textContent=[modes[data.actualMode],data.actualDevice].filter(Boolean).join(' · ')||'尚未建立输出链路';
    $('asio-hint').hidden=data.selectedMode!=='Asio';$('device-memory').checked=data.deviceMemory;
    $('device-profile-hint').textContent=data.canSaveProfile?(data.hasDeviceProfile?'当前实际设备已有 DSP 配置档。':'当前实际设备尚未保存 DSP 配置。'):'使用具体设备起播后，可以保存它的 DSP 配置。';
    $('save-device-profile').disabled=busy||!data.canSaveProfile;
    $('ab-a').textContent=time(data.a);$('ab-b').textContent=time(data.b);$('ab-hint').textContent=data.abHint;
    $('ab-toggle').textContent=data.abActive?'关闭 AB 循环':'开启 AB 循环';$('ab-toggle').disabled=busy||data.a<0||data.b<=data.a;
    if(force||document.activeElement!==$('audio-rate')){$('audio-rate').value=data.rate;$('audio-rate-value').textContent=Number(data.rate).toFixed(2)+'×';}
    document.querySelectorAll('[name="audio-pitch"]').forEach(input=>input.checked=(input.value==='preserve')===data.preservePitch);
    $('audio-dsd-note').textContent=data.dsdDirect?'当前为 DSD / DoP 直出，不进行变速。设置将保留给后续 PCM 播放。':'DSD / DoP 直出不经过变速处理；设置会保留给后续 PCM 播放。';
    document.querySelectorAll('[data-sleep]').forEach(button=>button.setAttribute('aria-pressed',String(Number(button.dataset.sleep)===data.sleepSelection)));
    $('sleep-count-label').hidden=data.sleepSelection!==7;if(force||document.activeElement!==$('sleep-count'))$('sleep-count').value=data.sleepTracks;
    $('sleep-custom').hidden=!customEditing&&data.sleepSelection!==8;
    if(!customEditing&&data.sleepSelection===8&&document.activeElement!==$('sleep-minutes'))$('sleep-minutes').value=data.sleepMinutes;
    $('sleep-status').textContent=data.sleepSelection===7?`再播放 ${data.sleepTracks} 首后暂停。`:data.sleepSelection===6?'当前曲目结束后暂停。':data.sleepSelection===0?'未启用睡眠定时。':'定时已启用，到点自动暂停。';
    if(data.sleepSelection===8)$('sleep-status').textContent=`已设定 ${data.sleepMinutes} 分钟后暂停。修改后需重新点击开始计时。`;
  }
  async function audio(operation,payload={},quiet=false){
    if(busy)return;const generation=epoch;
    if(!quiet){status.textContent='正在读取或应用设置…';setBusy(true);}
    try{
      let result;
      if(live)result=await rpc('audioSettings',{operation,...payload});
      else{
        if(operation==='setMode')demo.selectedMode=payload.mode;
        if(operation==='setRate')demo.rate=payload.rate;
        if(operation==='setPitch')demo.preservePitch=payload.preservePitch;
        if(operation==='setSleep'){demo.sleepSelection=payload.selection;demo.sleepTracks=payload.tracks;demo.sleepMinutes=payload.minutes;}
        if(operation==='deviceMemory')demo.deviceMemory=payload.enabled;
        result=demo;
      }
      if(epoch!==generation)return;renderSettings(result,!quiet);
      if(!quiet)status.textContent=live?(operation==='saveDeviceProfile'?'当前设备的 DSP 配置已保存。':''):'界面预览 · 设置仅用于演示';
    }catch(error){if(epoch===generation){status.textContent=error.message;if(settings)renderSettings(settings,true);}}
    finally{if(!quiet)setBusy(false);}
  }
  document.querySelectorAll('[data-output-mode]').forEach(button=>button.addEventListener('click',()=>audio('setMode',{mode:button.dataset.outputMode})));
  $('audio-device').addEventListener('change',()=>audio('setDevice',{mode:settings.selectedMode,deviceId:$('audio-device').value}));
  $('audio-refresh').addEventListener('click',()=>audio('refresh'));
  $('device-memory').addEventListener('change',()=>audio('deviceMemory',{enabled:$('device-memory').checked}));
  $('save-device-profile').addEventListener('click',()=>audio('saveDeviceProfile'));
  document.querySelectorAll('[data-ab]').forEach(button=>button.addEventListener('click',()=>audio('ab',{action:button.dataset.ab})));
  $('audio-rate').addEventListener('input',()=>{$('audio-rate-value').textContent=Number($('audio-rate').value).toFixed(2)+'×';});
  $('audio-rate').addEventListener('change',()=>audio('setRate',{rate:Number($('audio-rate').value)}));
  $('audio-rate-reset').addEventListener('click',()=>audio('setRate',{rate:1}));
  document.querySelectorAll('[name="audio-pitch"]').forEach(input=>input.addEventListener('change',()=>audio('setPitch',{preservePitch:input.value==='preserve'})));
  document.querySelectorAll('[data-sleep]').forEach(button=>button.addEventListener('click',()=>{
    const selection=Number(button.dataset.sleep);customEditing=selection===8;
    if(customEditing){$('sleep-custom').hidden=false;$('sleep-minutes').focus();return;}
    audio('setSleep',{selection,tracks:Math.max(1,Math.min(99,Number($('sleep-count').value)||1))});
  }));
  $('sleep-custom').addEventListener('submit',event=>{event.preventDefault();if(!$('sleep-minutes').checkValidity())return;customEditing=false;audio('setSleep',{selection:8,minutes:Number($('sleep-minutes').value),tracks:1});});
  $('sleep-minutes').addEventListener('input',()=>{customEditing=true;});
  $('sleep-count').addEventListener('change',()=>audio('setSleep',{selection:7,tracks:Math.max(1,Math.min(99,Number($('sleep-count').value)||1))}));
  async function loadQueue(offset=0){
    const generation=epoch;
    try{
      const data=live?await rpc('queue',{operation:'get',offset}):{revision:0,total:0,currentIndex:-1,offset:0,tracks:[]};
      if(epoch!==generation)return;
      if(offset>0&&queue?.revision!==data.revision)return loadQueue(0);
      if(offset===0){const signature=String(data.revision);if(signature===queueStamp){queue.currentIndex=data.currentIndex;document.querySelectorAll('.web-queue-row').forEach(row=>row.setAttribute('aria-current',String(Number(row.dataset.queueIndex)===data.currentIndex)));return;}queueStamp=signature;queue=data;$('web-queue-list').replaceChildren();}
      else queue.tracks.push(...data.tracks);
      $('web-queue-count').textContent=data.total?`${data.total} 首 · 双击或按 Enter 播放`:'播放队列为空';
      data.tracks.forEach(track=>{const button=document.createElement('button');button.className='web-queue-row';button.dataset.trackId=track.id;button.dataset.queueIndex=track.index;button.dataset.queueRevision=data.revision;button.setAttribute('aria-current',String(track.index===data.currentIndex));
        const number=document.createElement('small');number.textContent=String(track.index+1).padStart(2,'0');const text=document.createElement('span');const title=document.createElement('strong');title.textContent=track.title;const artist=document.createElement('small');artist.textContent=track.artist;const duration=document.createElement('time');duration.textContent=time(track.duration);text.append(title,artist);button.append(number,text,duration);
        const play=async()=>{try{await rpc('queue',{operation:'play',revision:data.revision,index:track.index,trackId:track.id});await loadQueue();}catch(error){status.textContent=error.message;loadQueue();}};
        button.addEventListener('dblclick',play);button.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();play();}});$('web-queue-list').append(button);
      });
      $('web-queue-more').hidden=queue.tracks.length>=data.total;
    }catch(error){if(epoch===generation)status.textContent=error.message;}
  }
  $('web-queue-more').addEventListener('click',()=>loadQueue(queue?.tracks.length||0));
  document.addEventListener('celeste:tracksChanged',()=>{if(view==='queue')loadQueue();});
  async function moreAction(operation,payload={}){
    try{const result=await rpc('more',{operation,trackId:moreTrackId,...payload});shell.notify(result.message||'已完成');if(view==='playlists')close();}
    catch(error){if(dialog.open)status.textContent=error.message;else shell.notify(error.message);}
  }
  document.querySelectorAll('[data-more]').forEach(button=>button.addEventListener('click',async()=>{
    $('transport-more-menu').hidden=true;
    if(button.dataset.more==='tags'){
      if(!moreTrackId || shell.getState().trackId!==moreTrackId)return shell.notify('当前歌曲已变化，请重新打开菜单。');
      await window.CelesteTags.open([moreTrackId]);return;
    }
    if(button.dataset.more==='locateAlbum'){
      if(shell.getState().trackId!==moreTrackId)return shell.notify('当前歌曲已变化，请重新打开菜单。');
      if(!window.CelesteAlbums?.openForTrack(moreTrackId))shell.notify('当前歌曲不在本机专辑库中。');
      return;
    }
    if(button.dataset.more!=='addToPlaylist')return moreAction(button.dataset.more);
    show('playlists',$('transport-more'));const generation=epoch;$('web-playlist-list').replaceChildren();
    try{const data=await rpc('more',{operation:'get'});if(epoch!==generation)return;if(!data.playlists.length)status.textContent='还没有播放列表；创建播放列表的页面待接入。';
      data.playlists.forEach(name=>{const item=document.createElement('button');item.textContent=name;item.addEventListener('click',()=>moreAction('addToPlaylist',{name}));$('web-playlist-list').append(item);});
    }catch(error){if(epoch===generation)status.textContent=error.message;}
  }));
  function open(next,button){
    if(next==='more'){
      if(dialog.open)close(false);
      const menu=$('transport-more-menu');menu.hidden=!menu.hidden;if(menu.hidden)return;
      moreTrackId=shell.getState().trackId||'';
      // Same horizontal rule as placePanel(): centre the menu on the button's axis.
      // The "more" button sits hard against the right edge, so when half the menu does not fit
      // the clamped value pins it to the screen edge — same trade-off as the audio-settings panel.
      // Width is measured rather than hardcoded: it was 232px before, which silently broke whenever
      // the menu's own width changed.
      const r=button.getBoundingClientRect(),width=menu.offsetWidth||232;
      menu.style.left=Math.max(16,Math.min(innerWidth-width-16,r.left+r.width/2-width/2))+'px';menu.style.bottom=(innerHeight-r.top+12)+'px';enter(menu);return;
    }
    if(dialog.open&&view===next)return close();
    show(next,button);button?.setAttribute('aria-expanded','true');if(next==='audio')audio('get');else if(next==='queue')loadQueue();
  }
  document.addEventListener('pointerdown',event=>{if(!event.target.closest('#transport-more-menu,#transport-more'))$('transport-more-menu').hidden=true;if(dialog.open&&!event.target.closest('#transport-dialog,#audio-settings,#queue,#transport-more,#transport-more-menu'))close(false);});
  document.addEventListener('keydown',event=>{if(event.key==='Escape'){$('transport-more-menu').hidden=true;if(dialog.open){event.preventDefault();close();}}});
  window.addEventListener('resize',()=>{$('transport-more-menu').hidden=true;if(dialog.open)requestAnimationFrame(placePanel);});
  setInterval(async()=>{if(!dialog.open||!live||busy||polling||document.hidden)return;polling=true;try{if(view==='audio')await audio('get',{},true);else if(view==='queue')await loadQueue();}finally{polling=false;}},1500);
  window.CelesteTransport={open,request:rpc};
})();
