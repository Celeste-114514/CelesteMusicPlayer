const assert=require('node:assert/strict'),http=require('node:http'),fs=require('node:fs'),path=require('node:path');
const {chromium}=require('playwright');
const root=path.resolve(__dirname,'../outputs/webview2-shell');
const server=http.createServer((req,res)=>{
 const file=path.join(root,decodeURIComponent(new URL(req.url,'http://local').pathname));
 if(!file.startsWith(root+path.sep)){res.writeHead(403).end();return;}
 fs.readFile(file,(error,data)=>{if(error){res.writeHead(404).end();return;}res.setHeader('Content-Type',file.endsWith('.js')?'text/javascript':file.endsWith('.css')?'text/css':file.endsWith('.glb')?'model/gltf-binary':'text/html');res.end(data);});
});
(async()=>{
 await new Promise(r=>server.listen(0,'127.0.0.1',r));
 const browser=await chromium.launch({executablePath:'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',headless:true,args:['--enable-webgl','--use-angle=d3d11']});
 try{
 const page=await browser.newPage({viewport:{width:1440,height:960}}),errors=[];page.on('pageerror',e=>errors.push(e.message));
 await page.goto(`http://127.0.0.1:${server.address().port}/index.html`);
 await page.evaluate(()=>window.CelesteShell.navigate('albums'));
 await page.locator('#rhine-launch').click();
 await page.waitForFunction(()=>window.CelesteRhine.getStats()?.loaded,{timeout:90000});
 await page.waitForTimeout(2200);
 await page.screenshot({path:path.resolve(__dirname,'../outputs/rhine-array-preview.png')});
 assert.equal(await page.locator('.rh-scene canvas').count(),1);
 const count=await page.locator('.rh-picker option').count();assert(count>1);
 await page.locator('.rh-picker').selectOption({index:1});
 const expected=await page.locator('.rh-picker option').nth(1).textContent();
 assert(expected.includes(await page.locator('.rh-title').textContent()));
 await page.getByRole('button',{name:'抽取专辑',exact:true}).click();
 await page.waitForTimeout(6500);assert(await page.locator('.rh-tracks').isVisible());
 await page.screenshot({path:path.resolve(__dirname,'../outputs/rhine-detail-preview.png')});
 await page.getByRole('button',{name:'播放整张',exact:true}).click();
 assert((await page.evaluate(()=>window.CelesteShell.getState())).playing);
 await page.keyboard.press('Escape');assert.equal(await page.locator('.rhine-room').count(),0);
 assert.equal(await page.evaluate(()=>window.CelesteRhine.getStats()),undefined);
 await page.locator('#rhine-launch').click();await page.waitForFunction(()=>window.CelesteRhine.getStats()?.loaded,{timeout:90000});
 await page.evaluate(()=>window.CelesteShell.navigate('songs'));assert.equal(await page.locator('.rhine-room').count(),0);
 await page.evaluate(()=>window.CelesteShell.navigate('albums'));
 await page.setViewportSize({width:900,height:760});await page.locator('#rhine-launch').click();
 await page.waitForFunction(()=>window.CelesteRhine.getStats()?.loaded,{timeout:90000});
 assert(await page.getByRole('button',{name:'返回播放器',exact:true}).isVisible());
 await page.getByRole('button',{name:'返回播放器',exact:true}).click();
 const native=await browser.newPage();
 await native.addInitScript(()=>{
   window.commands=[];window.chrome ||= {};window.chrome.webview={
     addEventListener:(name,fn)=>{if(name==='message')window.deliver=message=>fn({data:message});},
     postMessage:message=>window.commands.push(message)
   };
 });
 await native.goto(`http://127.0.0.1:${server.address().port}/index.html`);
 await native.evaluate(()=>{
   const track={id:'native-track-1',title:'真实桥接曲目',artist:'测试艺术家',album:'本地测试专辑',duration:180,chips:[],remote:false};
   window.deliver({version:1,kind:'celeste.library',phase:'begin',revision:1,total:1});
   window.deliver({version:1,kind:'celeste.library',phase:'batch',revision:1,tracks:[track]});
   window.deliver({version:1,kind:'celeste.library',phase:'end',revision:1,albums:[{id:'native-album',title:track.album,artist:track.artist,trackCount:1,trackIds:[track.id],duration:180}]});
   window.CelesteShell.navigate('albums');
 });
 await native.locator('#rhine-launch').click();await native.waitForFunction(()=>window.CelesteRhine.getStats()?.loaded,{timeout:90000});
 await native.getByRole('button',{name:'播放整张',exact:true}).click();
 assert((await native.evaluate(()=>window.commands)).some(c=>c.command==='playAll'&&c.trackIds?.[0]==='native-track-1'));
 await native.getByRole('button',{name:'抽取专辑',exact:true}).click();
 await native.locator('.rh-track').click();
 assert((await native.evaluate(()=>window.commands)).some(c=>c.command==='playTrack'&&c.trackId==='native-track-1'));
 await native.evaluate(()=>document.dispatchEvent(new CustomEvent('celeste:library')));assert.equal(await native.locator('.rhine-room').count(),0);
 assert.deepEqual(errors,[]);console.log('PASS: WebGL model, album identity, extraction, tracks, playback, disposal, reopen, navigation, narrow window, native bridge track IDs and library refresh');
 }finally{await browser.close();server.close();}
})().catch(e=>{console.error(e);server.close();process.exitCode=1;});
