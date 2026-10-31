(() => {
  'use strict';
  const shell=window.CelesteShell,live=typeof window.chrome?.webview?.postMessage==='function';
  const node=(tag,text,cls)=>{const item=document.createElement(tag);if(text!==undefined)item.textContent=text;if(cls)item.className=cls;return item;};
  const page=node('section',undefined,'page folders-page');page.id='folders-page';page.hidden=true;
  const heading=node('div',undefined,'folders-heading');heading.append(node('p','YOUR COLLECTION','eyebrow'),node('h1','文件夹'));
  const count=node('p','','songs-count');heading.append(count);page.append(heading);
  const layout=node('div',undefined,'folder-layout'),sidebar=node('aside',undefined,'folder-sidebar'),main=node('div',undefined,'folder-detail');layout.append(sidebar,main);page.append(layout);
  const treeHeader=node('div',undefined,'folder-tree-header');treeHeader.append(node('h2','媒体库'));const refresh=node('button','刷新','text-button');refresh.id='folder-refresh';treeHeader.append(refresh);sidebar.append(treeHeader);
  const add=node('button','＋ 添加文件夹','secondary');add.id='folder-add';sidebar.append(add);
  const tree=node('div',undefined,'folder-tree');tree.id='folder-tree';tree.role='tree';tree.setAttribute('aria-label','媒体库目录');sidebar.append(tree);
  const detailHeader=node('div',undefined,'folder-detail-header'),copy=node('div'),title=node('h2','选择文件夹查看歌曲'),summary=node('p','','setting-note');title.id='folder-detail-title';copy.append(title,summary);detailHeader.append(copy);
  const play=node('button','播放该文件夹','primary'),queue=node('button','添加至播放队列','secondary');play.id='folder-play';queue.id='folder-queue';play.disabled=queue.disabled=true;detailHeader.append(play,queue);main.append(detailHeader);
  const status=node('p','选择左侧文件夹，在这里查看歌曲。','setting-note');status.id='folder-status';status.role='status';main.append(status);
  const list=node('div',undefined,'folder-songs');list.id='folder-songs';main.append(list);const more=node('button','加载更多','secondary');more.hidden=true;main.append(more);
  document.querySelector('.main').append(page);
  let active=false,epoch=0,selectionEpoch=0,current=null,tracks=[],limit=200,roots=[],loading=false,treeSelection='';
  const expanded=new Set(),cache=new Map(),pending=new Map();
  const allDemo=()=>shell.getLibrary().map((track,i)=>({...track,id:String(i)}));
  async function request(operation,payload={}){if(live)return window.CelesteTransport.request('folders',{operation,...payload});if(operation==='roots')return{nodes:[{id:'demo-root',name:'D:\\Music',path:'D:\\Music',folder:true}],total:allDemo().length,rootCount:1};if(operation==='children')return{nodes:payload.nodeId==='demo-root'?[{id:'demo-rock',name:'Rock',path:'D:\\Music\\Rock',folder:true},{id:'demo-file',name:'Sample.flac',path:'D:\\Music\\Sample.flac',folder:false}]:[]};if(operation==='songs')return{nodeId:payload.nodeId,path:payload.nodeId==='demo-root'?'D:\\Music':'D:\\Music\\Rock',tracks:allDemo()};throw new Error('请在桌面播放器中使用此功能。');}
  const visibleNodes=()=>{const output=[];function walk(items,depth){for(const item of items){output.push({item,depth});if(item.folder&&expanded.has(item.id))walk(cache.get(item.id)||[],depth+1);}}walk(roots,0);return output;};
  function renderTree(focusId){
    tree.replaceChildren();
    if(!roots.length){tree.append(node('p','请选择文件夹','setting-note'));return;}
    visibleNodes().forEach(({item,depth})=>{
      const row=node('div',undefined,'folder-node');row.dataset.nodeId=item.id;row.role='treeitem';row.tabIndex=0;row.setAttribute('aria-level',String(depth+1));row.setAttribute('aria-selected',String(treeSelection===item.id));row.title=item.path;row.style.paddingLeft=(depth*16+4)+'px';
      if(item.folder){
        row.setAttribute('aria-expanded',String(expanded.has(item.id)));
        // Chevron is drawn as SVG like every other icon in this shell. It used to be two Unicode
        // glyphs (a down-arrowhead and a right-angle-quote) whose shapes sit low on the baseline
        // and change with the font — that is what made the arrow look off-centre.
        // Text glyphs are never icons; see collab/PROJECT-BRIEF.md.
        const arrow=node('button',undefined,'folder-chevron');
        const chevronSvg=document.createElementNS('http://www.w3.org/2000/svg','svg'),chevronUse=document.createElementNS('http://www.w3.org/2000/svg','use');
        chevronUse.setAttribute('href','#i-chevron');chevronSvg.append(chevronUse);arrow.append(chevronSvg);
        if(expanded.has(item.id))arrow.classList.add('is-open');
        arrow.tabIndex=-1;arrow.setAttribute('aria-label',(expanded.has(item.id)?'折叠 ':'展开 ')+item.name);arrow.addEventListener('click',event=>{event.stopPropagation();toggle(item,true);});row.append(arrow);
      }else row.append(node('span','','folder-chevron'));
      const icon=node('span',undefined,'folder-node-icon');icon.setAttribute('aria-hidden','true');const svg=document.createElementNS('http://www.w3.org/2000/svg','svg'),use=document.createElementNS('http://www.w3.org/2000/svg','use');use.setAttribute('href',item.folder?'#i-folder':'#i-note');svg.append(use);icon.append(svg);row.append(icon,node('span',item.name,'folder-node-name'));
      row.addEventListener('click',()=>{if(item.folder)toggle(item,false);else select(item);});row.addEventListener('dblclick',event=>{event.preventDefault();if(item.folder)select(item);else action('play',item);});
      row.addEventListener('keydown',event=>{const rows=[...tree.querySelectorAll('[role=treeitem]')],i=rows.indexOf(row);if(event.key==='ArrowDown'||event.key==='ArrowUp'){event.preventDefault();rows[Math.max(0,Math.min(rows.length-1,i+(event.key==='ArrowDown'?1:-1)))]?.focus();}else if(event.key==='ArrowRight'&&item.folder){event.preventDefault();if(!expanded.has(item.id))toggle(item,true);else rows[i+1]?.focus();}else if(event.key==='ArrowLeft'){event.preventDefault();if(item.folder&&expanded.has(item.id))toggle(item,false);else{const parent=rows.slice(0,i).reverse().find(other=>Number(other.getAttribute('aria-level'))<depth+1);parent?.focus();}}else if(event.key==='Enter'){event.preventDefault();item.folder?select(item):action('play',item);}else if(event.key===' '&&item.folder){event.preventDefault();toggle(item,true);}});
      row.addEventListener('contextmenu',async event=>{event.preventDefault();if(item.folder)window.CelesteTrackMenu?.folder(event,item,action);else{await select(item);const song=list.querySelector('.folder-track');if(song)window.CelesteTrackMenu?.open(song,event.clientX,event.clientY);}});
      tree.append(row);
    });if(focusId)[...tree.children].find(row=>row.dataset.nodeId===focusId)?.focus({preventScroll:true});
  }
  async function toggle(item,details){
    treeSelection=item.id;
    if(expanded.has(item.id)){expanded.delete(item.id);renderTree(item.id);if(details)select(item);return;}
    expanded.add(item.id);renderTree(item.id);if(details)select(item);
    if(cache.has(item.id))return;const generation=epoch;
    if(pending.has(item.id))return;
    const work=request('children',{nodeId:item.id});pending.set(item.id,work);
    try{const result=await work;if(generation!==epoch||!active)return;cache.set(item.id,result.nodes);renderTree(item.id);}catch(error){if(generation===epoch){expanded.delete(item.id);renderTree(item.id);status.textContent=error.message;}}finally{if(pending.get(item.id)===work)pending.delete(item.id);}
  }
  function renderSongs(){
    const query=document.getElementById('search').value.trim().toLocaleLowerCase(),filtered=tracks.filter(track=>!query||[track.title,track.songTitle,track.artist,track.album].some(value=>String(value||'').toLocaleLowerCase().includes(query)));
    list.replaceChildren();filtered.slice(0,limit).forEach((track,i)=>{const row=node('div',undefined,'album-track folder-track');row.dataset.trackId=track.id;row.tabIndex=0;row.role='button';row.setAttribute('aria-label','播放 '+track.title);const copy=node('span');copy.append(node('strong',track.title),node('small',[track.artist,track.album].filter(Boolean).join(' · ')));const seconds=Math.max(0,track.duration||0);row.append(node('span',String(i+1),'track-number'),copy,node('time',Math.floor(seconds/60)+':'+String(Math.floor(seconds%60)).padStart(2,'0')));const start=()=>live?window.CelesteTransport.request('trackAction',{operation:'play',queue:false,trackIds:[track.id]}).catch(error=>shell.notify(error.message)):shell.playDemo(Number(track.id));row.addEventListener('dblclick',start);row.addEventListener('keydown',event=>{if(event.key==='Enter'){event.preventDefault();start();}});list.append(row);});
    summary.textContent=tracks.length+' 首歌曲'+(query?' · '+filtered.length+' 首匹配':'');more.hidden=filtered.length<=limit;
    status.textContent=loading?'正在读取歌曲…':!current?'选择左侧文件夹，在这里查看歌曲。':!tracks.length?'无歌曲':!filtered.length?'没有找到匹配的歌曲。':'';play.disabled=queue.disabled=loading||!tracks.length;playing();
  }
  async function select(item){const generation=++selectionEpoch;current=item;treeSelection=item.id;tracks=[];limit=200;loading=true;title.textContent=item.path;renderTree();renderSongs();try{const result=await request('songs',{nodeId:item.id});if(generation!==selectionEpoch||!active)return;tracks=result.tracks;loading=false;renderSongs();}catch(error){if(generation===selectionEpoch){loading=false;status.textContent=error.message;}}}
  async function action(operation,item=current){if(!item)return;try{await request(operation,{nodeId:item.id});shell.notify(operation==='queue'?'已添加至播放队列':'已开始播放');document.dispatchEvent(new CustomEvent('celeste:tracksChanged',{detail:{source:'folders'}}));}catch(error){shell.notify(error.message);}}
  async function load(operation='roots'){const generation=++epoch;selectionEpoch++;expanded.clear();cache.clear();pending.clear();current=null;treeSelection='';tracks=[];loading=false;title.textContent='选择文件夹查看歌曲';tree.replaceChildren(node('p','正在读取目录…','setting-note'));renderSongs();try{const result=await request(operation);if(generation!==epoch||!active)return;roots=result.nodes;count.textContent=`${result.rootCount||roots.length} 个来源目录 · ${result.total} 首歌曲`;renderTree();}catch(error){if(generation===epoch)status.textContent=error.message;}}
  function playing(){const current=shell.getState();list.querySelectorAll('[data-track-id]').forEach(row=>row.setAttribute('aria-current',String(live?row.dataset.trackId===current.trackId:Number(row.dataset.trackId)===current.index)));}
  refresh.addEventListener('click',()=>load());add.addEventListener('click',()=>load('add'));play.addEventListener('click',()=>action('play'));queue.addEventListener('click',()=>action('queue'));more.addEventListener('click',()=>{limit+=200;renderSongs();});
  document.getElementById('search').addEventListener('input',()=>{if(active){limit=200;renderSongs();}});
  document.addEventListener('celeste:navigate',event=>{active=event.detail==='folders';page.hidden=!active;if(active){document.getElementById('browse-page').hidden=true;shell.setPath([{label:'文件夹',action:()=>load()}]);document.getElementById('search').placeholder='搜索当前文件夹歌曲';/* 目录是异步读的：外壳那次淡入跑在空页面上，等数据回来再补一次内容淡入，否则内容会「蹦」出来，看着像没有动画。 */load().then(()=>{if(active)window.CelesteMotion?.page(page,'forward');});}else{epoch++;selectionEpoch++;}});
  document.addEventListener('celeste:library',()=>{if(active)load();});document.addEventListener('celeste:message',event=>{if(active&&event.detail.kind==='celeste.state')playing();});
  document.addEventListener('celeste:tracksChanged',event=>{if(active&&current&&event.detail?.source!=='folders')select(current);});
})();
