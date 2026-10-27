const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url');
let chromium;try{({chromium}=require('playwright'));}catch{({chromium}=require('playwright-core'));}
(async()=>{const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});try{
 const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{window.commands=[];window.slow=false;window.chrome||={};window.chrome.webview={addEventListener:(_,cb)=>window.deliver=data=>cb({data}),postMessage:m=>{commands.push(m);if(!m.requestId)return;let data,delay=5;
  const file=(id,title)=>({id,title,artist:'Test artist',album:'Test album',duration:180});
  if(m.command==='folders'){
   if(['roots','add'].includes(m.operation))data={nodes:[{id:'root',name:'D:\\Music',path:'D:\\Music',folder:true},{id:'other',name:'E:\\Other',path:'E:\\Other',folder:true}],total:4031,rootCount:2};
   else if(m.operation==='children')data={nodes:m.nodeId==='root'?[{id:'rock',name:'Rock',path:'D:\\Music\\Rock',folder:true},{id:'empty',name:'Empty',path:'D:\\Music\\Empty',folder:true},{id:'file',name:'Root.flac',path:'D:\\Music\\Root.flac',folder:false}]:m.nodeId==='rock'?[{id:'subfile',name:'Disc.flac',path:'D:\\Music\\Rock\\Disc.flac',folder:false}]:[]};
   else{data={nodeId:m.nodeId,path:m.nodeId==='other'?'E:\\Other':'D:\\Music',tracks:m.nodeId==='empty'?[]:m.nodeId==='other'?[file('c','Other.flac')]:m.nodeId==='file'?[file('a','Root.flac')]:[file('a','Root.flac'),file('b','Disc.flac')]};if(window.slow&&m.nodeId==='root')delay=200;}
  }
  if(m.command==='trackAction')data=m.operation==='get'?{favorite:false,canDelete:true}:{message:'完成'};
  setTimeout(()=>deliver({kind:'celeste.reply',version:1,requestId:m.requestId,ok:true,data}),delay);
 }};});
 await page.goto(pathToFileURL(path.join(__dirname,'../outputs/webview2-shell/index.html')).href);
 await page.evaluate(()=>CelesteShell.navigate('folders'));await page.waitForSelector('#folder-tree [role=treeitem]');
 assert.equal(await page.locator('#browse-page').isVisible(),false);assert.equal(await page.locator('#folder-tree [role=treeitem]').count(),2);assert.match(await page.locator('#folders-page .songs-count').textContent(),/4031/);assert.equal(await page.locator('.folder-track').count(),0);
 const arrow=id=>page.locator(`#folder-tree [data-node-id="${id}"] .folder-chevron`);
 await arrow('root').click();await page.waitForSelector('[data-node-id="rock"]');await page.waitForSelector('.folder-track');assert.equal(await page.locator('.folder-track').count(),2);assert.equal(await page.locator('[data-node-id="rock"]').getAttribute('aria-level'),'2');
 await arrow('rock').click();await page.waitForSelector('[data-node-id="subfile"]');assert.equal(await page.locator('[data-node-id="subfile"]').getAttribute('aria-level'),'3');
 await arrow('root').click();assert.equal(await page.locator('#folder-tree [role=treeitem]').count(),2);assert.equal(await page.locator('.folder-track').count(),2,'collapse should not discard detail songs');
 const reads=await page.evaluate(()=>commands.filter(m=>m.command==='folders'&&m.operation==='children').length);await arrow('root').click();assert.equal(await page.locator('[data-node-id="subfile"]').count(),1);assert.equal(await page.evaluate(()=>commands.filter(m=>m.command==='folders'&&m.operation==='children').length),reads,'cached expansion does not reread folders');
 await page.locator('[data-node-id="empty"]').press('Enter');await page.waitForFunction(()=>document.querySelector('#folder-status').textContent==='无歌曲');assert.equal(await page.locator('#folder-play').isDisabled(),true);
 await page.evaluate(()=>window.slow=true);await page.locator('[data-node-id="root"]').press('Enter');await page.locator('[data-node-id="other"]').press('Enter');await page.waitForTimeout(300);assert.equal(await page.locator('.folder-track strong').textContent(),'Other.flac','late previous selection must not overwrite newer selection');
 await page.evaluate(()=>window.slow=false);await page.locator('[data-node-id="root"]').press('Enter');await page.waitForFunction(()=>document.querySelectorAll('.folder-track').length===2);await page.locator('#search').fill('Disc');assert.equal(await page.locator('.folder-track').count(),1);await page.locator('#search').fill('');
 await page.locator('.folder-track').nth(1).click({button:'right'});await page.locator('#track-context-menu').waitFor({state:'visible'});await page.locator('#track-context-menu button').getByText('复制歌名与艺术家',{exact:true}).click();assert.deepEqual(await page.evaluate(()=>commands.filter(m=>m.command==='trackAction'&&m.operation==='copy').at(-1).trackIds),['b']);
 await page.locator('[data-node-id="rock"]').click({button:'right'});await page.locator('#track-context-menu button').getByText('添加至播放队列',{exact:true}).click();await page.waitForTimeout(30);assert.equal(await page.evaluate(()=>commands.filter(m=>m.command==='folders'&&m.operation==='queue').at(-1).nodeId),'rock');
 await page.locator('[data-node-id="file"]').press('Enter');await page.waitForTimeout(30);assert.equal(await page.evaluate(()=>commands.filter(m=>m.command==='folders'&&m.operation==='play').at(-1).nodeId),'file');
 await page.locator('#folder-refresh').click();await page.waitForSelector('[data-node-id="root"]');assert.equal(await page.locator('#folder-tree [role=treeitem]').count(),2);assert.equal(await page.locator('.folder-track').count(),0);
 await arrow('root').click();await page.waitForSelector('[data-node-id="rock"]');await page.waitForSelector('.folder-track');await arrow('rock').click();await page.waitForSelector('[data-node-id="subfile"]');await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-folder-tree.png')});
 for(const width of [1440,1024,760]){await page.setViewportSize({width,height:960});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));const left=await page.locator('.folder-sidebar').boundingBox(),right=await page.locator('.folder-detail').boundingBox();assert.ok(left.x+left.width<=right.x);}
 await page.evaluate(()=>CelesteShell.navigate('songs'));assert.equal(await page.locator('#folders-page').isVisible(),false);assert.deepEqual(errors,[]);
 console.log('PASS: native two-pane tree, roots/full paths, 4031 count preserved, lazy/cached expand/collapse, nested audio files, recursive details, empty folders, selection races, search, song/folder menus, file playback, refresh and responsive geometry.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
