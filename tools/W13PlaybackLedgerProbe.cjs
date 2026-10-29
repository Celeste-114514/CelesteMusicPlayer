// 一次性验证：真实起播两首，看台账日志与数据库。会出声，窗口隐藏。
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
      const lib = CelesteShell.getLibrary().filter(t => !t.isRemote);
      const short = lib.filter(t => t.duration > 20 && t.duration < 200);
      const a = short[0], b = short[1];
      return [a && { id: a.id, title: a.title, dur: a.duration }, b && { id: b.id, title: b.title, dur: b.duration }];
    });
    console.log('选中:', JSON.stringify(picked));

    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), picked[0].id);
    await wait(Number(process.env.PLAY_MS || 5000));
    const state1 = await page.evaluate(async () => await CelesteTransport.request('queue', { operation: 'get' }));
    console.log('第一首后 currentIndex=', state1.currentIndex, 'tracks=', state1.tracks.length);

    await page.evaluate(() => CelesteShell.send('transport', { action: 'next' }));
    await wait(3500);
    const state2 = await page.evaluate(async () => await CelesteTransport.request('nowPlaying', {}));
    console.log('切歌后 nowPlaying trackId=', (state2.trackId || '').slice(0, 12));

    await page.evaluate(() => { try { CelesteShell.send('window', { action: 'close' }); } catch { } });
    for (let i = 0; i < 30 && child.exitCode === null; i++) await wait(400);
    console.log('退出 exitCode=', child.exitCode);
  } finally {
    try { await browser.close(); } catch { }
    if (child.exitCode === null) child.kill();
  }
  process.exit(0);
})().catch(e => { console.error('FAIL', e); process.exit(1); });
