// W13 无缝接续「界面跟不跟得上」探针。
// 播两首短曲，让第一首自然播完，全程每秒采一次 nowPlaying 快照，
// 用来判断"音频切了但界面没切"是宿主快照陈旧，还是网页侧没刷新。
// 会出声（约 30–90 秒）。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13SeamlessUiProbe.cjs <exe> [秒数]
const path = require('node:path');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
const seconds = Number(process.argv[3] || 75);
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

    const pick = await page.evaluate(() => {
      const lib = CelesteShell.getLibrary().filter(t => !t.isRemote);
      // 同一张专辑里挑两首短的，尽量让无缝接续真的生效
      const byAlbum = new Map();
      for (const t of lib) {
        const key = (t.album || '') + '|' + (t.albumArtist || '');
        if (!byAlbum.has(key)) byAlbum.set(key, []);
        byAlbum.get(key).push(t);
      }
      let best = null;
      for (const [, list] of byAlbum) {
        const short = list.filter(t => t.duration > 5 && t.duration < 90).sort((a, b) => a.duration - b.duration);
        if (short.length >= 2) {
          const span = short[0].duration + short[1].duration;
          if (!best || span < best.span) best = { span, list: short.slice(0, 2) };
        }
      }
      if (!best) return null;
      return { span: best.span, list: best.list.map(t => ({ id: t.id, title: t.title, duration: t.duration, album: t.album })) };
    });
    if (!pick) { console.log('找不到两首短曲，放弃'); return; }
    console.log('播放列表:', JSON.stringify(pick.list), '合计', Math.round(pick.span), '秒');

    // 关键：用 playTrack 单曲起播（队列为空 → 走库内续播），而不是 playAll 填队列。
    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), pick.list[0].id);

    const samples = [];
    const started = Date.now();
    while (Date.now() - started < seconds * 1000) {
      const snap = await page.evaluate(async () => {
        const st = (window.CelesteShell?.getState?.() || {});
        const text = id => document.getElementById(id)?.textContent || '';
        return {
          trackId: String(st.trackId || '').slice(0, 10),
          stateTitle: st.title || '',
          barTitle: text('player-title') || text('bar-title') || '',
          homeTitle: text('home-now-title'),
          barTitle: text('track-title'),
          queueIndex: null,
          queueTitle: text('queue-now-title'),
          playing: !!st.playing
        };
      }).catch(() => null);
      samples.push({ t: Math.round((Date.now() - started) / 100) / 10, ...snap });
      await wait(1000);
    }

    // 只打印"变化点"，避免几十行噪声
    const changes = [];
    let prev = null;
    for (const s of samples) {
      if (!s) continue;
      const key = s.trackId + '|' + s.playing;
      if (key !== prev) { changes.push(s); prev = key; }
    }
    console.log('\n快照变化点（秒 / 曲目 / 显示标题 / 是否在播 / 位置）:');
    for (const c of changes) console.log(`  ${String(c.t).padStart(5)}s  ${c.trackId || '-'}  底栏=[${c.barTitle}]  state=[${c.stateTitle}]  playing=${c.playing}`);

    const final = samples[samples.length - 1] || {};
    console.log('\n结束快照:', JSON.stringify(final));
    console.log('出现过的曲目:', JSON.stringify([...new Set(samples.filter(Boolean).map(s => s.trackId))]));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
