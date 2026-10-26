const assert=require('node:assert/strict'),path=require('node:path'),{pathToFileURL}=require('node:url'),{chromium}=require('playwright');
(async()=>{const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});try{
 const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.addInitScript(()=>{window.commands=[];window.rejectQueue=false;window.chrome||={};window.chrome.webview={addEventListener:(_,callback)=>window.deliver=data=>callback({data}),postMessage:m=>{commands.push(m);if(!m.requestId)return;let data,ok=true,error='';
  if(m.command==='browse')data={tracks:[{id:'a',folder:'Music/Rock',favorite:true,rating:5,playCount:8,lastPlayed:'2026-10-25'},{id:'b',folder:'Music/Jazz',favorite:false,rating:3,playCount:2,lastPlayed:'2026-10-26'}],playlists:[{name:'Weekend',trackIds:['a','b'],missingCount:0,coverUrl:'https://celeste-cover.internal/playlist/Weekend'}]};
  if(m.command==='queue')data={revision:3,currentIndex:0,total:3,offset:0,tracks:[{id:'a',index:0,title:'Alpha',artist:'Alice',duration:180},{id:'b',index:1,title:'Beta',artist:'Bob',duration:240},{id:'a',index:2,title:'Alpha repeated',artist:'Alice',duration:180}]};
  if(m.command==='folders')data=m.operation==='roots'?{nodes:[{id:'root',name:'D:\\Music',path:'D:\\Music',folder:true}],total:2,rootCount:1}:m.operation==='children'?{nodes:[{id:'rock',name:'Rock',path:'D:\\Music\\Rock',folder:true}]}:{path:'D:\\Music',tracks:[{id:'a',title:'Alpha.flac',artist:'Alice',album:'First',duration:180},{id:'b',title:'Beta.flac',artist:'Bob',album:'Second',duration:240}]};
  if(m.command==='trackAction'){data=m.operation==='get'?{favorite:false,canDelete:true}:{message:'完成'};if(m.queue&&window.rejectQueue){ok=false;error='播放队列已变化，请重新打开菜单。';}}
  if(m.command==='collectionOverview')data={songCount:1,albumCount:1,duration:180,genres:[]};
  setTimeout(()=>deliver({kind:'celeste.reply',version:1,requestId:m.requestId,ok,data,error}),5);
 }};});
 await page.route('https://celeste-cover.internal/**',route=>route.fulfill({status:200,contentType:'image/svg+xml',body:'<svg xmlns="http://www.w3.org/2000/svg" width="100" height="100"><rect width="100" height="100" fill="pink"/></svg>'}));
 await page.goto(pathToFileURL(path.join(__dirname,'../outputs/webview2-shell/index.html')).href);
 await page.evaluate(()=>{deliver({kind:'celeste.library',version:1,phase:'begin',revision:1,total:2});deliver({kind:'celeste.library',version:1,phase:'batch',revision:1,tracks:[{id:'a',title:'Alpha',artist:'Alice',albumArtist:'Alice',album:'First',duration:180,year:2024,chips:[]},{id:'b',title:'Beta',artist:'Bob',albumArtist:'Bob',album:'Second',duration:240,year:2020,chips:[]}]});deliver({kind:'celeste.library',version:1,phase:'end',revision:1,albums:[{id:'first',title:'First',artist:'Alice',trackIds:['a'],trackCount:1,duration:180},{id:'second',title:'Second',artist:'Bob',trackIds:['b'],trackCount:1,duration:240}]});});
 const go=async name=>{await page.evaluate(name=>CelesteShell.navigate(name),name);await page.waitForTimeout(100);};
 const open=async row=>{await row.click({button:'right',position:{x:110,y:15}});await page.locator('#track-context-menu').waitFor({state:'visible'});};
 const choose=async label=>{await page.locator('#track-context-menu button').getByText(label,{exact:true}).click();await page.waitForTimeout(70);};
 const last=()=>page.evaluate(()=>commands.filter(m=>m.command==='trackAction'&&m.operation!=='get').at(-1));
 for(const category of ['songs','albums','artists','albumArtists','folders','favorites','ratings','recent','mostPlayed','playlists','queue']){
  await go(category);
  if(category==='albums')await page.locator('.album-open').first().click();
  if(['artists','albumArtists'].includes(category))await page.locator('.artist-card').first().click();
  if(category==='folders'){await page.locator('#folder-tree [data-node-id="root"] .folder-chevron').click();await page.waitForSelector('.folder-track');}
  if(category==='playlists'){assert.equal(await page.locator('#playlist-import').isVisible(),true);assert.equal(await page.locator('.browse-card img').count(),1);await page.locator('.browse-card').click();for(const id of ['playlist-queue','playlist-copy','playlist-export','playlist-cover'])assert.equal(await page.locator('#'+id).isVisible(),true);assert.equal(await page.locator('#browse-play').textContent(),'播放该列表');}
  const row=page.locator('.song-row:visible,.album-track:visible,.web-queue-row:visible').first();
  const id=await row.getAttribute('data-track-id');const before=await page.evaluate(()=>commands.filter(m=>['playTrack','playAll'].includes(m.command)).length);
  await open(row);assert.equal(await page.evaluate(()=>commands.filter(m=>['playTrack','playAll'].includes(m.command)).length),before,'right click must not start playback');
  await choose('复制歌名与艺术家');const sent=await last();assert.equal(sent.operation,'copy');if(category==='queue'){assert.equal(sent.revision,3);assert.deepEqual(sent.entries,[{index:0,trackId:'a'}]);}else assert.deepEqual(sent.trackIds,[id]);
 }
 await go('songs');await open(page.locator('.song-row').first());await choose('多选');await page.locator('.song-row').nth(1).click();assert.match(await page.locator('.track-selection-bar').textContent(),/2 首/);await page.locator('.track-selection-bar button').first().click();await choose('添加至播放队列');assert.deepEqual((await last()).trackIds,['a','b']);await page.keyboard.press('Escape');
 await go('songs');await page.locator('.song-row').nth(1).focus();await page.keyboard.press('Shift+F10');await page.locator('#track-context-menu').waitFor({state:'visible'});await choose('下一首播放');assert.deepEqual((await last()).trackIds,['b']);
 await go('queue');await open(page.locator('.browse-row').nth(2));await choose('置顶');assert.deepEqual((await last()).entries,[{index:2,trackId:'a'}]);
 await page.evaluate(()=>window.rejectQueue=true);await page.locator('.browse-row').nth(1).click({button:'right',position:{x:110,y:15}});await page.waitForTimeout(100);
 assert.equal(await page.locator('#track-context-menu').isVisible(),false);assert.match(await page.locator('#toast').textContent(),/队列已变化/);await page.evaluate(()=>window.rejectQueue=false);
 await go('playlists');await page.locator('.browse-card').click({button:'right'});await choose('设置封面（本地图片…）');assert.equal(await page.evaluate(()=>commands.filter(m=>m.command==='browse').at(-1).operation),'setCover');
 for(const width of [1440,1024,760]){await page.setViewportSize({width,height:960});await go('songs');await open(page.locator('.song-row').first());const box=await page.locator('#track-context-menu').boundingBox();assert.ok(box.x>=0&&box.x+box.width<=width&&box.y>=0&&box.y+box.height<=960);await page.keyboard.press('Escape');}
 await page.setViewportSize({width:1440,height:960});await go('playlists');await page.locator('.browse-card').click();await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-playlist-context.png')});
 assert.deepEqual(errors,[]);console.log('PASS: context menus on 11 categories, clicked ID, no accidental play, multi-select, keyboard, exact duplicate queue entry, stale revision, playlist cover/actions, responsive bounds.');
}finally{await browser.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
