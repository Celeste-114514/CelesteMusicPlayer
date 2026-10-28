/* Local-album host for the RhineLabUI 3D archive. Playback stays in Celeste. */
(() => {
  'use strict';
  const shell=window.CelesteShell, live=typeof window.chrome?.webview?.postMessage==='function';
  let renderer=null, root=null, selected=null, albums=[], generation=0, detail=false, scriptPromise;
  const make=(tag,cls,text)=>{const el=document.createElement(tag);el.className=cls;if(text)el.textContent=text;return el;};
  const button=(text,fn)=>{const el=make('button','rh-button',text);el.type='button';el.onclick=fn;return el;};
  function load(){return scriptPromise ||= new Promise((resolve,reject)=>{
    const s=document.createElement('script');s.src='rhine-renderer.js';s.onload=resolve;s.onerror=()=>{scriptPromise=null;s.remove();reject(Error('三维资源加载失败'));};document.head.append(s);
  });}
  function close(){generation++;renderer?.dispose();renderer=null;root?.remove();root=null;document.body.classList.remove('rhine-open');document.getElementById('rhine-launch')?.focus();}
  function showAlbum(album){
    if(!root||!album)return;selected=album;
    root.querySelector('.rh-number').textContent='NO. '+String(albums.indexOf(album)+1).padStart(3,'0');
    root.querySelector('.rh-title').textContent=album.title;
    root.querySelector('.rh-artist').textContent=album.artist||'未知艺术家';
    root.querySelector('.rh-meta').textContent=[album.year||'',`${album.trackCount} 首`,`${Math.floor((album.duration||0)/60)} 分钟`].filter(Boolean).join(' / ');
    const art=root.querySelector('.rh-art');art.replaceChildren(window.CelesteAlbums.artwork(album));
    const list=root.querySelector('.rh-tracks');list.replaceChildren();
    const tracks=new Map(shell.getLibrary().map((t,i)=>[live?t.id:String(i),t]));
    album.trackIds.forEach((id,i)=>{const t=tracks.get(id);if(!t)return;
      const b=button(`${String(i+1).padStart(2,'0')}  ${t.title}`,()=>live?shell.playTrack(id):shell.playDemo(Number(id)));b.className='rh-track';list.append(b);
    });
    root.querySelector('.rh-picker').value=album.id;
  }
  async function open(){
    if(root)return;
    albums=window.CelesteAlbums.getAlbums().slice();
    if(!albums.length){shell.notify('音乐库里还没有专辑，请先添加音乐。');return;}
    const token=++generation;detail=false;
    root=make('section','rhine-room');root.setAttribute('aria-label','莱茵专辑阵列');
    const head=make('header','rh-head');
    head.append(make('div','rh-brand','CELESTE / MUSIC ARCHIVE'),make('span','rh-edition','莱茵阵列 · 本地音乐库'),button('返回播放器',close));
    const canvas=make('div','rh-scene');
    const status=make('p','rh-status','正在载入三维档案模型…');status.setAttribute('role','status');canvas.append(status);
    const panel=make('aside','rh-panel');
    const number=make('p','rh-number'),title=make('h1','rh-title'),artist=make('p','rh-artist'),meta=make('p','rh-meta');
    const art=make('div','rh-art'),tracks=make('div','rh-tracks');
    const controls=make('div','rh-actions');
    const extract=button('抽取专辑',()=>{detail=!detail;renderer?.detail(detail);root.classList.toggle('rh-detail',detail);extract.textContent=detail?'归还阵列':'抽取专辑';});extract.disabled=true;
    controls.append(extract,button('播放整张',()=>{if(selected){if(live)shell.playTracks(selected.trackIds);else shell.playDemo(Number(selected.trackIds[0]));}}));
    panel.append(number,title,artist,meta,controls,art,tracks);
    const footer=make('footer','rh-footer');
    const label=make('label','rh-select-label','定位专辑 '),picker=make('select','rh-picker');picker.setAttribute('aria-label','定位专辑');
    albums.forEach(a=>{const o=make('option','',`${a.title} / ${a.artist}`);o.value=a.id;picker.append(o);});
    picker.onchange=()=>{const i=albums.findIndex(a=>a.id===picker.value);if(i>=0){renderer?.select(i);if(detail){detail=false;renderer?.detail(false);root.classList.remove('rh-detail');extract.textContent='抽取专辑';}}};label.append(picker);
    const quality=button('画质：原始',()=>{const high=quality.dataset.high!=='true';quality.dataset.high=String(high);quality.textContent='画质：'+(high?'原始':'性能');renderer?.quality(high);});quality.dataset.high='true';
    footer.append(label,make('span','rh-help','拖动浏览 / 滚轮切换 / Esc 返回'),quality);
    const credit=make('small','rh-credit','3D 场景与模型：LBEILC / RhineLabUI · MIT');
    root.append(head,canvas,panel,footer,credit);document.body.append(root);document.body.classList.add('rhine-open');
    showAlbum(albums[0]);head.querySelector('button').focus();
    try{
      await load();if(token!==generation)return;
      const instance=await window.CelesteRhineRenderer.create(canvas,albums,showAlbum);
      if(token!==generation){instance.dispose();return;}renderer=instance;status.remove();extract.disabled=false;
    }catch(error){if(token!==generation)return;status.textContent='三维场景未能启动，可以返回播放器后重试。';console.error(error);}
  }
  const launch=button('莱茵阵列',open);launch.id='rhine-launch';launch.title='以三维透明档案阵列浏览本地专辑';
  document.querySelector('.album-sort-actions').prepend(launch);
  document.addEventListener('keydown',e=>{if(e.key==='Escape'&&root){e.preventDefault();close();}});
  document.addEventListener('celeste:navigate',()=>{if(root)close();});
  document.addEventListener('celeste:library',()=>{if(root){close();shell.notify('音乐库已更新，请重新打开莱茵阵列。');}});
  document.addEventListener('visibilitychange',()=>{if(document.hidden&&root)close();});
  window.CelesteRhine=Object.freeze({open,close,getStats:()=>renderer?.stats()});
})();
