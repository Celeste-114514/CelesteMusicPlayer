(() => {
 'use strict';
 const $=id=>document.getElementById(id),shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
 const titles={favorites:'我喜欢的音乐',ratings:'评分',recent:'最近播放',mostPlayed:'播放最多',playlists:'播放列表',queue:'播放队列'};
 const compare=new Intl.Collator('zh-CN',{numeric:true,sensitivity:'base'}),element=(tag,text,cls)=>{const n=document.createElement(tag);if(text!==undefined)n.textContent=text;if(cls)n.className=cls;return n;};
 let page='',data={tracks:[],playlists:[]},folder='',playlist=null,rating=-1,ascending=true,limit=200,rows=[],token=0,queue=null,loading=false;
 const path=value=>(value||'').replaceAll('\\','/').replace(/\/$/,'');
 const time=seconds=>`${Math.floor(seconds/60)}:${String(Math.floor(seconds%60)).padStart(2,'0')}`;
 const tracks=()=>shell.getLibrary().map((t,i)=>({...t,id:live?t.id:String(i)}));
 const inFolder=(track,dir)=>track.folder.toLocaleLowerCase()===dir.toLocaleLowerCase()||track.folder.toLocaleLowerCase().startsWith(dir.toLocaleLowerCase()+'/');
 function trail(){
  const items=[{label:titles[page],action:()=>{folder='';playlist=null;render();}}];
  if(page==='folders'&&folder){const parts=folder.split('/');parts.forEach((part,i)=>{const dir=parts.slice(0,i+1).join('/');if(part)items.push({label:part,action:()=>{folder=dir;render();}});});}
  if(page==='playlists'&&playlist)items.push({label:playlist.name,action:render});shell.setPath(items);
 }
 function card(name,summary,iconId,action,coverUrl){const button=element('button',undefined,'browse-card');const art=element('span',undefined,'folder-symbol');const artSvg=document.createElementNS('http://www.w3.org/2000/svg','svg'),artUse=document.createElementNS('http://www.w3.org/2000/svg','use');artUse.setAttribute('href',iconId);artSvg.append(artUse);art.append(artSvg);if(coverUrl){const img=element('img');img.src=coverUrl;img.alt='';img.loading='lazy';img.addEventListener('error',()=>img.remove(),{once:true});art.append(img);}button.append(art,element('strong',name),element('small',summary));button.addEventListener('click',()=>{action();document.querySelector('.main').scrollTop=0;});$('browse-cards').append(button);return button;}
 function library(){const stats=new Map(data.tracks.map(t=>[t.id,t]));return tracks().map(t=>({...t,...stats.get(t.id),folder:path(stats.get(t.id)?.folder||'演示音乐/'+t.artist)}));}
 function selected(){
  let list=library();
  if(page==='favorites')list=list.filter(t=>t.favorite);
  if(page==='ratings'&&rating>=0)list=list.filter(t=>(t.rating||0)===rating);
  if(page==='recent')list=list.filter(t=>t.lastPlayed).sort((a,b)=>String(b.lastPlayed).localeCompare(String(a.lastPlayed)));
  if(page==='mostPlayed')list=list.filter(t=>t.playCount>0).sort((a,b)=>b.playCount-a.playCount);
  if(page==='folders')list=folder?list.filter(t=>inFolder(t,folder)):list;
  if(page==='playlists'){const map=new Map(list.map(t=>[t.id,t]));list=playlist?playlist.trackIds.map(id=>map.get(id)).filter(Boolean):[];}
  if(page==='queue')list=(queue?.tracks||[]).map(t=>({...t,queueIndex:t.index}));
  return list;
 }
 async function mutate(operation,payload){try{loading=true;if(live)data=await window.CelesteTransport.request('browse',{operation,...payload});else{const item=data.tracks.find(t=>t.id===payload.trackId);if(item)item[operation]=payload.value;}render();}catch(error){$('browse-status').textContent=error.message;}finally{loading=false;}}
 function render(){
  if(!titles[page]||shell.getPage()!==page)return;
  trail();$('browse-title').textContent=playlist?.name||(folder?folder.split('/').at(-1):titles[page]);$('browse-cards').replaceChildren();$('browse-tracks').replaceChildren();$('playlist-create').hidden=page!=='playlists'||!!playlist;$('browse-filters').hidden=page!=='ratings';
  if(page==='ratings'){$('browse-filters').replaceChildren();for(let value=-1;value<=5;value++){const button=element('button',value<0?'全部':value===0?'未评分':'★'.repeat(value));button.setAttribute('aria-pressed',String(rating===value));button.addEventListener('click',()=>{rating=value;limit=200;render();});$('browse-filters').append(button);}}
  let all=selected(),query=$('search').value.trim().toLocaleLowerCase();
  $('browse-count').textContent=page==='queue'?`${queue?.total||0} 首 · 当前队列`:`${all.length} 首歌曲`;
  $('browse-play').disabled=!all.length||loading;
  $('browse-status').textContent=!live?'演示曲库 · 收藏、评分和记录仅供界面预览':page==='playlists'&&playlist?.missingCount?`${playlist.missingCount} 个文件未加入曲库，未在下方显示；播放该列表时仍会读取其中存在的文件。`:'';
  if(page==='folders'){
   const source=library().filter(t=>!folder||inFolder(t,folder)),children=new Map();
   source.forEach(t=>{const rest=folder?t.folder.slice(folder.length).replace(/^\//,''):t.folder;if(!rest)return;const name=rest.split('/')[0],dir=folder?folder+'/'+name:name;if(!children.has(dir))children.set(dir,{name,count:0});children.get(dir).count++;});
   [...children].sort((a,b)=>compare.compare(a[1].name,b[1].name)*(ascending?1:-1)).filter(([dir])=>!query||dir.toLocaleLowerCase().includes(query)).forEach(([dir,item])=>card(item.name,item.count+' 首歌曲','#i-folder',()=>{folder=dir;limit=200;render();}));
   if(!folder)$('browse-count').textContent=`${children.size} 个来源目录 · ${library().length} 首歌曲`;
   all=all.filter(t=>t.folder.toLocaleLowerCase()===folder.toLocaleLowerCase());
  }
  if(page==='playlists'&&!playlist){data.playlists.filter(item=>!query||item.name.toLocaleLowerCase().includes(query)).sort((a,b)=>compare.compare(a.name,b.name)*(ascending?1:-1)).forEach(item=>{const button=card(item.name,item.trackIds.length+' 首歌曲'+(item.missingCount?' · '+item.missingCount+' 个未入库文件':''),'#i-note',()=>{playlist=item;limit=200;render();},item.coverUrl||library().find(t=>t.id===item.trackIds[0])?.coverUrl);button.dataset.playlistName=item.name;});$('browse-count').textContent=data.playlists.length+' 个播放列表';}
  playlistToolbar();
  rows=all.filter(t=>!query||[t.title,t.artist,t.album].some(value=>String(value||'').toLocaleLowerCase().includes(query)));
  const sort=$('browse-sort').value;if(sort!=='default')rows.sort((a,b)=>{const result=['rating','duration','playCount'].includes(sort)?Number(a[sort]||0)-Number(b[sort]||0):compare.compare(String(a[sort]||''),String(b[sort]||''));return (result||compare.compare(a.title||'',b.title||''))*(ascending?1:-1);});else if(!ascending)rows.reverse();
  rows.slice(0,limit).forEach((track,index)=>{
   const row=element('div',undefined,'album-track browse-row');row.dataset.trackId=track.id;if(page==='queue'){row.dataset.queueIndex=track.queueIndex;row.dataset.queueRevision=queue.revision;}row.tabIndex=0;row.setAttribute('role','button');row.setAttribute('aria-label',`播放 ${track.title}`);
   const cover=element('img');cover.className='row-cover';cover.alt='';cover.loading='lazy';if(track.coverUrl){cover.src=track.coverUrl;cover.addEventListener('error',()=>cover.removeAttribute('src'),{once:true});}
   const copy=element('span');copy.append(element('strong',track.title),element('small',[track.artist,track.album].filter(Boolean).join(' · ')));const tools=element('span',undefined,'row-tools');
   if(page==='mostPlayed')tools.append(element('span',track.playCount+' 次'));
   if(page==='recent')tools.append(element('span',new Date(track.lastPlayed).toLocaleDateString('zh-CN')));
   if(page!=='queue'){
    const favorite=element('button',track.favorite?'♥':'♡');favorite.title=track.favorite?'取消喜欢':'喜欢';favorite.setAttribute('aria-pressed',String(!!track.favorite));favorite.addEventListener('click',()=>!loading&&mutate('favorite',{trackId:track.id,value:!track.favorite}));tools.append(favorite);
    const select=element('select');select.className='rating';select.setAttribute('aria-label','为 '+track.title+' 评分');for(let rating=0;rating<=5;rating++){const option=element('option',rating?'★'.repeat(rating):'未评分');option.value=rating;select.append(option);}select.value=track.rating||0;select.addEventListener('change',()=>!loading&&mutate('rating',{trackId:track.id,value:Number(select.value)}));tools.append(select);
   }
   tools.addEventListener('dblclick',event=>event.stopPropagation());tools.addEventListener('keydown',event=>event.stopPropagation());row.append(element('span',index+1,'track-number'),cover,copy,tools,element('time',time(track.duration||0)));
   row.addEventListener('dblclick',()=>start(track));row.addEventListener('keydown',event=>{if(event.key==='Enter'&&event.target===row){event.preventDefault();start(track);}});$('browse-tracks').append(row);
  });
  $('browse-more').hidden=rows.length<=limit&&!(page==='queue'&&(queue?.tracks.length||0)<(queue?.total||0));
  if(!rows.length&&!$('browse-cards').children.length)$('browse-status').textContent=query?'没有找到匹配内容。':({favorites:'点亮歌曲旁的爱心，就能在这里找到它。',recent:'开始播放后，这里会记录最近听过的歌曲。',mostPlayed:'听得越多，越能看见自己的偏爱。',playlists:'创建第一个播放列表，为喜欢的音乐留个位置。',queue:'当前播放队列为空。'}[page]||'这里暂时没有歌曲。');
  playing();
 }
 async function start(track){try{if(page==='queue'&&live)await window.CelesteTransport.request('queue',{operation:'play',revision:queue.revision,index:track.queueIndex,trackId:track.id});else if(live)shell.playTrack(track.id);else shell.playDemo(Number(track.id));}catch(error){shell.notify(error.message);load();}}
 async function load(append=false){
  const current=++token;loading=true;$('browse-status').textContent='正在读取曲库…';$('browse-play').disabled=true;
  try{
   if(page==='queue'){
    const result=live?await window.CelesteTransport.request('queue',{operation:'get',offset:append?queue?.tracks.length||0:0}):{revision:0,total:tracks().length,tracks:tracks().map((t,index)=>({...t,index}))};
    if(current!==token)return;if(append&&queue?.revision!==result.revision)return load();queue=append?{...result,tracks:[...queue.tracks,...result.tracks]}:result;
   }else if(live){const result=await window.CelesteTransport.request('browse',{operation:'get'});if(current!==token)return;data=result;if(playlist)playlist=data.playlists.find(item=>item.name===playlist.name)||null;}
   else if(!data.tracks.length)data={tracks:tracks().map((t,i)=>({id:t.id,folder:'演示音乐/'+t.artist,favorite:i<3,rating:i%6,playCount:i<6?12-i:0,lastPlayed:i<4?new Date(Date.now()-i*86400000).toISOString():''})),playlists:[{name:'片刻收藏',trackIds:['0','1','2']}]};
   loading=false;render();
  }catch(error){if(current===token)$('browse-status').textContent=error.message;}finally{if(current===token)loading=false;}
 }
 function playing(){const state=shell.getState();document.querySelectorAll('.browse-row').forEach(row=>row.setAttribute('aria-current',String(page==='queue'?Number(row.dataset.queueIndex)===queue?.currentIndex:row.dataset.trackId===state.trackId)));}
 $('browse-play').addEventListener('click',()=>{if(page==='playlists'&&playlist&&live)return playlistAction('playPlaylist');const all=page==='folders'?library().filter(t=>!folder||inFolder(t,folder)):rows;if(!all.length)return;if(page==='queue')start(all[0]);else if(live)shell.playTracks(all.map(t=>t.id));else shell.playDemo(Number(all[0].id));});
 $('browse-sort').addEventListener('change',render);$('browse-direction').addEventListener('click',()=>{ascending=!ascending;$('browse-direction').textContent=ascending?'升序 ↑':'降序 ↓';render();});$('browse-refresh').addEventListener('click',()=>load());$('browse-more').addEventListener('click',()=>{limit+=200;if(page==='queue'&&queue.tracks.length<queue.total)load(true);else render();});
 $('playlist-create').addEventListener('submit',async event=>{event.preventDefault();if(loading)return;const name=$('playlist-name').value.trim();if(!name)return;try{if(live)data=await window.CelesteTransport.request('browse',{operation:'createPlaylist',name});else data.playlists.push({name,trackIds:[]});$('playlist-name').value='';render();}catch(error){$('browse-status').textContent=error.message;}});
 $('search').addEventListener('input',()=>{limit=200;render();});
 document.addEventListener('celeste:navigate',event=>{token++;if(!titles[event.detail]){page='';return;}if(page!==event.detail){folder='';playlist=null;rating=-1;limit=200;ascending=true;$('browse-direction').textContent='升序 ↑';$('browse-sort').value='default';}page=event.detail;$('browse-title').textContent=titles[page];$('browse-cards').replaceChildren();$('browse-tracks').replaceChildren();$('search').placeholder='搜索'+titles[page];load();});
 document.addEventListener('celeste:library',()=>{if(titles[page])load();});document.addEventListener('celeste:message',event=>{if(event.detail.kind==='celeste.state')requestAnimationFrame(playing);});
 document.addEventListener('celeste:tracksChanged',()=>{if(titles[page])load();});
 const actions=element('div',undefined,'playlist-actions');$('browse-filters').before(actions);
 const playlistButtons=[['playlist-new','新建播放列表',()=>{$('playlist-name').focus();}],['playlist-import','导入 M3U…',()=>playlistAction('importM3u')],['playlist-cue','打开 CUE…',()=>playlistAction('openCue')],['playlist-queue','添加至播放队列',()=>playlistAction('queuePlaylist')],['playlist-copy','添加到播放列表',()=>playlistAction('copyPlaylist')],['playlist-export','导出为 m3u8',()=>playlistAction('exportPlaylist')],['playlist-cover','设置封面…',()=>playlistAction('setCover')],['playlist-search-cover','从网络搜索封面…',()=>playlistAction('searchCover')],['playlist-reset-cover','恢复首曲封面',()=>playlistAction('resetCover')]];
 playlistButtons.forEach(([id,label,action],index)=>{const button=element('button',label,index===0?'primary':'secondary');button.id=id;button.addEventListener('click',action);actions.append(button);});
 const detailCover=element('img',undefined,'playlist-detail-cover');detailCover.alt='播放列表封面';detailCover.addEventListener('error',()=>detailCover.hidden=true);$('browse-title').before(detailCover);
 function playlistToolbar(){const isList=page==='playlists',detail=isList&&!!playlist;actions.hidden=!isList;playlistButtons.forEach(([id],index)=>{$(id).hidden=index<3?detail:!detail;$(id).disabled=loading;});$('browse-play').hidden=isList&&!detail;$('browse-play').textContent=detail?'播放该列表':'播放全部';if(detail)$('browse-play').disabled=loading||!(playlist.trackIds.length||playlist.missingCount);const sort=$('browse-sort').closest('.sort-picker');['playlist-queue','playlist-copy','playlist-export'].forEach(id=>{if(detail)sort.before($(id));else actions.append($(id));});detailCover.hidden=!detail||!playlist.coverUrl;if(detail&&playlist.coverUrl&&detailCover.getAttribute('src')!==playlist.coverUrl)detailCover.src=playlist.coverUrl;}
 async function playlistAction(operation,name=playlist?.name){if(loading)return;if(!live)return shell.notify('请在桌面播放器中使用此功能。');const generation=token;loading=true;playlistToolbar();try{const result=await window.CelesteTransport.request('browse',{operation,name});if(generation!==token)return;data=result;if(playlist)playlist=data.playlists.find(item=>item.name===playlist.name)||null;loading=false;render();}catch(error){shell.notify(error.message);}finally{loading=false;playlistToolbar();}}
 $('browse-cards').addEventListener('contextmenu',event=>{const card=event.target.closest('[data-playlist-name]');if(!card)return;event.preventDefault();window.CelesteTrackMenu?.playlist(event,card.dataset.playlistName,playlistAction);});
 setInterval(async()=>{if(page!=='queue'||loading||document.hidden||!live)return;const current=token;try{const result=await window.CelesteTransport.request('queue',{operation:'get',offset:0});if(current!==token||page!=='queue')return;if(result.revision!==queue?.revision){queue=result;render();}else{queue.currentIndex=result.currentIndex;playing();}}catch{}},5000);
})();
