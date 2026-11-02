// W13HotkeysProbe —— 验证网页层的三条快捷键真的生效（Space / ← → / 滚轮音量）
// 用法：NODE_PATH=<playwright-core 所在 node_modules> node tools/W13HotkeysProbe.cjs "<exe 绝对路径>"
// 走 --w7-background-test：临时 WebView2 配置目录，不碰用户存档，也不显示窗口。
// 判据用「拦截 postMessage 看命令有没有发出去」，不依赖真的有歌在放。
const { spawn } = require('child_process');
const { chromium } = require('playwright-core');

(async () => {
  const child = spawn(process.argv[2], ['--w7-background-test'], { stdio: 'ignore' });
  let browser;
  try {
    for (let i = 0; i < 60; i++) {
      try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; }
      catch { await new Promise(r => setTimeout(r, 500)); }
    }
    if (!browser) throw new Error('连不上 9347 调试端口');
    const page = browser.contexts()[0].pages()[0];
    await page.waitForFunction(() => window.CelesteShell);

    const result = await page.evaluate(async () => {
      const sleep = ms => new Promise(r => setTimeout(r, ms));
      const shell = window.CelesteShell;
      const sent = [];
      const bridge = window.chrome?.webview;
      if (!bridge) throw new Error('没有 chrome.webview，不是真实宿主');
      const original = bridge.postMessage.bind(bridge);
      bridge.postMessage = message => {
        if (message && message.command) sent.push(message);
        return original(message);
      };
      const commands = () => sent.map(m => m.command);
      const fireWheel = (target, deltaY) =>
        target.dispatchEvent(new WheelEvent('wheel', { deltaY, bubbles: true, cancelable: true }));
      const fireKey = (target, key, code) =>
        target.dispatchEvent(new KeyboardEvent('keydown', { key, code, bubbles: true, cancelable: true }));

      const start = shell.getState();
      const scroller = document.querySelector('main.main');
      const bar = document.querySelector('#volume-group') || document.querySelector('.player-bar') || document.body;
      const report = { start: { volume: start.volume, position: start.position } };

      // ① 列表还能往下滚时，滚轮必须让给页面（不能偷去调音量）
      if (scroller) {
        scroller.scrollTop = 0;
        sent.length = 0;
        fireWheel(scroller, 100);
        await sleep(120);
        report.wheelScrollable = commands();
      }

      // ② 列表已经在最顶部，再往上滚 = 调音量（+2）
      if (scroller) {
        scroller.scrollTop = 0;
        sent.length = 0;
        fireWheel(scroller, -100);
        await sleep(150);
        report.wheelAtTop = sent.filter(m => m.command === 'setVolume').map(m => m.volume);
      }

      // ③ 底栏这种不滚动的地方，滚轮直接调音量（往下滚 = 减）
      sent.length = 0;
      fireWheel(bar, 100);
      await sleep(150);
      report.wheelOnBar = sent.filter(m => m.command === 'setVolume').map(m => m.volume);

      // ④ 空格 = 播放/暂停
      sent.length = 0;
      fireKey(document.body, ' ', 'Space');
      await sleep(150);
      report.space = commands();

      // ⑤ 右方向键 = 快进
      sent.length = 0;
      fireKey(document.body, 'ArrowRight', 'ArrowRight');
      await sleep(150);
      report.arrowRight = sent.filter(m => m.command === 'seek').map(m => m.seconds);

      // ⑥ 守卫：输入框里按空格不触发播放
      const input = document.createElement('input');
      input.type = 'text';
      document.body.append(input);
      input.focus();
      sent.length = 0;
      fireKey(input, ' ', 'Space');
      await sleep(120);
      report.spaceInInput = commands();
      input.remove();

      bridge.postMessage = original;

      // 收尾：把音量与进度恢复原样，别把用户设置改了
      if (shell.getState().volume !== start.volume) shell.send('setVolume', { volume: start.volume });
      if (start.position >= 0 && shell.getState().position !== start.position) shell.send('seek', { seconds: start.position });
      return report;
    });

    const problems = [];
    if (result.wheelScrollable && result.wheelScrollable.includes('setVolume')) problems.push('列表还能滚时滚轮被抢去调音量了');
    if (!(result.wheelAtTop?.length === 1 && result.wheelAtTop[0] > 0)) problems.push('列表滚到头后滚轮没有调音量');
    if (!(result.wheelOnBar?.length === 1)) problems.push('底栏滚轮没有调音量');
    if (!result.space.includes('togglePlay')) problems.push('空格没有发出 togglePlay');
    if (!(result.arrowRight?.length === 1 && result.arrowRight[0] > 0)) problems.push('右方向键没有发出 seek');
    if (result.spaceInInput.length) problems.push('输入框里的空格被拦了：' + result.spaceInInput.join(','));

    console.log(JSON.stringify(result, null, 2));
    if (problems.length) { console.error('FAIL: ' + problems.join(' / ')); process.exitCode = 1; }
    else console.log('HOTKEYS=PASS');
  } finally {
    await browser?.close();
    child.kill();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
