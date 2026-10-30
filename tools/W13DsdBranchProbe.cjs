// W13 DSD 分支探针：播一首 .dsf/.dff，验证会话返回的 DsdDirectOut / DsdTranscoded 分支还对。
// 判据看日志：DoP 直出 → "DSD DoP 直出"（界面文案）；回退 PCM → "DSD 已转码 PCM 输出"。
// 会出声（4 秒）。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13DsdBranchProbe.cjs <exe>
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
      const lib = CelesteShell.getLibrary();
      const first = lib[0] || {};
      // 路径字段名不一定叫 filePath：挑一个"看起来是磁盘路径"的字符串字段，别写死。
      const pathKey = Object.keys(first).find(k => typeof first[k] === 'string' && /[\\/]/.test(first[k]) && /\.[a-z0-9]{2,5}$/i.test(first[k])) || 'filePath';
      const local = lib.filter(t => t && !t.isRemote && typeof t[pathKey] === 'string' && t[pathKey]);
      const dsd = local.filter(t => /\.(dsf|dff)$/i.test(t[pathKey]));
      return {
        pathKey,
        keys: Object.keys(first).slice(0, 24),
        total: lib.length,
        localCount: local.length,
        count: dsd.length,
        first: dsd[0] ? { id: dsd[0].id, title: dsd[0].title, path: dsd[0][pathKey] } : null
      };
    });
    console.log('路径字段=', picked.pathKey, '曲库=', picked.total, '本地=', picked.localCount);
    console.log('DSD 曲目数=', picked.count, '选中=', JSON.stringify(picked.first));
    if (!picked.first) { console.log('库里没有 DSD 文件，跳过'); console.log('曲目字段:', JSON.stringify(picked.keys)); return; }

    await page.evaluate(id => CelesteShell.send('playTrack', { trackId: id }), picked.first.id);
    await wait(6000);
    const np = await page.evaluate(async () => await CelesteTransport.request('nowPlaying', {}));
    console.log('nowPlaying=', JSON.stringify({ trackId: (np.trackId || '').slice(0, 12), title: np.title, playing: np.playing }));
    // 不收尾也行：进程马上就被 kill。早期版本这里调 transport stop，会撞上"未知的播放栏操作"。
    await wait(800);
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
