const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require('playwright-core');
(async () => {
  const browser = await chromium.launch({headless:true,channel:'msedge'});
  try {
    const page = await browser.newPage({viewport:{width:1000,height:600}});
    await page.setContent(`<style>
      body{margin:0}.titlebar{height:58px;width:1000px}
      .corner-orbit{position:absolute;left:350px;top:12px;width:300px;height:145px}
      .corner-toggle{position:absolute;left:137px;top:0;width:26px;height:26px}
      .corner-item{position:absolute;left:20px;top:15px;width:26px;height:26px}
      .is-collapsed .orbit-content{visibility:hidden}
      </style><div class="titlebar"></div><div class="corner-orbit">
      <button class="corner-toggle">Toggle</button><div class="orbit-content"><button class="corner-item">Item</button></div></div>`);
    await page.evaluate(() => {
      window.chrome={webview:{}};
      window.CelesteShell={send:(_kind,payload)=>window.caption=payload.rects};
    });
    await page.addScriptTag({path:path.resolve(__dirname,'../outputs/webview2-shell/window-drag.js')});
    const covers = (x,y) => page.evaluate(({x,y})=>window.caption.some(r=>x>=r.x&&x<r.x+r.width&&y>=r.y&&y<r.y+r.height),{x,y});
    await page.waitForFunction(()=>window.caption?.length);
    assert(await covers(420,30),'Expanded orbit blank area must drag');
    assert(!await covers(380,30),'Visible navigation button must remain clickable');
    assert(!await covers(500,20),'Toggle must remain clickable');
    await page.evaluate(()=>document.querySelector('.corner-orbit').classList.add('is-collapsed'));
    await page.waitForFunction(()=>window.caption.some(r=>380>=r.x&&380<r.x+r.width));
    assert(await covers(380,30),'Hidden navigation button must stop blocking drag');
    assert(!await covers(500,20),'Collapsed toggle must remain clickable');
    await page.evaluate(()=>document.querySelector('.corner-orbit').classList.remove('is-collapsed'));
    await page.waitForFunction(()=>!window.caption.some(r=>380>=r.x&&380<r.x+r.width));
    console.log('PASS: expanded/collapsed caption gaps and navigation controls');
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
