const assert=require('node:assert/strict');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
let chromium;try{({chromium}=require('playwright'));}catch{({chromium}=require('playwright-core'));};
(async()=>{
  const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true});
  try{
    const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[];
    page.on('pageerror',error=>errors.push(error.message));
    const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=','base64');
    await page.route('https://celeste-cover.internal/**',route=>route.fulfill({status:200,contentType:'image/png',body:png}));
    await page.addInitScript(()=>{window.commands=[];window.chrome||={};window.chrome.webview={postMessage:message=>commands.push(message),addEventListener:(_,handler)=>window.deliver=data=>handler({data})};});
    await page.goto(pathToFileURL(path.join(__dirname,'../outputs/webview2-shell/index.html')).href);
    await page.evaluate(()=>{
      const tracks=[
        {id:'a1',title:'Northern Lights',artist:'Celeste Ensemble',album:'Aurora',year:2020,track:1,disc:1,genre:'Ambient',sortPath:'C:/a.flac',duration:240,chips:[],coverUrl:'https://celeste-cover.internal/cover/a1'},
        {id:'a2',title:'After the Rain',artist:'Celeste Ensemble',album:'Aurora',year:2020,track:1,disc:2,genre:'Ambient',sortPath:'C:/b.flac',duration:180,chips:[],coverUrl:'https://celeste-cover.internal/cover/a2'},
        {id:'b1',title:'Midnight Walk',artist:'Night Radio',album:'Midnight',year:2010,track:5,disc:1,genre:'Jazz',sortPath:'C:/c.flac',duration:300,chips:[],coverUrl:'https://celeste-cover.internal/cover/b1'}
      ];
      deliver({kind:'celeste.library',version:1,phase:'begin',revision:1,total:3});deliver({kind:'celeste.library',version:1,phase:'batch',revision:1,tracks});
      deliver({kind:'celeste.library',version:1,phase:'end',revision:1,albums:[
        {id:'aurora',title:'Aurora',artist:'Celeste Ensemble',year:2020,added:0,trackCount:2,duration:420,coverUrl:tracks[0].coverUrl+'?size=480',trackIds:['a1','a2']},
        {id:'midnight',title:'Midnight',artist:'Night Radio',year:2010,added:1,trackCount:1,duration:300,coverUrl:tracks[2].coverUrl+'?size=480',trackIds:['b1']}
      ]});
    });
    await page.locator('#corner-orbit [data-page="songs"]').click();
    await page.waitForFunction(()=>[...document.querySelectorAll('.song-cover img')].every(image=>image.complete&&image.naturalWidth>0));
    assert.equal(await page.locator('.song-cover img').count(),3);
    assert.equal(await page.locator('.song-cover img').first().evaluate(image=>getComputedStyle(image).opacity),'1');
    assert.equal(await page.locator('#songs-sort option').count(),8);
    await page.locator('#songs-sort').selectOption('year');
    assert.equal(await page.locator('.song-row').first().getAttribute('data-track-id'),'b1');
    await page.locator('#songs-sort').selectOption('track');
    assert.equal(await page.locator('.song-row').last().getAttribute('data-track-id'),'b1');
    assert.equal(await page.locator('#volume').evaluate(input=>getComputedStyle(input,'::-webkit-slider-thumb').borderTopWidth),'0px');
    await page.evaluate(()=>{
      deliver({kind:'celeste.state',version:1,state:{trackId:'a1',title:'Northern Lights',artist:'Celeste Ensemble',duration:240,position:60,playing:false}});
      deliver({kind:'celeste.waveform',version:1,trackId:'a1',peaks:Array.from({length:120},(_,i)=>.1+(i%10)/12)});
    });
    await page.waitForFunction(()=>document.querySelector('#waveform-status').textContent==='歌曲波形');
    await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
    const pixels=await page.locator('#progress-waveform').evaluate(canvas=>{
      const data=canvas.getContext('2d').getImageData(0,0,canvas.width,canvas.height).data;let pink=0,gray=0;
      for(let i=0;i<data.length;i+=4){if(data[i+3]&&data[i]===230&&data[i+1]===107)pink++;if(data[i+3]&&data[i]===223&&data[i+1]===213)gray++;}return {pink,gray};
    });
    assert.ok(pixels.pink>0&&pixels.gray>pixels.pink);
    await page.locator('#progress').click({position:{x:Math.round((await page.locator('#progress').boundingBox()).width*.75),y:17}});
    assert.ok(Math.abs((await page.evaluate(()=>commands.filter(message=>message.command==='seek').at(-1))).seconds-180)<3);
    await page.evaluate(()=>CelesteShell.navigate('albums'));
    assert.equal(await page.locator('.album-card').count(),2);
    await page.waitForFunction(()=>[...document.querySelectorAll('.album-art img')].every(image=>image.complete&&image.naturalWidth>0));
    assert.equal(await page.locator('.album-art img').first().evaluate(image=>getComputedStyle(image).opacity),'1');
    await page.locator('#albums-sort').selectOption('year');
    assert.equal(await page.locator('.album-card').first().getAttribute('data-album-id'),'midnight');
    await page.locator('#search').fill('Ensemble');assert.equal(await page.locator('.album-card').count(),1);
    await page.locator('.album-open').click();
    assert.equal(await page.locator('.album-disc-heading').count(),2);
    await page.locator('#album-play').click();
    assert.deepEqual(await page.evaluate(()=>commands.filter(message=>message.command==='playAll').at(-1).trackIds),['a1','a2']);
    await page.locator('.album-track').nth(1).dblclick();
    assert.equal(await page.evaluate(()=>commands.filter(message=>message.command==='playTrack').at(-1).trackId),'a2');
    await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-album-detail.png')});
    await page.locator('#breadcrumb button').filter({hasText:/^专辑$/}).click();await page.locator('#search').fill('');
    for(const width of [1440,1024,760]){await page.setViewportSize({width,height:960});assert.ok(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth));}
    await page.locator('#transport-more').click();await page.locator('[data-more="locateAlbum"]').click();
    assert.equal(await page.locator('#album-detail-title').textContent(),'Aurora');
    assert.deepEqual(errors,[]);
    const demo=await browser.newPage({viewport:{width:1440,height:960}});
    await demo.goto(pathToFileURL(path.join(__dirname,'../outputs/webview2-shell/index.html')).href);await demo.locator('#corner-orbit [data-page="albums"]').click();
    await demo.waitForTimeout(450);
    await demo.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-albums.png')});
    console.log('PASS: visible list/album covers, 8 song sorts, real waveform drawing and seek, no white thumbs, album search/sort/discs/playback/locate, responsive grid.');
  }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
