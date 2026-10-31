// W13 实播冒烟：真实进程 + CDP，播放两首曲子验证出声链路（DSP 批次收尾用）。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13DspBatchPlaybackSmoke.cjs <exe路径>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  if (!fs.existsSync(exe)) throw new Error('exe 不存在: ' + exe);
  const child = spawn(exe, ['--w7-background-test'], { detached: false, stdio: 'ignore' });
  let browser = null;
  for (let i = 0; i < 75; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(1000); }
  }
  if (!browser) { try { child.kill(); } catch { } throw new Error('CDP 连接失败'); }
  const out = { ok: false, tracks: [] };
  try {
    const page = browser.contexts()[0].pages()[0];
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 120000 });
    const lib = await page.evaluate(() => window.CelesteShell.getLibrary().slice(0, 2).map(t => ({ id: t.id, title: t.title })));
    out.picked = lib;
    if (lib.length < 2) throw new Error('曲库不足两首');

    // 第一首：playTracks 指定两首顺序播放
    await page.evaluate(ids => { try { CelesteShell.send('playAll', { trackIds: ids }); } catch (e) { throw new Error('playAll 失败: ' + e.message); } }, lib.map(t => t.id));

    // 等第一首进入播放
    const t1 = await page.waitForFunction(() => {
      const s = window.CelesteShell?.getState?.();
      return s && (s.isPlaying || s.playing) ? JSON.stringify({ title: s.title || s.currentTitle || '', pos: s.position }) : null;
    }, null, { timeout: 60000 });
    out.tracks.push(JSON.parse(t1));

    // 播 8 秒后切下一首
    await wait(8000);
    await page.evaluate(() => { try { CelesteShell.send('next', {}); } catch { } });
    const t2 = await page.waitForFunction(prev => {
      const s = window.CelesteShell?.getState?.();
      if (!s || !(s.isPlaying || s.playing)) return null;
      const title = s.title || s.currentTitle || '';
      return title && title !== prev ? JSON.stringify({ title, pos: s.position }) : null;
    }, out.tracks[0].title, { timeout: 60000 });
    out.tracks.push(JSON.parse(t2));

    out.ok = out.tracks.length === 2;
  } catch (e) {
    out.error = String(e && e.message || e);
  } finally {
    try { await page.evaluate(() => { try { CelesteShell.send('window', { action: 'close' }); } catch { } }); } catch { }
    await wait(3000);
    try { await browser.close(); } catch { }
    try { child.kill(); } catch { }
  }
  fs.writeFileSync(path.resolve(__dirname, '../_w13_dspbatch_playback.json'), JSON.stringify(out, null, 2));
  console.log(JSON.stringify(out, null, 2));
  process.exit(out.ok ? 0 : 1);
})();
