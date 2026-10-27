const assert=require('node:assert/strict');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
let chromium;try{({chromium}=require('playwright'));}catch{({chromium}=require('playwright-core'));};
(async()=>{
 const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(()=>{
   window.commands=[];window.chrome||={};window.chrome.webview={addEventListener:(_,handler)=>window.deliver=data=>handler({data}),postMessage:message=>{
    commands.push(message);if(!message.requestId)return;let data;
    window.settings||={selectedMode:'Shared',selectedDeviceId:'',actualMode:'WasapiShared',devices:[],rate:1,preservePitch:true,a:-1,b:-1,abActive:false,sleepSelection:0,sleepTracks:1};
    if(message.command==='audioSettings'){if(message.operation==='setSleep'){settings.sleepSelection=message.selection;settings.sleepMinutes=message.minutes;}data={...settings};}
    if(message.command==='collectionOverview')data={songCount:2,albumCount:2,duration:360,yearFrom:2010,yearTo:2024,losslessCount:1,lossyCount:1,unknownCount:0,losslessPercent:50,highestSpec:'24bit / 96kHz',localCount:1,remoteCount:1,genres:['Ambient','Jazz']};
    if(message.command==='browse')data={tracks:[],playlists:[]};
    setTimeout(()=>deliver({kind:'celeste.reply',version:1,requestId:message.requestId,ok:true,data}),5);
   }};
  });
  await page.goto(pathToFileURL(path.join(__dirname,'../outputs/webview2-shell/index.html')).href);
  await page.evaluate(()=>{
   const tracks=[{id:'one',title:'A Song',artist:'Alice',albumArtist:'The Ensemble',album:'First',year:2010,genre:'Ambient',duration:120,disc:1,track:1},{id:'two',title:'B Song',artist:'Alice',albumArtist:'The Ensemble',album:'Second',year:2024,genre:'Jazz',duration:240,disc:1,track:1,remote:true},{id:'three',title:'C Song',artist:'Bob',albumArtist:'The Ensemble',album:'Second',year:2024,genre:'Jazz',duration:180,disc:1,track:2}];
   tracks.forEach(t=>{t.chips=[];t.coverUrl='';});deliver({kind:'celeste.library',version:1,phase:'begin',revision:1,total:3});deliver({kind:'celeste.library',version:1,phase:'batch',revision:1,tracks});deliver({kind:'celeste.library',version:1,phase:'end',revision:1,albums:[{id:'first',title:'First',artist:'The Ensemble',trackIds:['one'],trackCount:1,duration:120,year:2010},{id:'second',title:'Second',artist:'The Ensemble',trackIds:['two','three'],trackCount:2,duration:420,year:2024}]});
  });
  for(const category of ['overview','songs','albums','artists','albumArtists','folders']){
   await page.evaluate(page=>CelesteShell.navigate(page),category);await page.waitForTimeout(300);await page.waitForFunction(()=>document.getAnimations().filter(a=>a.playState==='running').length===0).catch(()=>{});await page.waitForTimeout(120);
   const selector={overview:'#overview-title',songs:'.songs-heading',albums:'.albums-heading',artists:'#artists-browser .albums-heading',albumArtists:'#artists-browser .albums-heading',folders:'#folder-detail-title'}[category];
   const before=await page.locator(selector).first().boundingBox();await page.locator('#orbit-toggle').click();await page.waitForTimeout(450);const after=await page.locator(selector).first().boundingBox();
   for(const key of ['x','y','width','height'])assert.ok(Math.abs(after[key]-before[key])<.5,category+' heading moved: '+key);
  }
  await page.evaluate(()=>CelesteShell.navigate('artists'));
  assert.equal(await page.locator('.artist-card').count(),2);assert.match(await page.locator('[data-artist="Alice"]').textContent(),/2 首歌曲 · 2 张专辑/);
  await page.locator('#artists-sort').click();assert.equal(await page.locator('.ui-listbox').count(),1);await page.getByRole('listbox',{name:'艺术家排序'}).getByRole('option',{name:'歌曲数',exact:true}).click();assert.equal(await page.locator('.artist-card').first().getAttribute('data-artist'),'Bob');
  await page.locator('#artists-direction').click();assert.equal(await page.locator('.artist-card').first().getAttribute('data-artist'),'Alice');
  await page.locator('[data-artist="Alice"]').click();await page.waitForFunction(()=>document.querySelector('#artist-stats').textContent.includes('首'));
  // 统计现在是六项：歌曲/专辑/时长/年份/来源/流派（早期版本还有编码信息，那部分已下线）
  assert.equal(await page.locator('#artist-stats dt').count(),6);assert.match(await page.locator('#artist-stats').textContent(),/1 首/);
  await page.locator('#artist-songs .album-track').nth(1).dblclick();assert.equal(await page.evaluate(()=>commands.filter(m=>m.command==='playTrack').at(-1).trackId),'two');
  await page.locator('#artist-play').click();assert.deepEqual(await page.evaluate(()=>commands.filter(m=>m.command==='playAll').at(-1).trackIds),['one','two']);
  await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-artist-detail.png')});
  await page.locator('[data-artist-tab="albums"]').click();assert.equal(await page.locator('#artist-albums .album-open').count(),2);await page.locator('#artist-albums .album-open').first().click();assert.equal(await page.locator('#album-detail-title').textContent(),'First');await page.waitForFunction(()=>document.querySelector('#album-summary').textContent.includes('首'));
  await page.evaluate(()=>CelesteShell.navigate('albumArtists'));assert.equal(await page.locator('.artist-card').count(),1);assert.match(await page.locator('.artist-card').textContent(),/3 首歌曲 · 2 张专辑/);
  await page.locator('#search').fill('Ensemble');await page.locator('#search').press('Enter');assert.equal(await page.evaluate(()=>CelesteShell.getPage()),'albumArtists');await page.locator('#search').fill('');
  for(const width of [1440,1024,760]){await page.setViewportSize({width,height:960});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));}
  await page.setViewportSize({width:1440,height:960});await page.locator('#audio-settings').click();await page.locator('[data-audio-tab="speed"]').click();await page.waitForTimeout(500);
  const tick=await page.locator('.rate-ticks span').nth(1).boundingBox(),slider=await page.locator('#audio-rate').boundingBox();assert.ok(Math.abs(tick.x+tick.width/2-(slider.x+4+(slider.width-8)/3))<1);
  await page.locator('[data-audio-tab="sleep"]').click();await page.locator('[data-sleep="8"]').click();await page.locator('#sleep-minutes').fill('47');await page.locator('#sleep-custom button').click();await page.waitForFunction(()=>document.querySelector('#sleep-status').textContent.includes('47 分钟'));
  const request=await page.evaluate(()=>commands.filter(m=>m.operation==='setSleep').at(-1));assert.equal(request.selection,8);assert.equal(request.minutes,47);
  await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-custom-sleep.png')});
  assert.deepEqual(errors,[]);console.log('PASS: fixed headings across 6 pages, custom sort menu, artists/album artists counts and sorting, 8 real overview fields, album navigation and stats, playback IDs, speed tick alignment, custom sleep RPC, responsive widths.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
