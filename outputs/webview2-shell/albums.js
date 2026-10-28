(() => {
  'use strict';
  const $=id=>document.getElementById(id),shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
  const collator=new Intl.Collator('zh-CN',{numeric:true,sensitivity:'base'});
  let albums=[],trackMap=new Map(),opened=null,ascending=true,limit=80,randomOrder=new Map(),parentTrail=[];
  // 浏览方式（网格 / CD 架）跟人走：存在本机，下次打开还是上次那一种
  const VIEW_KEY='celeste.albums.view';
  let view='grid',shelfFocusId='',pendingScroll=0,restoreFocus=false;
  try{view=localStorage.getItem(VIEW_KEY)==='shelf'?'shelf':'grid';}catch{}
  const format=value=>`${Math.floor(value/60)}:${String(Math.floor(value%60)).padStart(2,'0')}`;
  function rebuild(){
    const tracks=shell.getLibrary();trackMap=new Map(tracks.map((track,index)=>[live?track.id:String(index),track]));
    if(live)albums=shell.getAlbums();
    else{
      const groups=new Map();tracks.forEach((track,index)=>{const key=track.album.toLocaleLowerCase();if(!groups.has(key))groups.set(key,[]);groups.get(key).push({...track,id:String(index)});});
      albums=[...groups.entries()].map(([id,items],index)=>({id,title:items[0].album,artist:items[0].artist,year:items[0].year||0,added:index,trackCount:items.length,duration:items.reduce((sum,t)=>sum+t.duration,0),trackIds:items.map(t=>t.id),art:items[0].art,sleeve:items[0].sleeve}));
    }
    randomOrder=new Map(albums.map(album=>[album.id,Math.random()]));
    if(opened){const current=albums.find(album=>album.id===opened.id);if(current)opened=current;else opened=null;}
  }
  function artwork(album){
    const art=document.createElement('span');art.className='album-art'+(!live?' art-'+album.art:'');
    if(live){const mark=document.createElementNS('http://www.w3.org/2000/svg','svg');const use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href','#i-note');mark.append(use);mark.setAttribute('aria-hidden','true');mark.classList.add('art-note');art.append(mark);}
    else{const placeholder=document.createElement('small');placeholder.textContent=album.sleeve||'CELESTE';art.append(placeholder);}
    if(album.coverUrl){const image=document.createElement('img');image.alt='';image.loading='lazy';image.decoding='async';image.src=album.coverUrl;image.addEventListener('error',()=>image.remove(),{once:true});art.append(image);}return art;
  }
  function playAlbum(album){if(live)shell.playTracks(album.trackIds);else shell.playDemo(Number(album.trackIds[0]));}
  function visibleAlbums(){
    const query=$('search').value.trim().toLocaleLowerCase(),key=$('albums-sort').value;
    return albums.filter(album=>!query||[album.title,album.artist,album.year||''].some(value=>String(value).toLocaleLowerCase().includes(query))).sort((a,b)=>{
      let result=key==='random'?(randomOrder.get(a.id)||0)-(randomOrder.get(b.id)||0):key==='year'?(Number(a.year)||(ascending?Number.MAX_SAFE_INTEGER:0))-(Number(b.year)||(ascending?Number.MAX_SAFE_INTEGER:0)):['added','trackCount','duration'].includes(key)?Number(a[key]||0)-Number(b[key]||0):collator.compare(a[key]||'',b[key]||'');
      return (result||collator.compare(a.title,b.title))*(ascending?1:-1);
    });
  }
  function renderGrid(visible){
    // 换排序时卡片会整体换位：先记位置，重建后让它们滑过去
    const cardPositions=window.CelesteMotion?.captureRows($('albums-grid'));
    $('albums-grid').replaceChildren();
    visible.forEach(album=>{
      const card=document.createElement('article');card.className='album-card';card.dataset.albumId=album.id;card.dataset.flipkey=album.id;
      const open=document.createElement('button');open.className='album-open';open.setAttribute('aria-label',`打开专辑 ${album.title}，${album.artist}`);open.title=album.title+'\n'+album.artist;
      const title=document.createElement('strong');title.className='album-card-title';title.textContent=album.title;
      const artist=document.createElement('span');artist.className='album-card-artist';artist.textContent=album.artist;
      const meta=document.createElement('small');meta.className='album-card-meta';meta.textContent=[album.year||'',album.trackCount+' 首'].filter(Boolean).join(' · ');
      open.append(artwork(album),title,artist,meta);open.addEventListener('click',()=>openAlbum(album));
      const play=document.createElement('button');play.className='album-quick-play';play.setAttribute('aria-label','播放专辑 '+album.title);play.title='播放这张专辑';
      const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS(svg.namespaceURI,'use');use.setAttribute('href','#i-play');svg.append(use);play.append(svg);play.addEventListener('click',()=>playAlbum(album));card.append(open,play);$('albums-grid').append(card);
    });
    const moved=window.CelesteMotion?.replayRows($('albums-grid'),cardPositions)||0;
    if(!moved)window.CelesteMotion?.fadeIn($('albums-grid'),'list');
  }
  // 正在播放的是哪张专辑：只认宿主 state 里的曲目 id，不在网页里另记一份
  function playingAlbumId(){
    const state=shell.getState(),trackId=live?state.trackId:String(state.index);
    if(!trackId)return '';
    const album=albums.find(item=>item.trackIds.includes(trackId));
    return album?album.id:'';
  }
  function renderShelf(visible,focusNow){
    window.CelesteCdShelf?.render({host:$('albums-shelf'),albums:visible,artwork,playingId:playingAlbumId(),focusId:shelfFocusId,focusNow,onOpen:openAlbum,onPlay:playAlbum,onFocus:id=>{shelfFocusId=id;}});
  }
  function render(){
    const visible=visibleAlbums(),query=$('search').value.trim(),focusNow=restoreFocus;
    restoreFocus=false;
    $('albums-count').textContent=query?`${visible.length} 张匹配 · 共 ${albums.length} 张专辑`:`${albums.length} 张专辑 · ${shell.getLibrary().length} 首歌曲`;
    // 三种"一张都没有"要说清是哪一种：还在扫库、库是空的、还是搜索没命中
    const empty=$('albums-empty');
    empty.hidden=visible.length>0;
    if(!visible.length)empty.textContent=shell.isLibraryLoading()?'正在载入曲库，专辑会随扫描完成陆续出现。':query?'没有找到相关专辑，试试其他专辑名或艺术家。':'曲库里还没有专辑，添加音乐后会自动出现在这里。';
    $('albums-more').hidden=visible.length<=limit;
    const page=visible.slice(0,limit),shelf=view==='shelf';
    $('albums-grid').hidden=shelf;$('albums-shelf').hidden=!shelf;
    // 另一套视图立刻清空：留着几十张隐藏封面只会白占解码和内存
    if(shelf){$('albums-grid').replaceChildren();renderShelf(page,focusNow);}
    else{window.CelesteCdShelf?.clear();renderGrid(page);}
  }
  function openAlbum(album){
    // 进详情前记住列表滚到哪、焦点在哪：返回时原样还回去，别从头开始找
    if(!$('albums-browser').hidden){pendingScroll=document.querySelector('.main').scrollTop;if(view==='shelf')shelfFocusId=album.id;}
    opened=album;$('albums-browser').hidden=true;$('album-detail').hidden=false;
    shell.setPath([...(parentTrail.length?parentTrail:[{label:'专辑',action:backToBrowser}]),{label:album.title,action:()=>openAlbum(album)}]);
    $('album-detail-cover').replaceChildren(artwork(album));$('album-detail-title').textContent=album.title;$('album-detail-artist').textContent=album.artist;
    $('album-detail-meta').textContent=[album.year?album.year+' 年':'',album.trackCount+' 首歌曲',Math.floor(album.duration/60)+' 分钟'].filter(Boolean).join(' · ');
    $('album-play').disabled=!album.trackIds.length;$('album-tracks').replaceChildren();
    let lastDisc=null;const tracks=album.trackIds.map(id=>({id,track:trackMap.get(id)})).filter(item=>item.track);
    const multipleDiscs=new Set(tracks.map(({track})=>Number(track.disc)||1)).size>1;
    tracks.forEach(({id,track},index)=>{
      const disc=Number(track.disc)||1;if(multipleDiscs&&disc!==lastDisc){const heading=document.createElement('h3');heading.className='album-disc-heading';heading.textContent='DISC '+disc;$('album-tracks').append(heading);lastDisc=disc;}
      const button=document.createElement('button');button.className='album-track';button.dataset.trackId=id;button.setAttribute('aria-label',`${track.title}，${track.artist}，${format(track.duration)}`);
      const number=document.createElement('span');number.className='track-number';number.textContent=track.track||index+1;const copy=document.createElement('span'),title=document.createElement('strong'),artist=document.createElement('small'),duration=document.createElement('time');
      title.textContent=track.title;artist.textContent=track.artist;duration.textContent=format(track.duration);copy.append(title,artist);button.append(number,copy,duration);
      const play=()=>live?shell.playTrack(id):shell.playDemo(Number(id));button.addEventListener('dblclick',play);button.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();play();}});$('album-tracks').append(button);
    });
    updatePlaying();document.querySelector('.main').scrollTop=0;
    document.dispatchEvent(new CustomEvent('celeste:album-open',{detail:{album,tracks:tracks.map(item=>item.track)}}));
  }
  function updatePlaying(){
    const state=shell.getState();
    document.querySelectorAll('.album-track').forEach(row=>row.setAttribute('aria-current',String(live?row.dataset.trackId===state.trackId:Number(row.dataset.trackId)===state.index)));
    // 架子上只改播放标记，不重建 —— 切歌和每秒的进度刷新都会走到这里
    window.CelesteCdShelf?.setPlaying(playingAlbumId());
  }
  function backToBrowser(){opened=null;restoreFocus=true;show();}
  function restoreScroll(){if(!pendingScroll)return;const main=document.querySelector('.main'),top=pendingScroll;pendingScroll=0;main.scrollTop=top;}
  function show(){
    if(opened){openAlbum(opened);return;}
    shell.setPath([{label:'专辑',action:backToBrowser}]);
    $('albums-browser').hidden=false;$('album-detail').hidden=true;
    render();
    restoreScroll();
  }
  $('album-back').addEventListener('click',backToBrowser);$('album-play').addEventListener('click',()=>{if(opened)playAlbum(opened);});
  $('albums-sort').addEventListener('change',()=>{if($('albums-sort').value==='random')randomOrder=new Map(albums.map(album=>[album.id,Math.random()]));render();});
  $('albums-direction').addEventListener('click',()=>{ascending=!ascending;$('albums-direction').textContent=ascending?'升序 ↑':'降序 ↓';render();});$('albums-more').addEventListener('click',()=>{limit+=80;render();});
  const viewButtons=[...document.querySelectorAll('[data-album-view]')];
  function paintViewButtons(){viewButtons.forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.albumView===view)));}
  viewButtons.forEach(button=>button.addEventListener('click',()=>{
    const next=button.dataset.albumView==='shelf'?'shelf':'grid';
    if(next===view)return;
    view=next;try{localStorage.setItem(VIEW_KEY,view);}catch{}
    paintViewButtons();pendingScroll=0;restoreFocus=false;
    render();
  }));
  paintViewButtons();
  $('search').addEventListener('input',()=>{if(shell.getPage()==='albums'){opened=null;limit=80;show();}});
  document.addEventListener('celeste:navigate',event=>{if(event.detail==='albums'){rebuild();show();}});
  document.addEventListener('celeste:library',()=>{rebuild();if(shell.getPage()==='albums')show();});
  document.addEventListener('celeste:message',event=>{if(event.detail.kind==='celeste.state')requestAnimationFrame(updatePlaying);});
  window.CelesteAlbums=Object.freeze({getAlbums:()=>albums,artwork,openById:(id,trail=[])=>{parentTrail=trail;rebuild();const album=albums.find(item=>item.id===id);if(!album)return false;opened=album;shell.navigate('albums');return true;},openForTrack:id=>{parentTrail=[];rebuild();const album=albums.find(item=>item.trackIds.includes(id));if(!album)return false;opened=album;shell.navigate('albums');return true;}});
  rebuild();
})();
