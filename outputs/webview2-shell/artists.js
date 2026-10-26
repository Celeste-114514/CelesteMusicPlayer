(() => {
  'use strict';
  const $=id=>document.getElementById(id),shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
  const compare=new Intl.Collator('zh-CN',{numeric:true,sensitivity:'base'}),cache=new Map();
  let kind='artists',groups=[],opened=null,ascending=true,limit=80,tab='songs',detailAscending=true,generation=0;
  const detailPreferences={songs:{key:'title',ascending:true},albums:{key:'title',ascending:true}};
  const duration=seconds=>{const minutes=Math.floor(seconds/60);return minutes>=60?`${Math.floor(minutes/60)} 小时 ${minutes%60} 分钟`:`${minutes} 分钟`;};
  const time=seconds=>`${Math.floor(seconds/60)}:${String(Math.floor(seconds%60)).padStart(2,'0')}`;
  const node=(tag,text,className)=>{const element=document.createElement(tag);if(text!==undefined)element.textContent=text;if(className)element.className=className;return element;};
  function base(tracks){
    const years=tracks.map(track=>Number(track.year)||0).filter(Boolean);
    return {songCount:tracks.length,albumCount:new Set(tracks.map(track=>(track.album||'').toLocaleLowerCase())).size,duration:tracks.reduce((sum,track)=>sum+Number(track.duration||0),0),yearFrom:years.length?Math.min(...years):0,yearTo:years.length?Math.max(...years):0,genres:[...new Set(tracks.map(track=>track.genre).filter(value=>value&&value!=='未知流派'))].slice(0,5),localCount:tracks.filter(track=>!track.remote).length,remoteCount:tracks.filter(track=>track.remote).length};
  }
  function rebuild(){
    const map=new Map();shell.getLibrary().forEach((track,index)=>{
      const name=(kind==='albumArtists'?track.albumArtist:track.artist)||'未知艺术家',key=name.toLocaleLowerCase();
      if(!map.has(key))map.set(key,{name,key,tracks:[]});map.get(key).tracks.push({...track,id:live?track.id:String(index)});
    });
    groups=[...map.values()].map(group=>({...group,...base(group.tracks)}));
    if(opened)opened=groups.find(group=>group.key===opened.key)||null;
  }
  function portrait(group){
    const art=node('span',group.name.slice(0,1),'artist-image');
    const track=[...group.tracks].sort((a,b)=>(b.year||0)-(a.year||0)).find(track=>track.coverUrl||track.art);
    if(track?.coverUrl){const image=node('img');image.alt='';image.loading='lazy';image.src=track.coverUrl;image.addEventListener('error',()=>image.remove(),{once:true});art.append(image);}
    else if(track?.art)art.append(window.CelesteAlbums.artwork(track));
    art.title='使用曲库中的专辑封面';return art;
  }
  function browse(){
    const query=$('search').value.trim().toLocaleLowerCase(),sort=$('artists-sort').value;
    const visible=groups.filter(group=>!query||group.name.toLocaleLowerCase().includes(query)).sort((a,b)=>{
      if(sort==='year'&&(!a.yearTo||!b.yearTo)){if(!a.yearTo!==!b.yearTo)return a.yearTo?-1:1;}
      const value=sort==='name'?compare.compare(a.name,b.name):Number(a[sort==='year'?'yearTo':sort]||0)-Number(b[sort==='year'?'yearTo':sort]||0);
      return (value||compare.compare(a.name,b.name))*(ascending?1:-1);
    });
    $('artists-title').replaceChildren(document.createTextNode(kind==='artists'?'艺术家':'专辑艺人'),node('span','。'));
    $('artists-count').textContent=`${query?visible.length+' 位匹配 · ':''}共 ${groups.length} 位 · ${shell.getLibrary().length} 首歌曲`;
    $('artists-grid').replaceChildren();$('artists-empty').hidden=visible.length>0;$('artists-more').hidden=visible.length<=limit;
    visible.slice(0,limit).forEach(group=>{const card=node('button',undefined,'artist-card');card.dataset.artist=group.name;card.append(portrait(group),node('strong',group.name),node('small',`${group.songCount} 首歌曲 · ${group.albumCount} 张专辑`));card.addEventListener('click',()=>open(group));$('artists-grid').append(card);});
  }
  function stats(target,data,loading=false){
    const years=data.yearFrom?(data.yearFrom===data.yearTo?String(data.yearFrom):`${data.yearFrom}–${data.yearTo}`):'未知';
    const values=[['歌曲',data.songCount+' 首'],['专辑',data.albumCount+' 张'],['时长',duration(data.duration)],['年份',years,'按库内歌曲的发行年份范围统计'],['来源',[data.localCount?'本地 '+data.localCount+' 首':'',data.remoteCount?'远程 '+data.remoteCount+' 首':''].filter(Boolean).join(' · ')||'未知'],['流派',data.genres?.join(' / ')||'未知']];
    target.replaceChildren();values.forEach(([label,value,hint])=>{const cell=node('div'),term=node('dt',label),description=node('dd',value);if(hint){cell.title=hint;}cell.append(term,description);target.append(cell);});
  }
  async function overview(category,name,tracks,target,note,token){
    const basic=base(tracks),key=category+'\n'+name;
    stats(target,basic,live);note.textContent=live?'正在读取文件编码信息…':'演示曲库 · 未连接真实编码信息';
    if(!live)return;
    try{let data=cache.get(key);if(!data){data=await window.CelesteTransport.request('collectionOverview',{category,name});if(!data)throw new Error('暂未取得统计信息');if(token!==generation)return;cache.set(key,data);}if(token!==generation)return;stats(target,data);note.textContent='年份按库内歌曲的发行年份统计。';}
    catch(error){if(token!==generation)return;stats(target,basic);note.textContent='编码统计暂不可用：'+error.message;}
  }
  function play(group){if(live)shell.playTracks(group.tracks.map(track=>track.id));else shell.playDemo(Number(group.tracks[0].id));}
  function detailCompare(a,b){const key=$('artist-detail-sort').value;const value=['year','duration','track','trackCount'].includes(key)?Number(a[key]||0)-Number(b[key]||0):compare.compare(a[key]||a.title||'',b[key]||b.title||'');return (value||compare.compare(a.title||'',b.title||''))*(detailAscending?1:-1);}
  function detailOptions(next){
    detailPreferences[tab]={key:$('artist-detail-sort').value,ascending:detailAscending};tab=next;
    const options=tab==='songs'?[['title','名称'],['album','专辑'],['year','年份'],['duration','时长'],['track','音轨号']]:[['title','专辑名称'],['artist','艺术家'],['year','发行年份'],['duration','总时长'],['trackCount','曲目数']];
    $('artist-detail-sort').replaceChildren();options.forEach(([value,label])=>{const option=node('option',label);option.value=value;$('artist-detail-sort').append(option);});
    $('artist-detail-sort').value=detailPreferences[tab].key;detailAscending=detailPreferences[tab].ascending;$('artist-detail-direction').textContent=detailAscending?'升序 ↑':'降序 ↓';$('artist-detail-sort').dispatchEvent(new Event('change'));content();
  }
  $('artist-detail-sort').addEventListener('change',content);
  $('artist-detail-direction').addEventListener('click',()=>{detailAscending=!detailAscending;$('artist-detail-direction').textContent=detailAscending?'升序 ↑':'降序 ↓';content();});
  function content(){
    $('artist-songs').hidden=tab!=='songs';$('artist-albums').hidden=tab!=='albums';document.querySelectorAll('[data-artist-tab]').forEach(button=>button.setAttribute('aria-pressed',String(button.dataset.artistTab===tab)));
    if(!opened)return;
    $('artist-songs').replaceChildren();[...opened.tracks].sort(detailCompare).forEach((track,index)=>{
      const row=node('button',undefined,'album-track');row.dataset.trackId=track.id;const copy=node('span');copy.append(node('strong',track.title),node('small',track.album));row.append(node('span',index+1,'track-number'),copy,node('time',time(track.duration)));
      const start=()=>live?shell.playTrack(track.id):shell.playDemo(Number(track.id));row.addEventListener('dblclick',start);row.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();start();}});$('artist-songs').append(row);
    });
    const ids=new Set(opened.tracks.map(track=>track.id));$('artist-albums').replaceChildren();window.CelesteAlbums.getAlbums().filter(album=>album.trackIds.some(id=>ids.has(id))).sort(detailCompare).forEach(album=>{
      const card=node('button',undefined,'album-open');card.append(window.CelesteAlbums.artwork(album),node('strong',album.title,'album-card-title'),node('small',album.trackCount+' 首歌曲','album-card-meta'));card.addEventListener('click',()=>{const saved=opened,category=kind;window.CelesteAlbums.openById(album.id,[{label:category==='artists'?'艺术家':'专辑艺人',action:()=>{opened=null;shell.navigate(category);show();}},{label:saved.name,action:()=>{shell.navigate(category);open(saved);}}]);});$('artist-albums').append(card);
    });updatePlaying();
  }
  function open(group){
    opened=group;$('artists-browser').hidden=true;$('artist-detail').hidden=false;$('artist-name').textContent=group.name;$('artist-kind').textContent=kind==='artists'?'ARTIST':'ALBUM ARTIST';$('artist-back').textContent=kind==='artists'?'‹ 所有艺术家':'‹ 所有专辑艺人';shell.setPath([{label:kind==='artists'?'艺术家':'专辑艺人',action:()=>{opened=null;show();}},{label:group.name,action:()=>open(group)}]);$('artist-portrait').replaceChildren(portrait(group));
    content();overview(kind,group.name,group.tracks,$('artist-stats'),$('artist-stats-note'),++generation);document.querySelector('.main').scrollTop=0;
  }
  function show(){if(opened)open(opened);else{shell.setPath([{label:kind==='artists'?'艺术家':'专辑艺人',action:()=>{opened=null;show();}}]);$('artists-browser').hidden=false;$('artist-detail').hidden=true;browse();}}
  function updatePlaying(){const state=shell.getState();document.querySelectorAll('#artist-songs .album-track').forEach(row=>row.setAttribute('aria-current',String(live?state.trackId===row.dataset.trackId:state.index===Number(row.dataset.trackId))));}
  $('artist-back').addEventListener('click',()=>{generation++;opened=null;show();});$('artist-play').addEventListener('click',()=>opened&&play(opened));
  document.querySelectorAll('[data-artist-tab]').forEach(button=>button.addEventListener('click',()=>detailOptions(button.dataset.artistTab)));
  $('artists-sort').addEventListener('change',browse);$('artists-direction').addEventListener('click',()=>{ascending=!ascending;$('artists-direction').textContent=ascending?'升序 ↑':'降序 ↓';browse();});$('artists-more').addEventListener('click',()=>{limit+=80;browse();});
  $('search').addEventListener('input',()=>{if(['artists','albumArtists'].includes(shell.getPage())){generation++;opened=null;limit=80;show();}});
  document.addEventListener('celeste:navigate',event=>{if(event.detail!=='albums')generation++;if(!['artists','albumArtists'].includes(event.detail))return;if(kind!==event.detail){opened=null;detailOptions('songs');}kind=event.detail;$('search').placeholder=kind==='artists'?'搜索艺术家':'搜索专辑艺人';rebuild();show();});
  document.addEventListener('celeste:library',()=>{generation++;cache.clear();if(['artists','albumArtists'].includes(shell.getPage())){rebuild();show();}});
  document.addEventListener('celeste:message',event=>{if(event.detail.kind==='celeste.state')requestAnimationFrame(updatePlaying);});
  const summary=node('dl',undefined,'collection-stats album-summary'),note=node('p',undefined,'setting-note');summary.id='album-summary';$('album-tracks').before(summary,note);
  document.addEventListener('celeste:album-open',event=>{overview('albums',event.detail.album.title,event.detail.tracks,summary,note,++generation);});
  window.CelesteArtists=Object.freeze({getGroups:()=>groups,openByName:(name,trail=[])=>{kind='artists';rebuild();const group=groups.find(item=>item.name===name);if(!group)return false;opened=group;shell.navigate('artists');if(trail.length)shell.setPath([...trail,{label:group.name,action:()=>open(group)}]);return true;}});
})();
