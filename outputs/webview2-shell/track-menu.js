(() => {
  'use strict';
  const shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
  const selector='.song-row,.album-track[data-track-id],.web-queue-row';
  const menu=document.createElement('div');menu.id='track-context-menu';menu.className='track-context-menu';menu.role='menu';menu.hidden=true;document.body.append(menu);
  const bar=document.createElement('div');bar.className='track-selection-bar';bar.hidden=true;document.body.append(bar);
  let selected=new Set(),scope=null,anchor=null,multi=false,epoch=0,returnFocus=null,busy=false;
  const key=row=>row.dataset.queueIndex===undefined?row.dataset.trackId||'demo-'+row.dataset.index:'q:'+row.dataset.queueRevision+':'+row.dataset.queueIndex;
  const container=row=>row.parentElement.closest('#songs-list,#album-tracks,#artist-songs,#browse-tracks,#web-queue-list,#folder-songs')||row.parentElement;
  const rows=()=>scope?[...scope.querySelectorAll(selector)]:[];
  const chosen=()=>rows().filter(row=>selected.has(key(row)));
  function paint(){rows().forEach(row=>{row.classList.toggle('context-selected',selected.has(key(row)));});bar.hidden=!multi;bar.replaceChildren();if(!multi)return;const label=document.createElement('span');label.textContent=`已选择 ${chosen().length} 首`;bar.append(label);const operate=document.createElement('button');operate.textContent='操作所选歌曲';operate.disabled=!chosen().length||busy;operate.onclick=()=>{const first=chosen()[0],box=operate.getBoundingClientRect();if(first)openTracks(first,box.left,box.top-12);};const done=document.createElement('button');done.textContent='完成';done.onclick=reset;bar.append(operate,done);}
  function reset(){selected.clear();paint();multi=false;scope=null;anchor=null;bar.hidden=true;close();}
  function select(row,event={}){const parent=container(row);if(scope!==parent){reset();scope=parent;}const list=rows(),id=key(row);if(event.shiftKey&&anchor&&list.includes(anchor)){const a=list.indexOf(anchor),b=list.indexOf(row);if(!event.ctrlKey&&!event.metaKey)selected.clear();list.slice(Math.min(a,b),Math.max(a,b)+1).forEach(item=>selected.add(key(item)));multi=true;}else if(event.ctrlKey||event.metaKey||multi){selected.has(id)?selected.delete(id):selected.add(id);multi=true;anchor=row;}else{selected.clear();selected.add(id);anchor=row;}paint();}
  function close(focus=false){epoch++;menu.hidden=true;menu.replaceChildren();if(focus&&returnFocus?.isConnected)returnFocus.focus({preventScroll:true});}
  function place(x,y){menu.hidden=false;const box=menu.getBoundingClientRect();menu.style.left=Math.max(8,Math.min(x,innerWidth-box.width-8))+'px';menu.style.top=Math.max(8,Math.min(y,innerHeight-box.height-8))+'px';}
  function button(label,action,disabled=false){const item=document.createElement('button');item.type='button';item.role='menuitem';item.textContent=label;item.disabled=disabled;item.onclick=async()=>{if(busy)return;close();try{busy=true;await action();}catch(error){shell.notify(error.message);}finally{busy=false;paint();}};menu.append(item);return item;}
  function divider(){const line=document.createElement('hr');line.role='separator';menu.append(line);}
  function payload(items){const queue=items[0].dataset.queueIndex!==undefined;if(queue)return{queue:true,revision:Number(items[0].dataset.queueRevision),entries:items.map(row=>({index:Number(row.dataset.queueIndex),trackId:row.dataset.trackId}))};return{queue:false,trackIds:items.map(row=>row.dataset.trackId)};}
  async function openTracks(row,x,y){
    if(busy)return;
    if(scope!==container(row)||!selected.has(key(row))){reset();scope=container(row);selected.add(key(row));anchor=row;paint();}
    returnFocus=row;close();const generation=epoch,items=chosen(),snapshot=payload(items),queue=snapshot.queue;
    const invoke=async(operation,extra={})=>{
      if(!live){if(operation==='play')shell.playDemo(Number(items[0].dataset.trackId??items[0].dataset.index));else shell.notify('请在桌面播放器中使用此功能。');return;}
      await window.CelesteTransport.request('trackAction',{operation,...snapshot,...extra});
      document.dispatchEvent(new CustomEvent('celeste:tracksChanged'));if(['removeQueue','pin','delete'].includes(operation))reset();
    };
    let caps={favorite:false,canDelete:false};
    try{if(live)caps=await window.CelesteTransport.request('trackAction',{operation:'get',...snapshot});}catch(error){if(generation===epoch)shell.notify(error.message);return;}
    if(generation!==epoch)return;
    button('播放',()=>invoke('play'),items.length!==1);
    button('下一首播放',()=>invoke('next'));
    if(queue){button('置顶',()=>invoke('pin'));button('从播放队列中删除',()=>invoke('removeQueue'));}
    else{button('添加至播放队列',()=>invoke('addQueue'));button('添加到播放列表',()=>invoke('addPlaylist'));}
    button('多选',()=>{multi=true;paint();row.focus({preventScroll:true});});divider();
    button(caps.favorite?'取消喜欢':'添加到我喜欢的音乐',()=>invoke('favorite',{value:!caps.favorite}));
    const ratings=document.createElement('div');ratings.className='context-rating';ratings.role='group';ratings.setAttribute('aria-label','评分');menu.append(ratings);
    for(let value=0;value<=5;value++){const item=button(value===0?'清除评分':value+' 星',()=>invoke('rating',{value}));ratings.append(item);}
    divider();button('标签编辑',()=>window.CelesteTags.open(items.map(row=>row.dataset.trackId)));button('复制歌名与艺术家',()=>invoke('copy'));
    button('在文件夹中显示',()=>invoke('reveal'),items.length!==1);button('定位到专辑',()=>window.CelesteAlbums.openForTrack(snapshot.trackIds?.[0]||snapshot.entries?.[0].trackId),items.length!==1);divider();
    button(caps.canDelete?'从磁盘删除（回收站）':'从磁盘删除（不可用）',()=>invoke('delete'),!caps.canDelete);
    place(x,y);menu.querySelector('button:not(:disabled)')?.focus({preventScroll:true});
  }
  function rowFrom(event){return event.target.closest?.(selector);}
  document.addEventListener('contextmenu',event=>{const row=rowFrom(event);if(!row)return;event.preventDefault();event.stopPropagation();openTracks(row,event.clientX,event.clientY);});
  document.addEventListener('click',event=>{const row=rowFrom(event);if(!row||event.target.closest('select,input,.row-tools'))return;const selecting=multi||event.ctrlKey||event.metaKey||event.shiftKey;select(row,event);if(selecting){event.preventDefault();event.stopImmediatePropagation();}},true);
  document.addEventListener('dblclick',event=>{if(multi&&rowFrom(event)){event.preventDefault();event.stopImmediatePropagation();}},true);
  document.addEventListener('keydown',event=>{
    if(!menu.hidden){if(event.key==='Escape'){event.preventDefault();event.stopImmediatePropagation();close(true);return;}const items=[...menu.querySelectorAll('button:not(:disabled)')];if(['ArrowDown','ArrowUp','Home','End'].includes(event.key)){event.preventDefault();const i=items.indexOf(document.activeElement);items[event.key==='Home'?0:event.key==='End'?items.length-1:(i+(event.key==='ArrowUp'?-1:1)+items.length)%items.length]?.focus();}if(event.key==='Tab')close();return;}
    const row=rowFrom(event);if(event.key==='Escape'&&multi){event.preventDefault();reset();return;}if(!row||event.target.closest('select,input,.row-tools'))return;
    if(event.key==='ContextMenu'||event.shiftKey&&event.key==='F10'){event.preventDefault();event.stopImmediatePropagation();const box=row.getBoundingClientRect();openTracks(row,box.left+30,box.top+20);}
    else if((event.ctrlKey||event.metaKey)&&event.key.toLowerCase()==='a'){event.preventDefault();scope=container(row);multi=true;rows().forEach(item=>selected.add(key(item)));paint();}
    else if(event.key===' '&&multi){event.preventDefault();event.stopImmediatePropagation();select(row,{ctrlKey:true});}
  },true);
  document.addEventListener('pointerdown',event=>{if(!menu.contains(event.target))close();},true);
  document.addEventListener('celeste:navigate',reset);document.addEventListener('celeste:library',reset);
  document.addEventListener('scroll',event=>{if(!menu.contains(event.target))close();},true);window.addEventListener('resize',()=>close());
  window.CelesteTrackMenu=Object.freeze({open:openTracks,folder:(event,item,action)=>{close();returnFocus=event.target.closest('[role=treeitem]');button('播放该文件夹内的音频',()=>action('play',item));button('添加至播放队列',()=>action('queue',item));place(event.clientX,event.clientY);menu.querySelector('button')?.focus();},playlist:(event,name,action)=>{close();returnFocus=event.target.closest('button');button('设置封面（本地图片…）',()=>action('setCover',name));button('从网络搜索封面…',()=>action('searchCover',name));button('恢复默认（首曲封面）',()=>action('resetCover',name));divider();button('添加到播放队列',()=>action('queuePlaylist',name));button('导出（m3u8）',()=>action('exportPlaylist',name));place(event.clientX,event.clientY);menu.querySelector('button')?.focus();}});
})();
