const assert=require('node:assert/strict');
const {chromium}=require('playwright-core');
const {pathToFileURL}=require('node:url');
const path=require('node:path');
(async()=>{
 const browser=await chromium.launch({headless:true,channel:'msedge'});
 try{
 const page=await browser.newPage();
 await page.addInitScript(()=>{
  window.sent=[];window.chrome={webview:{postMessage:x=>sent.push(x),addEventListener:(_,fn)=>window.receive=fn}};
 });
 await page.goto(pathToFileURL(path.resolve(__dirname,'../outputs/webview2-shell/tray-menu.html')).href);
 await page.evaluate(()=>receive({data:{kind:'celeste.trayTheme',version:1,theme:{mode:'light',vars:{'menu-bg':'#ffffff',ink:'#202020'}},items:[{actionId:'showMain',label:'显示主界面'},{separator:true},{actionId:'next',label:'下一首',enabled:false},{actionId:'openSettings',label:'设置'}]}}));
 await page.getByText('设置',{exact:true}).hover();
 assert.equal(await page.evaluate(()=>document.activeElement.tagName),'BODY','Hover must not steal focus');
 await page.getByText('设置',{exact:true}).click();
 assert.equal(await page.evaluate(()=>sent.filter(x=>x.action==='invoke').at(-1).actionId),'openSettings');
 await page.evaluate(()=>receive({data:{kind:'celeste.trayItems',version:1,items:[{actionId:'showMain',label:'显示主界面'},{separator:true},{actionId:'next',label:'下一首',enabled:false},{actionId:'openSettings',label:'设置'}]}}));
 await page.keyboard.press('Home');await page.keyboard.press('ArrowDown');
 assert.equal(await page.evaluate(()=>document.activeElement.textContent),'设置','Keyboard skips disabled rows and separators');
 await page.keyboard.press('Enter');
 assert.equal(await page.evaluate(()=>sent.filter(x=>x.action==='invoke').length),2);
 assert.equal(await page.evaluate(()=>sent.filter(x=>x.action==='blur').length),0);
 await page.evaluate(()=>receive({data:{kind:'celeste.trayItems',version:1,items:[{actionId:'exit',label:'退出播放器'}]}}));
 await page.getByText('退出播放器',{exact:true}).click();
 assert.equal(await page.evaluate(()=>sent.filter(x=>x.actionId==='exit').length),1,'Pointer and click must dispatch exit exactly once');
 console.log('PASS: hover/click/keyboard actions and focus transitions');
 }finally{await browser.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
