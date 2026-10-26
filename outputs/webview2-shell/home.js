(() => {
  'use strict';
  const $=id=>document.getElementById(id),shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
  const node=(tag,text,className)=>{const el=document.createElement(tag);if(text!==undefined)el.textContent=text;if(className)el.className=className;return el;};
  const icon=name=>{const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS(svg.namespaceURI,'use');use.setAttribute('href','#i-'+name);svg.append(use);return svg;};
  let snapshot=null,generation=0,recommendations=[],scrollTop=0,lastRevision='',loading=false;
  const today=()=>{const now=new Date(),pad=n=>String(n).padStart(2,'0');return `${now.getFullYear()}-${pad(now.getMonth()+1)}-${pad(now.getDate())}`;};
  const localDate=date=>{const pad=n=>String(n).padStart(2,'0');return `${date.getFullYear()}-${pad(date.getMonth()+1)}-${pad(date.getDate())}`;};
  const dayStart=()=>{const date=new Date();date.setHours(0,0,0,0);date.setDate(date.getDate()-((date.getDay()+6)%7)-77);return date;};
  const homeTrail=()=>[{label:'主页',action:()=>shell.navigate('overview')}];
  function artwork(album,className='home-album-art'){
    const art=node('span',undefined,className);art.append(icon('note'));
    if(album?.coverUrl){const image=node('img');image.alt='';image.loading='lazy';image.decoding='async';image.src=album.coverUrl;image.addEventListener('error',()=>image.remove(),{once:true});art.append(image);}
    return art;
  }
  function albumCard(album){
    const card=node('article',undefined,'home-album-card'),open=node('button',undefined,'home-album-open');
    open.title=`${album.title}\n${album.artist}`;open.setAttribute('aria-label',`打开专辑 ${album.title}，${album.artist}`);
    open.append(artwork(album),node('strong',album.title),node('small',album.artist));open.addEventListener('click',()=>window.CelesteAlbums.openById(album.id,homeTrail()));
    const play=node('button',undefined,'home-album-play');play.append(icon('play'));play.title='播放这张专辑';play.setAttribute('aria-label','播放专辑 '+album.title);play.addEventListener('click',()=>live?shell.playTracks(album.trackIds):shell.playDemo(Number(album.trackIds[0])));
    card.append(open,play);return card;
  }
  function renderAlbums(target,albums,emptyId,emptyText){
    target.replaceChildren(...albums.map(albumCard));const empty=$(emptyId);empty.hidden=albums.length>0;if(emptyText)empty.textContent=emptyText;
  }
  function renderNow(){
    const state=shell.getState(),hasTrack=!!state.trackId||(!live&&state.title),cover=$('home-now-cover');cover.replaceChildren();
    if(hasTrack&&state.coverUrl)cover.append(...artwork({coverUrl:state.coverUrl},'').childNodes);else cover.append(icon('note'));
    $('home-now-label').textContent=hasTrack?(state.playing?'正在播放':'当前曲目 · 已暂停'):'上次播放';
    $('home-now-title').textContent=hasTrack?state.title:(snapshot?.lastTrackTitle||'还没有播放记录');
    $('home-now-album').textContent=hasTrack?(state.album||''):(snapshot?.lastTrackAlbum||'');
    $('home-now-artist').textContent=state.artist||snapshot?.lastTrackArtist||'从曲库挑一首，开始今天的第一段旋律。';
    const albumButton=$('home-now-album'),album=hasTrack&&window.CelesteAlbums.getAlbums().find(item=>item.trackIds.includes(state.trackId));albumButton.disabled=!album;albumButton.onclick=album?()=>window.CelesteAlbums.openById(album.id,homeTrail()):null;
    const primary=$('home-play'),label=primary.querySelector('span');primary.querySelector('use').setAttribute('href',state.playing?'#i-pause':'#i-play');
    label.textContent=state.playing?'暂停':hasTrack?'继续播放':snapshot?.lastTrackId?'播放上次歌曲':shell.getLibrary().length?'开始播放':'添加音乐';
    $('home-subtitle').textContent=hasTrack?`${state.artist||'未知艺术家'} · ${state.album||'未知专辑'}`:'熟悉的旋律，随时接着听。';
    $('home-shuffle').disabled=!shell.getLibrary().length;
  }
  function renderPulse(){
    const tracks=shell.getLibrary(),albums=window.CelesteAlbums.getAlbums(),artists=new Set(tracks.map(track=>(track.artist||'未知艺术家').toLocaleLowerCase()));
    $('home-song-count').textContent=tracks.length.toLocaleString('zh-CN');$('home-album-count').textContent=albums.length.toLocaleString('zh-CN');$('home-artist-count').textContent=artists.size.toLocaleString('zh-CN');
    $('home-favorite-count').textContent=snapshot?Number(snapshot.favoriteCount||0).toLocaleString('zh-CN'):'—';
  }
  function pickRecommendations(){
    const recent=new Set((snapshot?.recentAlbumIds||[]).map(String)),albums=window.CelesteAlbums.getAlbums();
    let pool=albums.filter(album=>!recent.has(String(album.id)));if(!pool.length)pool=albums;
    pool=[...pool];for(let i=pool.length-1;i>0;i--){const j=Math.floor(Math.random()*(i+1));[pool[i],pool[j]]=[pool[j],pool[i]];}
    recommendations=pool.slice(0,Math.min(6,pool.length));renderAlbums($('home-discover-grid'),recommendations,'home-discover-empty');
  }
  function level(count){return count>=20?4:count>=10?3:count>=5?2:count>=1?1:0;}
  function renderHeatmap(){
    const root=$('home-heatmap');root.replaceChildren();if(snapshot?.activityError){$('home-activity-total').textContent='读取失败';const retry=node('button','足迹加载失败，点击重试','text-button');retry.addEventListener('click',()=>refresh(true));root.append(retry);return;}const counts=snapshot?.dailyCounts||{},start=dayStart(),end=today();let total=0;
    for(let i=0;i<84;i++){const date=new Date(start);date.setDate(start.getDate()+i);const key=localDate(date),future=key>end,count=future?0:Number(counts[key]||0);total+=count;
      const cell=node('button',undefined,'home-heat-cell'),label=`${key}，${future?'尚未到来':count+' 条播放记录'}`;cell.dataset.level=String(level(count));cell.dataset.date=key;cell.title=label;cell.setAttribute('aria-label',label);cell.setAttribute('role','gridcell');cell.disabled=future;cell.setAttribute('aria-pressed','false');cell.addEventListener('click',()=>loadDay(key,cell));root.append(cell);
    }
    $('home-activity-total').textContent=total?`${total} 条记录`:'';
  }
  async function loadDay(date,cell,cursor=0){
    document.querySelectorAll('.home-heat-cell').forEach(el=>el.setAttribute('aria-pressed',String(el===cell)));
    const panel=$('home-day-history');panel.hidden=false;panel.querySelector('.home-day-more')?.remove();if(!cursor)panel.replaceChildren(node('p','正在读取这一天的记录…','home-panel-note'));
    try{
      const data=live?await window.CelesteTransport.request('home',{operation:'dayHistory',date,offsetMinutes:new Date().getTimezoneOffset(),cursor,limit:100}):{tracks:[]};
      if(!cursor){panel.replaceChildren();const header=node('header'),title=node('strong',`${date} · ${data.total||0} 条记录`),close=node('button');close.append(icon('close'));close.setAttribute('aria-label','收起当日记录');close.addEventListener('click',()=>{panel.hidden=true;cell.setAttribute('aria-pressed','false');});header.append(title,close);panel.append(header);}
      (data.tracks||[]).forEach(item=>{const row=node('button',undefined,'home-day-track'),copy=node('span');copy.append(node('strong',item.title),node('small',item.artist||item.fileName||'未知艺术家'));row.append(copy,node('time',item.time||''));row.title=item.title;row.disabled=!item.trackId;row.addEventListener('click',()=>shell.playTrack(item.trackId));panel.append(row);});
      if(!cursor&&!(data.tracks||[]).length)panel.append(node('p','这一天没有播放记录。','home-empty'));
      if(data.nextCursor!==null&&data.nextCursor!==undefined){const more=node('button','加载更多','text-button home-day-more');more.addEventListener('click',()=>loadDay(date,cell,data.nextCursor));panel.append(more);}
    }catch(error){if(!cursor)panel.replaceChildren();panel.append(node('p','读取失败：'+error.message,'home-empty'));const retry=node('button','重试','text-button home-day-more');retry.addEventListener('click',()=>loadDay(date,cell,cursor));panel.append(retry);}
  }
  function rankRow(item,index,artist){
    const row=node('li'),button=node('button',undefined,'home-rank-item'),cover=artwork(item,'home-rank-cover'+(artist?' artist':''));
    const copy=node('span',undefined,'home-rank-copy');copy.append(node('strong',item.name||item.title),node('small',artist?'艺术家':item.artist));button.append(node('span',String(index+1).padStart(2,'0'),'home-rank-number'),cover,copy,node('span',`${item.count} 次`,'home-rank-count'));
    button.title=item.name||item.title;button.addEventListener('click',()=>artist?window.CelesteArtists.openByName(item.name,homeTrail()):window.CelesteAlbums.openById(item.id,homeTrail()));row.append(button);return row;
  }
  function renderRanks(){
    const artists=snapshot?.topArtists||[],albums=snapshot?.topAlbums||[];$('home-top-artists').replaceChildren(...artists.map((item,index)=>rankRow(item,index,true)));$('home-top-albums').replaceChildren(...albums.map((item,index)=>rankRow(item,index,false)));
    $('home-top-artists-empty').hidden=artists.length>0;$('home-top-albums-empty').hidden=albums.length>0;
  }
  // 后端保留最多 12 张，主区只铺一行 4 张，高度与右侧足迹对齐；完整列表走「查看全部」。
  function renderAll(){renderPulse();renderNow();if(snapshot){renderAlbums($('home-recent-grid'),(snapshot.recentAlbumIds||[]).map(id=>window.CelesteAlbums.getAlbums().find(album=>String(album.id)===String(id))).filter(Boolean).slice(0,4),'home-recent-empty');renderHeatmap();renderRanks();}}
  async function refresh(force=false){
    if(shell.getPage()!=='overview'||loading&&!force)return;const token=++generation;loading=true;$('home-library-status').textContent='正在更新主页…';
    try{
      if(live){snapshot=await window.CelesteTransport.request('home',{operation:'get',today:today(),offsetMinutes:new Date().getTimezoneOffset()});if(token!==generation)return;lastRevision=String(snapshot.revision??'');}
      else{const recent=window.CelesteAlbums.getAlbums().slice(0,4);snapshot={favoriteCount:0,lastTrackId:'',recentAlbumIds:recent.map(a=>a.id),dailyCounts:{},topArtists:[],topAlbums:[]};recommendations=window.CelesteAlbums.getAlbums().slice(4,10);}
      renderAll();if(!recommendations.length)pickRecommendations();else renderAlbums($('home-discover-grid'),recommendations.filter(album=>window.CelesteAlbums.getAlbums().some(a=>a.id===album.id)),'home-discover-empty');
    }catch(error){if(token!==generation)return;$('home-library-status').textContent='部分数据暂不可用';const retry=node('button','重试','text-button');retry.addEventListener('click',()=>refresh(true));$('home-recent-grid').replaceChildren(retry);$('home-recent-empty').hidden=true;$('home-activity-total').textContent='加载失败';}
    finally{if(token===generation){loading=false;$('home-library-status').textContent='';}}
  }
  $('home-play').addEventListener('click',async()=>{
    const state=shell.getState(),hasTrack=!!state.trackId||(!live&&state.title);
    if(state.playing||hasTrack){if(live)shell.send('togglePlay');else shell.playDemo(state.index);return;}
    if(snapshot?.lastTrackId){shell.playTrack(snapshot.lastTrackId);return;}
    if(!shell.getLibrary().length){if(live){try{await window.CelesteTransport.request('folders',{operation:'add'});}catch(error){shell.notify(error.message);}}else shell.notify('浏览器预览没有本地曲库');return;}
    shuffle();
  });
  function shuffle(){const ids=shell.getLibrary().map(track=>track.id);for(let i=ids.length-1;i>0;i--){const j=Math.floor(Math.random()*(i+1));[ids[i],ids[j]]=[ids[j],ids[i]];}if(!ids.length)return;if(live)shell.playTracks(ids.slice(0,30));else shell.playDemo(Math.floor(Math.random()*ids.length));}
  $('home-shuffle').addEventListener('click',shuffle);$('home-refresh-albums').addEventListener('click',pickRecommendations);
  document.addEventListener('celeste:navigate',event=>{if(event.detail==='overview'){requestAnimationFrame(()=>{document.querySelector('.main').scrollTop=scrollTop;});refresh();}else{generation++;loading=false;scrollTop=document.querySelector('.main').scrollTop;$('home-day-history').hidden=true;}});
  document.addEventListener('celeste:library',()=>{if(shell.getPage()==='overview'){recommendations=[];refresh(true);}});
  document.addEventListener('celeste:message',event=>{
    const kind=event.detail.kind;
    if(kind==='celeste.state'&&shell.getPage()==='overview')requestAnimationFrame(renderNow);
    if(kind==='celeste.homeChanged'&&shell.getPage()==='overview')refresh(true);
  });
  renderPulse();renderNow();if(live)refresh();else{snapshot={favoriteCount:0,recentAlbumIds:[],dailyCounts:{},topArtists:[],topAlbums:[]};renderAll();pickRecommendations();}
})();
