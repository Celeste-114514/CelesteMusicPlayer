(() => {
  'use strict';
  const $=id=>document.getElementById(id),shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
  const collator=new Intl.Collator('zh-CN',{numeric:true,sensitivity:'base'});
  let albums=[],trackMap=new Map(),opened=null,ascending=true,limit=80,randomOrder=new Map(),parentTrail=[];
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
  function renderGrid(){
    const query=$('search').value.trim().toLocaleLowerCase(),key=$('albums-sort').value;
    const visible=albums.filter(album=>!query||[album.title,album.artist,album.year||''].some(value=>String(value).toLocaleLowerCase().includes(query))).sort((a,b)=>{
      let result=key==='random'?(randomOrder.get(a.id)||0)-(randomOrder.get(b.id)||0):key==='year'?(Number(a.year)||(ascending?Number.MAX_SAFE_INTEGER:0))-(Number(b.year)||(ascending?Number.MAX_SAFE_INTEGER:0)):['added','trackCount','duration'].includes(key)?Number(a[key]||0)-Number(b[key]||0):collator.compare(a[key]||'',b[key]||'');
      return (result||collator.compare(a.title,b.title))*(ascending?1:-1);
    });
    $('albums-count').textContent=query?`${visible.length} 张匹配 · 共 ${albums.length} 张专辑`:`${albums.length} 张专辑 · ${shell.getLibrary().length} 首歌曲`;
    $('albums-grid').replaceChildren();$('albums-empty').hidden=visible.length>0;$('albums-more').hidden=visible.length<=limit;
    visible.slice(0,limit).forEach(album=>{
      const card=document.createElement('article');card.className='album-card';card.dataset.albumId=album.id;
      const open=document.createElement('button');open.className='album-open';open.setAttribute('aria-label',`打开专辑 ${album.title}，${album.artist}`);open.title=album.title+'\n'+album.artist;
      const title=document.createElement('strong');title.className='album-card-title';title.textContent=album.title;
      const artist=document.createElement('span');artist.className='album-card-artist';artist.textContent=album.artist;
      const meta=document.createElement('small');meta.className='album-card-meta';meta.textContent=[album.year||'',album.trackCount+' 首'].filter(Boolean).join(' · ');
      open.append(artwork(album),title,artist,meta);open.addEventListener('click',()=>openAlbum(album));
      const play=document.createElement('button');play.className='album-quick-play';play.setAttribute('aria-label','播放专辑 '+album.title);play.title='播放这张专辑';
      const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS(svg.namespaceURI,'use');use.setAttribute('href','#i-play');svg.append(use);play.append(svg);play.addEventListener('click',()=>playAlbum(album));card.append(open,play);$('albums-grid').append(card);
    });
    window.CelesteMotion?.fadeIn($('albums-grid'),'list');
  }
  function openAlbum(album){
    opened=album;$('albums-browser').hidden=true;$('album-detail').hidden=false;
    shell.setPath([...(parentTrail.length?parentTrail:[{label:'专辑',action:()=>{opened=null;show();}}]),{label:album.title,action:()=>openAlbum(album)}]);
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
  function updatePlaying(){const state=shell.getState();document.querySelectorAll('.album-track').forEach(row=>row.setAttribute('aria-current',String(live?row.dataset.trackId===state.trackId:Number(row.dataset.trackId)===state.index)));}
  function show(){if(opened)openAlbum(opened);else{shell.setPath([{label:'专辑',action:()=>{opened=null;show();}}]);$('albums-browser').hidden=false;$('album-detail').hidden=true;renderGrid();}}
  $('album-back').addEventListener('click',()=>{opened=null;show();});$('album-play').addEventListener('click',()=>{if(opened)playAlbum(opened);});
  $('albums-sort').addEventListener('change',()=>{if($('albums-sort').value==='random')randomOrder=new Map(albums.map(album=>[album.id,Math.random()]));renderGrid();});
  $('albums-direction').addEventListener('click',()=>{ascending=!ascending;$('albums-direction').textContent=ascending?'升序 ↑':'降序 ↓';renderGrid();});$('albums-more').addEventListener('click',()=>{limit+=80;renderGrid();});
  $('search').addEventListener('input',()=>{if(shell.getPage()==='albums'){opened=null;limit=80;show();}});
  document.addEventListener('celeste:navigate',event=>{if(event.detail==='albums'){rebuild();show();}});
  document.addEventListener('celeste:library',()=>{rebuild();if(shell.getPage()==='albums')show();});
  document.addEventListener('celeste:message',event=>{if(event.detail.kind==='celeste.state')requestAnimationFrame(updatePlaying);});
  window.CelesteAlbums=Object.freeze({getAlbums:()=>albums,artwork,openById:(id,trail=[])=>{parentTrail=trail;rebuild();const album=albums.find(item=>item.id===id);if(!album)return false;opened=album;shell.navigate('albums');return true;},openForTrack:id=>{parentTrail=[];rebuild();const album=albums.find(item=>item.trackIds.includes(id));if(!album)return false;opened=album;shell.navigate('albums');return true;}});
  rebuild();
})();
