// Requires playwright on NODE_PATH and installed Microsoft Edge. Browser-only checks; native input needs a live smoke test.
const {chromium}=require('playwright');
const assert=require('node:assert/strict');const {pathToFileURL}=require('node:url');
(async()=>{const browser=await chromium.launch({headless:true,channel:"msedge"});const page=await browser.newPage({viewport:{width:900,height:230}});const errors=[];page.on('pageerror',e=>errors.push(String(e)));
await page.addInitScript(()=>{window.sent=[];window.chrome={webview:{postMessage:m=>sent.push(m),addEventListener:(k,f)=>window.deliver=f}}});
await page.goto(pathToFileURL(require('node:path').resolve('outputs/webview2-shell/desktop-lyrics.html')).href);
const send=m=>page.evaluate(m=>deliver({data:m}),m);
const config={kind:'settings',DesktopLyricColorMode:'Gradient',DesktopLyricGradientStart:'#ffbb00',DesktopLyricGradientEnd:'#ff4400',DesktopLyricUnplayedColor:'#eeeeee',DesktopLyricOutlineColor:'#111111',DesktopLyricOutlineWidth:1,DesktopLyricShadowStrength:1,DesktopLyricFontSize:34,DesktopLyricSecondaryFontSize:19,DesktopLyricScale:100,DesktopLyricOpacity:100,DesktopLyricVisibleLines:3,DesktopLyricFontFamily:'Microsoft YaHei UI',DesktopLyricDirection:'Horizontal',DesktopLyricShowUnlockWhenLocked:true,LyricKaraokeStyle:true};
await send(config);await send({kind:'lyrics',lines:[{t:0,text:'作词：Shing02',chars:[0,.1,.2,.3,.4,.5,.6,.7,.8,.9,1,1.1]},{t:8,text:'第二句歌词'},{t:9,tr:true,text:'Translation'}]});await send({kind:'clock',time:.5,paused:true,rate:1});await page.waitForTimeout(100);
assert.equal(await page.locator('button svg').count(),7);assert.equal(await page.locator('#current').textContent(),'作词：Shing02');assert.equal(await page.locator('#current span').first().evaluate(e=>getComputedStyle(e,'::after').content),'none');
await page.mouse.move(400,100);await page.mouse.down();await page.mouse.up();assert((await page.evaluate(()=>sent)).some(m=>m.command==='drag'));
await page.getByRole('button',{name:'下一首',exact:true}).click();assert((await page.evaluate(()=>sent)).some(m=>m.command==='next'));
await send({kind:'clock',time:8.5,paused:false});await page.waitForTimeout(50);assert.equal(await page.locator('#translation').textContent(),'Translation');assert.equal(await page.locator('[data-command=play]').getAttribute('aria-label'),'暂停');
await page.screenshot({path:'outputs/desktop-lyrics-horizontal.png'});
await send({...config,locked:true});await page.waitForTimeout(50);assert(await page.locator('#unlock').isVisible());await page.locator('#unlock').click();assert((await page.evaluate(()=>sent)).some(m=>m.command==='unlock'));
await page.setViewportSize({width:360,height:680});await send({...config,DesktopLyricDirection:'Vertical'});await page.waitForTimeout(50);assert.equal(await page.locator('#current').evaluate(e=>getComputedStyle(e).writingMode),'vertical-rl');await page.screenshot({path:'outputs/desktop-lyrics-vertical.png'});
assert.deepEqual(errors,[]);await browser.close();console.log('PASS: single text layer, SVG controls, playback state, drag command, translation, lock/unlock, vertical layout');})().catch(e=>{console.error(e);process.exit(1)});
