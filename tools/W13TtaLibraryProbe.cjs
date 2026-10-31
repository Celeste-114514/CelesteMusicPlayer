// TTA 入曲库验证探针（2026-10-31）。
//
// 背景：TagLib 没有 TTA 容器解析器，CreatePlaylistItemFromPath 以前对 .tta 直接抛异常，
// 曲库扫描把整首丢掉（100 个素材只进 90 个）。修完后这里应该看到 100/100，
// 其中 10 个 .tta 的时长 = 0.25 秒（TTA 文件头算出，不解码）。
//
// 只读：不播放、不入队、不改设置。素材目录由 LibraryWatchFolders 提前配好。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13TtaLibraryProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_tta_library_out.json'));
const MARK = '_w13tagfixtures';

function wait(ms) { return new Promise(r => setTimeout(r, ms)); }

(async () => {
  if (!fs.existsSync(exe)) throw new Error('exe 不存在: ' + exe);
  const child = spawn(exe, ['--w7-background-test'], { detached: false, stdio: 'ignore' });

  let browser = null;
  for (let i = 0; i < 90; i++) {
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
    if (!page) throw new Error('未找到主网页');

    // 等 100 个素材全部入库
    const ready = await page.waitForFunction((mark) => {
      const lib = (window.CelesteShell && window.CelesteShell.getLibrary && window.CelesteShell.getLibrary()) || [];
      const n = lib.filter(t => String(t.sortPath || '').toLowerCase().includes(mark)).length;
      return n >= 100 ? n : false;
    }, MARK, { timeout: 300000 }).catch(() => null);
    const scanned = ready ? await ready.jsonValue() : -1;

    const out = await page.evaluate((mark) => {
      const base = (p) => String(p || '').split(/[\\/]/).pop();
      const lib = (window.CelesteShell.getLibrary() || [])
        .filter(t => String(t.sortPath || '').toLowerCase().includes(mark))
        .sort((a, b) => String(a.sortPath).localeCompare(String(b.sortPath)));
      const rows = lib.map(t => ({
        file: base(t.sortPath),
        ext: (String(base(t.sortPath)).match(/\.[^.]+$/) || [''])[0].toLowerCase(),
        title: t.title, artist: t.artist, album: t.album, albumArtist: t.albumArtist,
        genre: t.genre, year: t.year, track: t.track, disc: t.disc,
        duration: Math.round((t.duration || 0) * 1000) / 1000,
      }));
      const byExt = {};
      for (const r of rows) byExt[r.ext] = (byExt[r.ext] || 0) + 1;
      return { count: rows.length, byExt, tta: rows.filter(r => r.ext === '.tta'), all: rows };
    }, MARK);

    fs.writeFileSync(OUT, JSON.stringify({ scanned, ...out }, null, 2), 'utf8');
    console.log(JSON.stringify({
      exe, out: OUT, scanned, count: out.count, byExt: out.byExt,
      tta: out.tta.map(r => ({ file: r.file, title: r.title, artist: r.artist, album: r.album, dur: r.duration }))
    }, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
