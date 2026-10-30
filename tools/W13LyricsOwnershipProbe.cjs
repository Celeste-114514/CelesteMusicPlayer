// 验证「换歌后不会把上一首的歌词挂到新歌下面」。
// 做法：播 A → 等歌词就绪 → 播 B → 每 120ms 采 nowPlaying，检查 B 的样本里有没有出现 A 的歌词行。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13LyricsOwnershipProbe.cjs <exe>
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

    const pair = await page.evaluate(() => {
      const lib = CelesteShell.getLibrary().filter(t => !t.isRemote);
      return lib.slice(0, 2).map(t => ({ id: t.id, title: t.title }));
    });
    console.log('A =', pair[0].title, '/ B =', pair[1].title);

    const read = () => page.evaluate(async () => {
      const d = await CelesteTransport.request('nowPlaying', {});
      return {
        trackId: String(d.trackId || '').slice(0, 10),
        state: d.lyricState,
        lines: (d.lines || []).slice(0, 3).map(l => l.text),
        count: (d.lines || []).length
      };
    });

    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), pair[0].id);
    await wait(5000);
    const a = await read();
    console.log('A 的歌词:', a.state, a.count, '行，前三行=', JSON.stringify(a.lines));
    if (!a.count) { console.log('A 没有歌词，这条用例无法验证（换一首带歌词的再试）'); return; }
    const aLines = new Set(a.lines);

    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), pair[1].id);
    let violations = 0, samples = 0, loadingWithLines = 0, bReady = null;
    const started = Date.now();
    while (Date.now() - started < 5000) {
      const s = await read().catch(() => null);
      samples++;
      if (s) {
        if (s.state === 'loading' && s.count > 0) loadingWithLines++;
        if (s.trackId !== a.trackId && s.lines.some(t => aLines.has(t))) {
          violations++;
          console.log('  ! 违规样本: trackId=' + s.trackId + ' state=' + s.state + ' lines=' + JSON.stringify(s.lines));
        }
        if (!bReady && s.trackId && s.trackId !== a.trackId) bReady = s;
      }
      await wait(120);
    }
    console.log('样本数:', samples, '| B 的首次样本:', JSON.stringify(bReady));
    console.log('窜词次数:', violations, '| loading 却带歌词:', loadingWithLines);
    console.log(violations === 0 && loadingWithLines === 0 ? 'PASS' : 'FAIL');
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
