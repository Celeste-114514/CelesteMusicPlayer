// Run with NODE_PATH pointing at the bundled node_modules (Playwright).
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({
    executablePath: path.join(process.env['ProgramFiles(x86)'] || 'C:/Program Files (x86)', 'Microsoft/Edge/Application/msedge.exe'),
    headless: true
  });
  try {
    const page = await browser.newPage();
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      window.commands = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage: message => {
          window.commands.push(message);
          if(!message.requestId)return;
          window.hostSettings ||= {selectedMode:'Shared',selectedDeviceId:'one',actualMode:'WasapiShared',actualDevice:'Actual device',devices:[{id:'one',name:'Device one'},{id:'two',name:'Device two'}],rate:1,preservePitch:true,a:-1,b:-1,abActive:false,abHint:'先设 A 点',sleepSelection:0,sleepTracks:1,deviceMemory:false,canSaveProfile:false,hasDeviceProfile:false};
          const s=window.hostSettings;
          let data;
          if(message.command==='audioSettings'){
            if(message.operation==='setMode')s.selectedMode=message.mode;
            if(message.operation==='setDevice')s.selectedDeviceId=message.deviceId;
            if(message.operation==='setRate')s.rate=message.rate;
            if(message.operation==='setPitch')s.preservePitch=message.preservePitch;
            if(message.operation==='setSleep'){s.sleepSelection=message.selection;s.sleepTracks=message.tracks;}
            if(message.operation==='deviceMemory')s.deviceMemory=message.enabled;
            if(message.operation==='ab'){if(message.action==='a')s.a=12;if(message.action==='b')s.b=20;if(message.action==='toggle')s.abActive=!s.abActive;}
            data={...s};
          }else if(message.command==='queue'){
            data={revision:7,total:2,offset:0,currentIndex:0,tracks:[{id:'cue-a',index:0,title:'CUE A',artist:'Test',duration:60},{id:'cue-b',index:1,title:'CUE B',artist:'Test',duration:65}]};
          }else if(message.command==='more')data=message.operation==='get'?{playlists:['Test playlist']}:{message:'已完成'};
          setTimeout(()=>window.deliver({kind:'celeste.reply',version:1,requestId:message.requestId,ok:true,data}),10);
        },
        addEventListener: (_, handler) => window.deliver = data => handler({ data })
      };
    });
    await page.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href);
    await page.evaluate(() => {
      deliver({ kind:'celeste.library', version:1, phase:'begin', revision:1, total:2 });
      deliver({ kind:'celeste.library', version:1, phase:'batch', revision:1, tracks:[
        { id:'stable-z', title:'Zebra', artist:'Test', album:'Album', duration:120, chips:[], coverUrl:'' },
        { id:'stable-a', title:'Alpha', artist:'Test', album:'Album', duration:240, chips:[], coverUrl:'' }
      ] });
      deliver({ kind:'celeste.library', version:1, phase:'end', revision:1 });
    });
    await page.locator('#corner-orbit [data-page="songs"]').click();
    await page.locator('.song-row').first().dblclick();
    let messages = await page.evaluate(() => window.commands);
    assert.equal(messages.filter(m => m.command === 'playTrack').length, 1);
    assert.equal(messages.find(m => m.command === 'playTrack').trackId, 'stable-a');
    await page.locator('#songs-direction').click();
    await page.locator('#songs-play-all').click();
    messages = await page.evaluate(() => window.commands);
    assert.deepEqual(messages.find(m => m.command === 'playAll').trackIds, ['stable-z','stable-a']);
    await page.locator('#search').fill('Alpha');
    await page.locator('#search').press('Enter');
    assert.equal(await page.locator('.song-row').count(), 1);
    assert.equal((await page.evaluate(() => window.commands)).some(m => m.command === 'search'), false);
    await page.evaluate(() => {
      deliver({ kind:'celeste.metadata', version:1, trackId:'stable-a', chips:['FLAC','24bit/96kHz'] });
      deliver({ kind:'celeste.state', version:1, state:{trackId:'stable-a',title:'Alpha',artist:'Test',duration:240,position:12,volume:60,playing:true,outputMode:'Asio',quality:'24bit/96kHz'} });
    });
    assert.match(await page.locator('.song-badges').textContent(), /FLAC/);
    assert.match(await page.locator('#track-quality').textContent(), /ASIO/);
    assert.equal(await page.locator('#progress').inputValue(), '12');
    assert.equal(await page.locator('.song-row.playing').count(), 1);
    await page.locator('#orbit-toggle').click();
    await page.locator('#songs-title').click();
    assert.equal(await page.locator('#orbit-toggle').getAttribute('aria-expanded'), 'false');
    for (const [id, action] of [['seek-back','seekBack'],['seek-forward','seekForward'],['lyrics','desktopLyrics'],['playback-order','cycleOrder']]) {
      await page.locator('#'+id).click();
      assert.equal(await page.evaluate(() => commands.at(-1).action),action);
    }
    await page.locator('#playback-order').click({button:'right'});
    await page.locator('[data-order="TrackOnce"]').click();
    assert.deepEqual(await page.evaluate(()=>commands.at(-1)),{kind:'celeste.command',version:1,command:'setPlaybackOrder',order:'TrackOnce'});
    await page.setViewportSize({width:1440,height:960});
    await page.locator('#audio-settings').click();
    await page.waitForFunction(()=>document.querySelector('#audio-device').options.length===2&&!document.querySelector('#audio-device').disabled);
    assert.equal(await page.locator('#transport-dialog').evaluate(dialog=>dialog.open),true);
    assert.equal(await page.locator('#transport-dialog').evaluate(dialog=>dialog.matches(':modal')),false);
    for(const width of [1440,1024,760]){
      await page.setViewportSize({width,height:960});
      await page.evaluate(()=>new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve))));
      const geometry=await page.evaluate(()=>{
        const panel=document.querySelector('#transport-dialog').getBoundingClientRect(),button=document.querySelector('#audio-settings').getBoundingClientRect();
        const scroll=getComputedStyle(document.querySelector('.transport-dialog-body')),main=getComputedStyle(document.querySelector('.main'));
        return {left:panel.left,right:panel.right,top:panel.top,bottom:panel.bottom,anchor:button.top,colour:scroll.scrollbarColor,mainColour:main.scrollbarColor,width:scroll.scrollbarWidth};
      });
      assert.ok(geometry.left>=15&&geometry.right<=width-15&&geometry.top>=15&&geometry.bottom<geometry.anchor,JSON.stringify(geometry));
      assert.equal(geometry.colour,geometry.mainColour);assert.equal(geometry.width,'thin');
    }
    await page.setViewportSize({width:1440,height:960});
    assert.match(await page.locator('#audio-actual').textContent(),/Actual device/);
    await page.locator('[data-output-mode="WasapiExclusive"]').click();
    await page.waitForFunction(()=>window.hostSettings.selectedMode==='WasapiExclusive'&&document.querySelector('#transport-dialog').getAttribute('aria-busy')==='false');
    // Selected mode must not falsely overwrite the actual running output.
    assert.match(await page.locator('#audio-actual').textContent(),/共享/);
    await page.locator('#audio-device').selectOption('two');
    await page.waitForFunction(()=>window.hostSettings.selectedDeviceId==='two'&&document.querySelector('#transport-dialog').getAttribute('aria-busy')==='false');
    await page.locator('[data-audio-tab="ab"]').click();
    await page.locator('[data-ab="a"]').click();
    await page.waitForFunction(()=>!document.querySelector('[data-ab="b"]').disabled);
    await page.locator('[data-ab="b"]').click();
    await page.waitForFunction(()=>!document.querySelector('#ab-toggle').disabled);
    await page.locator('#ab-toggle').click();
    await page.waitForFunction(()=>document.querySelector('#ab-toggle').textContent==='关闭 AB 循环');
    await page.locator('[data-audio-tab="speed"]').click();
    await page.locator('#audio-rate').fill('1.25');
    await page.locator('#audio-rate').dispatchEvent('change');
    await page.waitForFunction(()=>window.hostSettings.rate===1.25&&document.querySelector('#transport-dialog').getAttribute('aria-busy')==='false');
    await page.locator('[name="audio-pitch"][value="follow"]').check();
    await page.waitForFunction(()=>window.hostSettings.preservePitch===false&&document.querySelector('#transport-dialog').getAttribute('aria-busy')==='false');
    await page.locator('[data-audio-tab="sleep"]').click();
    await page.locator('[data-sleep="7"]').click();
    await page.waitForFunction(()=>!document.querySelector('#sleep-count-label').hidden&&document.querySelector('#transport-dialog').getAttribute('aria-busy')==='false');
    await page.locator('#sleep-count').fill('3');await page.locator('#sleep-count').dispatchEvent('change');
    await page.waitForFunction(()=>window.hostSettings.sleepTracks===3&&document.querySelector('#transport-dialog').getAttribute('aria-busy')==='false');
    await page.waitForTimeout(250);
    await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-audio-sleep.png')});
    await page.locator('[data-audio-tab="output"]').click();
    await page.waitForTimeout(250);
    await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-audio-output.png')});
    await page.locator('#transport-dialog-close').click();
    await page.waitForFunction(()=>!document.querySelector('#transport-dialog').open);
    await page.locator('#queue').click();
    await page.waitForSelector('.web-queue-row');
    await page.locator('.web-queue-row').nth(1).dblclick();
    const queuePlay=await page.evaluate(()=>commands.find(message=>message.command==='queue'&&message.operation==='play'));
    assert.equal(queuePlay.trackId,'cue-b');assert.equal(queuePlay.revision,7);assert.equal(queuePlay.index,1);
    await page.locator('#transport-dialog-close').click();await page.waitForFunction(()=>!document.querySelector('#transport-dialog').open);
    await page.locator('#transport-more').click();
    assert.equal(await page.locator('#transport-more-menu button:disabled').count(),1);
    await page.locator('[data-more="addToPlaylist"]').click();await page.waitForSelector('#web-playlist-list button');
    await page.locator('#web-playlist-list button').click();await page.waitForFunction(()=>!document.querySelector('#transport-dialog').open);
    assert.equal(await page.evaluate(()=>commands.some(m=>m.command==='transport'&&['audioSettings','more','queue'].includes(m.action))),false);
    for (const width of [1440,1024,760]) {
      await page.setViewportSize({width,height:960});
      const centres=await page.evaluate(()=>{
        const box=id=>document.getElementById(id).getBoundingClientRect();
        const play=box('play'),progress=box('progress'),player=document.querySelector('.player').getBoundingClientRect();
        return [play.x+play.width/2,progress.x+progress.width/2,player.x+player.width/2];
      });
      assert.ok(Math.max(...centres)-Math.min(...centres)<1,`controls must centre at width ${width}: ${centres}`);
    }
    // A slow previous request must never blank or overwrite the newest artwork.
    const png=Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=','base64');
    let releaseSlow;
    const slow=new Promise(resolve=>releaseSlow=resolve);
    await page.route('https://celeste-cover.internal/cover/*',async route=>{
      if(route.request().url().endsWith('/slow'))await slow;
      await route.fulfill({status:200,contentType:'image/png',body:png});
    });
    const cover=async name=>page.evaluate(name=>deliver({kind:'celeste.state',version:1,state:{trackId:name,coverUrl:'https://celeste-cover.internal/cover/'+name}}),name);
    await cover('first');
    await page.waitForSelector('.mini-cover img.cover-current[src$="/first"]');
    await cover('slow');
    assert.equal(await page.locator('.mini-cover img.cover-current[src$="/first"]').count(),1);
    await cover('latest');
    await page.waitForSelector('.mini-cover img.cover-current[src$="/latest"]');
    releaseSlow();
    await page.waitForTimeout(600);
    assert.equal(await page.locator('.mini-cover img').count(),1);
    assert.ok((await page.locator('.mini-cover img').getAttribute('src')).endsWith('/latest'));
    await page.setViewportSize({width:1440,height:960});
    await page.screenshot({path:path.join(__dirname,'../outputs/webview2-shell/preview-transport.png')});
    assert.deepEqual(errors, []);
    console.log('PASS: library/playback, five orders, HTML settings/queue/menus, RPC mutations, selected vs actual output, centred layouts, decoded covers and rapid-switch race.');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
