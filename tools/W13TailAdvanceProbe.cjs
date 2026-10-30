// 复现"长曲自然播完后界面不换曲"：播指定曲目，快进到尾部 5 秒，等它自己接下一首，
// 全程每 500ms 采一次网页状态与底栏标题。会出声（约 20 秒）。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13TailAdvanceProbe.cjs <exe> ["歌名包含"] [等待秒数]
const path = require('node:path');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
const match = (process.argv[3] || 'Time').toLowerCase();
const seconds = Number(process.argv[4] || 25);
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

    const target = await page.evaluate(m => {
      const lib = CelesteShell.getLibrary().filter(t => !t.isRemote && (t.title || '').toLowerCase().includes(m));
      const t = lib.sort((a, b) => b.duration - a.duration)[0];
      return t ? { id: t.id, title: t.title, duration: t.duration, album: t.album } : null;
    }, match);
    if (!target) { console.log('找不到曲目:', match); return; }
    console.log('目标:', JSON.stringify(target));

    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), target.id);
    await wait(2500);
    // 详情页开着才暴露真实场景（歌词面板、频谱采样都会跑）
    const opened = await page.evaluate(() => { try { window.CelesteNowPlaying?.open?.(); return !!window.CelesteNowPlaying?.isOpen?.(); } catch { return false; } });
    console.log('详情页已打开=', opened);
    const seekTo = Math.max(0, target.duration - 5);
    await page.evaluate(s => CelesteShell.send('seek', { seconds: s }), seekTo);
    console.log('已快进到', seekTo.toFixed(1), '秒，等它自然接下一首…');

    const samples = [];
    const started = Date.now();
    while (Date.now() - started < seconds * 1000) {
      const snap = await page.evaluate(async () => {
        const st = (window.CelesteShell?.getState?.() || {});
        const q = await CelesteTransport.request('queue', { operation: 'get' }).catch(() => null);
        const np = await CelesteTransport.request('nowPlaying', {}).catch(() => null);
        const npTitle = document.getElementById('np-title')?.textContent || '';
        const lyric = document.querySelector('#np-lyrics .np-lyric')?.textContent || '';
        return {
          trackId: String(st.trackId || '').slice(0, 10),
          stateTitle: st.title || '',
          barTitle: document.getElementById('track-title')?.textContent || '',
          npTitle,
          lyric: String(lyric).slice(0, 18),
          hostTrackId: np ? String(np.trackId || '').slice(0, 10) : '',
          lyricState: np ? np.lyricState : '',
          playing: !!st.playing,
          pos: Math.round(st.position || 0),
          queueIndex: q ? q.currentIndex : null,
          queueLen: q ? q.tracks?.length : null
        };
      }).catch(() => null);
      samples.push({ t: Math.round((Date.now() - started) / 100) / 10, ...snap });
      await wait(500);
    }

    let prev = null;
    console.log('\n变化点（秒 / state曲目 / 底栏 / state标题 / 队列下标 / 位置）:');
    for (const s of samples) {
      if (!s) continue;
      const key = s.trackId + '|' + s.npTitle + '|' + s.hostTrackId + '|' + s.lyric + '|' + s.queueIndex;
      if (key !== prev) {
        console.log(`  ${String(s.t).padStart(5)}s  state=${s.trackId || '-'}  详情页标题=[${s.npTitle}]  宿主nowPlaying=${s.hostTrackId}(${s.lyricState})  歌词=[${s.lyric}]  qIdx=${s.queueIndex}/${s.queueLen}  pos=${s.pos}  playing=${s.playing}`);
        prev = key;
      }
    }
    console.log('\n结束:', JSON.stringify(samples[samples.length - 1]));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
