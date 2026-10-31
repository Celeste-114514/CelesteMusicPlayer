// 桌面歌词探针：验证「开关两次 + 页面真的加载 + 开着歌词关主程序能干净退出」。
//
// 背景：桌面歌词用 Windows.UI.Composition 的合成宿主，必须跑在一条自带系统级
// DispatcherQueue 的线程上（见 WebDesktopLyricsOverlay 顶部注释）。这条链路一旦回退，
// 表现是点开关弹「拒绝访问 / The caller must initialize DispatcherQueue」，只能靠真机跑验证。
//
// 用法：node tools/W13DesktopLyricsProbe.cjs <exe 路径>
// 依赖：playwright（NODE_PATH 指向已装 playwright 的 node_modules）
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
if (!exe || !fs.existsSync(exe)) {
  console.error('用法: node tools/W13DesktopLyricsProbe.cjs <CelesteMusicPlayer.exe>');
  process.exit(2);
}

const logPath = path.join(process.env.LOCALAPPDATA || '', 'CelesteMusicPlayer', 'logs', 'app',
  new Date().toISOString().slice(0, 10) + '.log');
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  const before = fs.existsSync(logPath) ? fs.readFileSync(logPath, 'utf8').split('\n').length : 0;
  const child = spawn(exe, ['--w7-background-test'], { stdio: ['ignore', 'ignore', 'pipe'] });
  let stderr = '';
  child.stderr.on('data', d => { stderr += d.toString(); });

  const out = { rounds: [], openedTwice: false, readyTwice: false, lyricTextShown: false, noCompositorError: true, closedCleanly: false };
  let browser = null;

  try {
    for (let i = 0; i < 60 && !browser; i++) {
      try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); } catch { await wait(1000); }
    }
    if (!browser) throw new Error('CDP 连接失败');

    let page = null;
    for (let i = 0; i < 90 && !page; i++) {
      page = browser.contexts()[0]?.pages().find(p => p.url().includes('celeste.internal'));
      if (!page) await wait(1000);
    }
    if (!page) throw new Error('找不到主页面');
    await page.waitForFunction(() => !!window.CelesteShell, null, { timeout: 60000 });
    await wait(1500);

    const toggle = () => page.evaluate(() => window.CelesteShell.send('transport', { action: 'desktopLyrics' }));
    const lyricPage = () => browser.contexts()[0]?.pages().find(p => p.url().includes('celeste-lyrics.internal'));

    // CDP 里第二个 WebView 的页面列表会滞后出现/消失，所以都要轮询等，不能睡一觉就断言。
    const waitForLyricPage = async (want, timeoutMs) => {
      const deadline = Date.now() + timeoutMs;
      while (Date.now() < deadline) {
        if (!!lyricPage() === want) return true;
        await wait(250);
      }
      return false;
    };

    for (let round = 1; round <= 2; round++) {
      await toggle();
      const appeared = await waitForLyricPage(true, 10000);
      let text = '';
      if (appeared) {
        try { text = await lyricPage().evaluate(() => document.querySelector('#current')?.textContent ?? ''); } catch { text = ''; }
      }
      out.rounds.push({ round, 歌词页出现: appeared, 当前行: text.slice(0, 30) });
      if (text.trim().length > 0) out.lyricTextShown = true;

      await toggle();
      out.rounds.push({ round, 关闭后歌词页移除: await waitForLyricPage(false, 8000) });
    }

    // 开着歌词关主程序：验证专用线程不会把进程吊住
    await toggle();
    await waitForLyricPage(true, 10000);
    await page.evaluate(() => { try { CelesteShell.send('window', { action: 'close' }); } catch { } });
    for (let i = 0; i < 40; i++) {
      if (child.exitCode !== null) { out.closedCleanly = child.exitCode === 0; break; }
      await wait(250);
    }
  } catch (caught) {
    out.error = String(caught && caught.message || caught);
  } finally {
    try { await browser?.close(); } catch { }
    if (child.exitCode === null) { try { child.kill(); } catch { } }
  }

  const lines = fs.existsSync(logPath) ? fs.readFileSync(logPath, 'utf8').split('\n').slice(Math.max(0, before - 1)) : [];
  const hits = lines.filter(l => /桌面歌词|Compositor|DispatcherQueue|DesktopLyrics\.Tick/.test(l));
  out.openedTwice = hits.filter(l => l.includes('裸窗口已创建')).length >= 2;
  out.readyTwice = hits.filter(l => l.includes('页面就绪')).length >= 2;
  out.noCompositorError = !hits.some(l => /Compositor|DispatcherQueue must|拒绝访问/.test(l));
  out.exitCode = child.exitCode;
  if (stderr) out.stderr = stderr.slice(-800);

  console.log(JSON.stringify(out, null, 1));

  const failed = out.error || !out.openedTwice || !out.readyTwice || !out.lyricTextShown || !out.noCompositorError || !out.closedCleanly;
  process.exit(failed ? 1 : 0);
})();
