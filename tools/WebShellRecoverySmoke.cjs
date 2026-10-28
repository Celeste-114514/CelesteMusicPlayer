// 恢复卡冒烟：一套壳子两种说法，别把两件事混在一起。
//   mode=crash   这次浏览时页面崩了：宿主已自动重载，卡上给重载 / 安全模式 / 知道了；
//   mode=unclean 上次没正常退出：启动加载走完才推，只告知（知道了 + 诊断入口）。
// 另外守住三条底线：宿主不推就不显示、外观必须取外观设置的变量、安全模式压得住主题推送。
const assert = require('node:assert/strict'), path = require('node:path'), { pathToFileURL } = require('node:url');
const os = require('node:os');
const SHOTS = process.env.SMOKE_SHOTS === '1';
let chromium; try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const SNAPSHOT = {
  revision: 4,
  groups: [{ id: 'about', label: '关于', description: '版本、更新与运行环境', icon: 'settings' }],
  defs: [],
  values: {},
  accounts: { lastfm: { configured: false, username: '', hasSession: false }, lastfmApiKey: false, discogsToken: false, discogsConsumerKey: false, discogsConsumerSecret: false, fanartTvApiKey: false, webdavPassword: false },
  hotkeys: [],
  about: { version: '26.10.28.0', variant: 'fd', configDirectory: 'C:\\Users\\MSI\\AppData', settingsFile: 'app-settings.json', recovered: false }
};

const BASE = {
  ok: true, show: true, safeMode: false,
  unclean: true, crashes: 3,
  lastCrashUtc: '2026-10-28T10:00:00+08:00', lastCrashVersion: '26.10.27.0',
  appVersion: '26.10.28.0', settingsRecovered: false,
  logsDirectory: 'C:/Users/MSI/AppData/Local/CelesteMusicPlayer/logs',
  diagnosticsDirectory: 'C:/Users/MSI/AppData/Local/CelesteMusicPlayer/diagnostics'
};

