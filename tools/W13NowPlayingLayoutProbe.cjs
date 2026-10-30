// 播放详情页版式检查：确认频谱块排在进度条上方，并抓一张截图留档。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13NowPlayingLayoutProbe.cjs <exe> [截图路径]
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
const shot = path.resolve(process.argv[3] || path.join(__dirname, '../_now-playing-layout.png'));
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  const child = spawn(exe, ['--w7-background-test'], { stdio: 'ignore' });
  let browser = null;
  for (let i = 0; i < 60; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(1000); }
  }
  if (!browser) throw new Error('CDP 连接失败');
  try {
    let page = null;
    for (let i = 0; i < 90; i++) {
      page = browser.contexts()[0]?.pages().find(p => p.url().includes('celeste.internal'));
      if (page) break;
      await wait(1000);
    }
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 90000 });

    // 先放一首，详情页才有内容可画
    const first = await page.evaluate(() => {
      const t = CelesteShell.getLibrary().find(x => !x.isRemote);
      return t ? t.id : null;
    });
    if (first) { await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), first); await wait(2500); }

    // 探针会把进程 kill 掉，下次启动就会弹"上次没有正常退出"卡片，挡视线 —— 先关掉
    await page.evaluate(() => {
      const buttons = [...document.querySelectorAll('button')];
      const dismiss = buttons.find(b => (b.textContent || '').trim() === '知道了');
      if (dismiss) dismiss.click();
    });
    await wait(500);

    await page.evaluate(() => window.CelesteNowPlaying?.open?.());
    await wait(1200);   // 打开有入场动画，量尺寸要等它落地

    const layout = await page.evaluate(() => {
      const left = document.querySelector('#now-playing .np-left');
      if (!left) return { ok: false, reason: '找不到 .np-left' };
      // 后台测试模式下窗口不可见 → 采样逻辑会主动把频谱藏起来（document.hidden），
      // 量出来的高度会是 0。这里只为量版式把它强制显示，不动产品代码。
      const sp = left.querySelector('.np-spectrum');
      if (sp) { sp.hidden = false; sp.style.display = 'block'; }
      const order = [...left.children].map(el => el.className || el.tagName.toLowerCase());
      const spectrum = left.querySelector('.np-spectrum');
      const progress = left.querySelector('.np-progress');
      const rect = el => el ? el.getBoundingClientRect() : null;
      const rS = rect(spectrum), rP = rect(progress);
      return {
        ok: true,
        order,
        spectrumIndex: order.findIndex(c => String(c).includes('np-spectrum')),
        progressIndex: order.findIndex(c => String(c).includes('np-progress')),
        spectrumAboveProgress: !!(rS && rP && rS.bottom <= rP.top + 1),
        spectrumHeight: rS ? Math.round(rS.height) : 0,
        progressTop: rP ? Math.round(rP.top) : 0,
        spectrumBottom: rS ? Math.round(rS.bottom) : 0
      };
    });
    console.log(JSON.stringify(layout, null, 2));
    await wait(800);
    await page.screenshot({ path: shot, fullPage: false });
    console.log('截图:', shot, fs.existsSync(shot) ? '(' + Math.round(fs.statSync(shot).size / 1024) + 'KB)' : '(未生成)');
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
