// 复现「队列下标还在上一首、实际已经换了曲目」：
// 先 playAll 两首（把队列填满、下标停在 A），再用 playTrack 直接播库里的另一首 C。
// 此时队列下标仍指向 A，而实际在播 C —— 网页标题应当显示 C。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13IndexMismatchProbe.cjs <exe>
const path = require('node:path');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
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

    const picked = await page.evaluate(() => {
      const lib = CelesteShell.getLibrary().filter(t => !t.isRemote && t.duration > 20);
      return [lib[0], lib[1], lib[9]].map(t => ({ id: t.id, title: t.title }));
    });
    console.log('A=', picked[0].title, '| B=', picked[1].title, '| C=', picked[2].title);

    await page.evaluate(ids => CelesteShell.send('playAll', { trackIds: ids }), [picked[0].id, picked[1].id]);
    await wait(2500);
    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), picked[2].id);
    await wait(3000);

    const now = await page.evaluate(async () => {
      const st = CelesteShell.getState();
      const q = await CelesteTransport.request('queue', { operation: 'get' });
      const np = await CelesteTransport.request('nowPlaying', {});
      return {
        stateTitle: st.title, stateId: String(st.trackId || '').slice(0, 10),
        barTitle: document.getElementById('track-title')?.textContent || '',
        queueIndex: q.currentIndex, queueLen: q.tracks.length,
        queueTitleAt: q.tracks?.[q.currentIndex]?.title || '(无)',
        hostTitle: np.title || '', lyricState: np.lyricState
      };
    });
    console.log(JSON.stringify(now, null, 2));
    console.log(now.stateTitle === picked[2].title ? 'PASS 界面跟着实际播放的曲目' : 'FAIL 界面停在下标那一首（' + now.stateTitle + '）');
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