const push = data => ({ kind: 'celeste.recovery', version: 1, data });
// ① 页面崩溃（用户这次遇到的问题）：宿主已自动重载，sessionCrashes 是本次运行第几次
const CRASH1 = push({ ...BASE, mode: 'crash', sessionCrashes: 1, lastStartText: '' });
const CRASH3 = push({ ...BASE, mode: 'crash', sessionCrashes: 3, lastStartText: '' });
// ② 上次没正常退出：等启动加载走完才推，带上次启动时间
const UNCLEAN = push({ ...BASE, mode: 'unclean', lastStartText: '2026-10-28 18:41:07' });

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } }), errors = [];
    page.on('pageerror', error => errors.push(error.message));

    await page.addInitScript(snapshot => {
      window.commands = [];
      window.chrome ||= {};
      window.chrome.webview = {
        addEventListener: (_, callback) => { window.deliver = data => callback({ data }); },
        postMessage: message => {
          window.commands.push(message);
          if (!message.requestId) return;
          let data = { message: 'ok' };
          if (message.command === 'appSettings') {
            if (message.operation === 'get') data = snapshot;
            if (message.operation === 'update') data = { stage: 'idle', status: 'up-to-date', current: '26.10.28.0' };
          }
          if (message.command === 'openExternal') data = { url: message.url };
          if (message.command === 'recovery' && message.action === 'export') {
            data = { ok: true, show: false, path: 'C:/diag/celeste-diagnostics-20261028.txt', fileName: 'celeste-diagnostics-20261028.txt' };
          }
          setTimeout(() => window.deliver({ kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }), 5);
        }
      };
    }, SNAPSHOT);

    await page.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href);
    await page.waitForFunction(() => window.CelesteShell && window.deliver);

    const state = () => page.evaluate(() => {
      const overlay = document.getElementById('recovery-overlay');
      const card = overlay.querySelector('.recovery-card');
      const primary = overlay.querySelector('.recovery-actions .primary');
      return {
        hidden: overlay.hidden,
        display: getComputedStyle(overlay).display,
        title: document.getElementById('recovery-title').textContent.trim(),
        detail: document.getElementById('recovery-detail').textContent.trim(),
        hint: document.getElementById('recovery-hint').textContent.trim(),
        facts: [...document.querySelectorAll('#recovery-facts li')].map(li => li.textContent.trim()),
        status: document.getElementById('recovery-status').textContent.trim(),
        actions: [...overlay.querySelectorAll('.recovery-actions [data-recovery]')].map(b => ({ action: b.dataset.recovery, text: b.textContent.trim(), cls: b.className })),
        links: [...overlay.querySelectorAll('.recovery-links [data-recovery]')].map(b => b.dataset.recovery),
        cardBg: getComputedStyle(card).backgroundColor,
        cardLine: getComputedStyle(card).borderTopColor,
        cardRadius: getComputedStyle(card).borderTopLeftRadius,
        primaryBg: primary ? getComputedStyle(primary).backgroundColor : '',
        primaryColor: primary ? getComputedStyle(primary).color : '',
        focused: (document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.recovery : '') || ''
      };
    });
    // 把变量名解析成实际颜色：变量可能是 hex 也可能是 color-mix，统一交给浏览器算。
    const resolve = () => page.evaluate(() => {
      const probe = document.createElement('div');
      probe.style.display = 'none';
      document.body.append(probe);
      const read = value => { probe.style.color = ''; probe.style.color = value; return getComputedStyle(probe).color; };
      const style = getComputedStyle(document.documentElement);
      const out = {
        menuBg: read(style.getPropertyValue('--menu-bg').trim()),
        accent: read(style.getPropertyValue('--accent').trim()),
        onAccent: read(style.getPropertyValue('--on-accent').trim()),
        line: read(style.getPropertyValue('--line').trim())
      };
      probe.remove();
      return out;
    });

    // ① 宿主没推：恢复卡必须完全不存在（连占位都不行）
    const idle = await state();
    assert.equal(idle.hidden, true, '没有恢复推送时不能显示恢复卡');
    assert.equal(idle.display, 'none', 'hidden 必须真的不占位');

    // ② 上次没正常退出：只在"加载完进主界面"之后推，说明上次什么时候、累计几次
    await page.evaluate(m => window.deliver(m), UNCLEAN);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    const unclean = await state();
    assert.ok(unclean.title.includes('上次没有正常退出'), '标题要说清是上次的事：' + unclean.title);
    assert.ok(unclean.detail.includes('2026-10-28 18:41:07'), '要说上次启动时间：' + unclean.detail);
    assert.ok(unclean.detail.includes('已经正常进到主界面'), '要说明这次已经进来了、不用紧张：' + unclean.detail);
    assert.ok(unclean.facts.some(t => t.includes('未正常退出累计') && t.includes('3 次')), '要带累计次数：' + unclean.facts.join(' | '));
    assert.ok(unclean.facts.some(t => t.includes('26.10.28.0')), '要带当前版本：' + unclean.facts.join(' | '));
    assert.deepEqual(unclean.actions.map(a => a.action), ['dismiss'], '上次退出只告知，不摆一堆选择');
    assert.deepEqual(unclean.links, ['export', 'openFolder']);
    assert.equal(unclean.focused, 'dismiss', '焦点应落在唯一的主按钮上');

    // ③ 外观对齐：卡片用外观设置里的 --menu-bg / --line / --accent / --on-accent
    const expected = await resolve();
    assert.equal(unclean.cardBg, expected.menuBg, '卡片底色必须等于 --menu-bg');
    assert.equal(unclean.cardLine, expected.line, '卡片描边必须等于 --line');
    assert.equal(unclean.primaryBg, expected.accent, '主按钮底色必须等于 --accent');
    assert.equal(unclean.primaryColor, expected.onAccent, '主按钮文字色必须等于 --on-accent');
    assert.equal(unclean.cardRadius, '20px', '圆角沿用 .ui-modal 的 20px');
    if (SHOTS) await page.screenshot({ path: path.join(os.tmpdir(), 'recovery-unclean.png') });

    // ③-1 导出诊断：走带回复的 request，回显文件名，且不关卡片（还想接着看别的）
    await page.click('.recovery-links [data-recovery="export"]');
    await page.waitForFunction(() => document.getElementById('recovery-status').textContent.includes('celeste-diagnostics'));
    const exported = await page.evaluate(() => ({
      status: document.getElementById('recovery-status').textContent.trim(),
      hidden: document.getElementById('recovery-overlay').hidden,
      request: window.commands.filter(m => m.command === 'recovery').at(-1)
    }));
    assert.ok(exported.status.includes('celeste-diagnostics-20261028.txt'), '要回显导出的文件名：' + exported.status);
    assert.equal(exported.hidden, false, '导出后不能自动关掉');
    assert.equal(exported.request.action, 'export');
    assert.ok(exported.request.requestId, '导出要带 requestId 才拿得到文件名');

    // ③-2 打开日志文件夹：单向命令即可
    await page.click('.recovery-links [data-recovery="openFolder"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'openFolder'));

    // ③-3 知道了：发 dismiss 并关掉，且带上 mode —— 宿主靠它区分"关掉告知"和"关掉崩溃救援"
    await page.click('.recovery-actions [data-recovery="dismiss"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'dismiss'));
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true);
    assert.equal(await page.evaluate(() => window.commands.filter(m => m.command === 'recovery' && m.action === 'dismiss').at(-1).mode), 'unclean', '关掉告知要带上 mode=unclean');

    // ④ 页面崩溃（第一次）：说清"已经替你重新载入"，重载放主位
    await page.evaluate(m => window.deliver(m), CRASH1);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    const crash1 = await state();
    assert.ok(crash1.title.includes('崩溃'), '标题要说清是这次崩了：' + crash1.title);
    assert.ok(crash1.detail.includes('已经替你重新载入'), '要说明已经自动重载过：' + crash1.detail);
    assert.ok(!crash1.detail.includes('第 1 次崩溃'), '只崩一次时不要报次数，免得吓人：' + crash1.detail);
    assert.deepEqual(crash1.actions.map(a => a.action), ['reload', 'safe', 'dismiss']);
    assert.equal(crash1.actions[0].cls, 'primary', '重载是主按钮');
    assert.equal(crash1.actions[2].text, '继续用这个界面');
    assert.ok(crash1.hint.includes('不动播放队列'), '要说清重载不影响播放：' + crash1.hint);

    // ④-1 点「重新载入界面」：发 reload 并关掉
    await page.click('.recovery-actions [data-recovery="reload"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'reload'));
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true);

    // ⑤ 反复崩：要报次数并指向安全模式
    await page.evaluate(m => window.deliver(m), CRASH3);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    const crash3 = await state();
    assert.ok(crash3.detail.includes('第 3 次崩溃'), '反复崩要说清第几次：' + crash3.detail);
    assert.ok(crash3.detail.includes('安全模式'), '反复崩要指向安全模式：' + crash3.detail);
    assert.ok(crash3.facts.some(t => t.includes('本次运行崩溃') && t.includes('3 次')), '要有本次崩溃次数：' + crash3.facts.join(' | '));

    // ⑤-1 安全模式：命令发出去、页面立刻降级，并且压得住随后的主题推送
    await page.click('.recovery-actions [data-recovery="safe"]');
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === true);
    const safe = await page.evaluate(() => {
      const style = getComputedStyle(document.documentElement);
      const first = { attr: document.documentElement.dataset.safeMode, bg: style.getPropertyValue('--bg-mode').trim(), motion: style.getPropertyValue('--motion-scale').trim() };
      // 模拟宿主随后推一次主题：行内变量写回来，安全模式必须仍然生效
      document.documentElement.style.setProperty('--bg-mode', 'video');
      document.documentElement.style.setProperty('--motion-scale', '1');
      const after = getComputedStyle(document.documentElement);
      return { first, bgAfter: after.getPropertyValue('--bg-mode').trim(), motionAfter: after.getPropertyValue('--motion-scale').trim(), sent: window.commands.filter(m => m.command === 'recovery').at(-1).action };
    });
    assert.equal(safe.sent, 'safe');
    assert.equal(safe.first.attr, 'true', '安全模式要挂 html[data-safe-mode]');
    assert.equal(safe.first.bg, 'solid', '安全模式下背景必须退成纯色：' + safe.first.bg);
    assert.ok(parseFloat(safe.first.motion) <= 0.01, '安全模式下动效要压到几乎不可见：' + safe.first.motion);
    assert.equal(safe.bgAfter, 'solid', '主题推送不能把视频背景放回来');
    assert.ok(parseFloat(safe.motionAfter) <= 0.01, '主题推送不能把动效放回来');
    if (SHOTS) await page.screenshot({ path: path.join(os.tmpdir(), 'recovery-crash-safe.png') });

    // ⑥ 「继续用这个界面」也要能关
    await page.evaluate(m => window.deliver(m), CRASH1);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    await page.click('.recovery-actions [data-recovery="dismiss"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'dismiss'));
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true);
    assert.equal(await page.evaluate(() => window.commands.filter(m => m.command === 'recovery' && m.action === 'dismiss').at(-1).mode), 'crash', '关掉崩溃卡要带上 mode=crash');

    // ⑦ show=false（宿主判定不该显示）：不能再冒出来
    await page.evaluate(m => window.deliver({ ...m, data: { ...m.data, show: false } }), CRASH1);
    await page.waitForTimeout(60);
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true, 'show=false 不能显示');

    assert.deepEqual(errors, []);
    console.log('PASS: 恢复卡——崩溃与上次异常分开说（崩溃给重载/安全模式、上次异常只告知）、不推不弹、外观取外观设置的变量、安全模式压得住主题推送。');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
