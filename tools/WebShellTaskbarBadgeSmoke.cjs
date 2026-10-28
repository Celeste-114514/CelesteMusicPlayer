// Headless: use the actual main shell and its incoming-state message handler.
const assert=require('node:assert/strict');
const path=require('node:path');
const {pathToFileURL}=require('node:url');
const {chromium}=require('playwright-core');
(async()=>{
 const browser=await chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1500,height:1000}});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.addInitScript(()=>{
   const handlers=[];window.sent=[];
   window.chrome={webview:{postMessage:m=>sent.push(m),addEventListener:(_,fn)=>handlers.push(fn)}};
   window.deliver=m=>handlers.forEach(fn=>fn({data:m}));
  });
  await page.goto(pathToFileURL(path.resolve(__dirname,'../outputs/webview2-shell/index.html')).href);
  for(const enabled of [true,false,true]){
   await page.evaluate(value=>deliver({kind:'celeste.state',version:1,state:{taskbarPlayer:value,desktopLyrics:false,playing:false,duration:0,position:0}}),enabled);
   await page.waitForFunction(value=>document.getElementById('taskbar-player').getAttribute('aria-pressed')===String(value),enabled);
   assert.equal(await page.locator('#taskbar-badge').textContent(),enabled?'on':'off');
  }
  // An incomplete state update must preserve the last known switch state.
  await page.evaluate(()=>deliver({kind:'celeste.state',version:1,state:{playing:true}}));
  assert.equal(await page.locator('#taskbar-badge').textContent(),'on');
  await page.locator('#taskbar-player').click();
  assert(await page.evaluate(()=>sent.some(m=>m.command==='transport'&&m.action==='toggleTaskbarPlayer')));
  assert.deepEqual(errors,[]);
  console.log('PASS: main-shell native state messages update ON/OFF, partial updates preserve state, toggle command reaches bridge');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
