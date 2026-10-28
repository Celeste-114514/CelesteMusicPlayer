const assert=require('node:assert/strict');const path=require('node:path');const{pathToFileURL}=require('node:url');const{chromium}=require('playwright-core');
(async()=>{const b=await chromium.launch({headless:true,channel:'msedge'});try{
for(const dpi of [1,1.25,1.5,2]){
 const p=await b.newPage({viewport:{width:360,height:48},deviceScaleFactor:dpi});
 await p.addInitScript(()=>{window.sent=[];window.chrome={webview:{postMessage:m=>sent.push(m),addEventListener:(_,fn)=>window.receive=fn}};});
 await p.goto(pathToFileURL(path.resolve(__dirname,'../outputs/webview2-shell/taskbar-player.html')).href);
 await p.evaluate(()=>receive({data:{background:'#fffdf8',state:{title:'A very long song title that must truncate safely',artist:'Pink Floyd',album:'The Dark Side of the Moon',playing:true,duration:100,position:38}}}));
 assert.equal(await p.locator('#progress').evaluate(e=>e.style.width),'38%');
 await p.getByRole('button',{name:'暂停',exact:true}).click();await p.getByRole('button',{name:'下一首',exact:true}).click();
 await p.locator('#identity').dblclick();await p.locator('#identity').click({button:'right'});
 assert.deepEqual(await p.evaluate(()=>sent.map(m=>m.action)),['togglePlay','next','showMain','menu']);
 await p.evaluate(()=>receive({data:{background:'#252525',state:{title:'<img onerror=alert(1)>',busy:true,playing:false,duration:0,position:NaN}}}));
 assert.equal(await p.locator('#title').textContent(),'<img onerror=alert(1)>');
 assert.equal(await p.locator('#progress').evaluate(e=>e.style.width),'0%');assert.equal(await p.locator('button:disabled').count(),3);
 assert(await p.evaluate(()=>document.documentElement.scrollWidth<=innerWidth&&document.documentElement.scrollHeight<=innerHeight),'No overflow at taskbar height');
 await p.close();
}console.log('PASS: taskbar actions, double-click restore, context menu, progress, busy state and 100–200% DPI layout');
}finally{await b.close();}})().catch(e=>{console.error(e);process.exitCode=1;});
