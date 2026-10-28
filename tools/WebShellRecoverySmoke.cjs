// 启动恢复页冒烟：
// ① 宿主不推就不显示（不能凭空拦人）；
// ② 收到 celeste.recovery 才画，且把「上次启动时间 / 累计次数 / 版本」说清楚；
// ③ 四个按钮各发各的命令，「导出诊断」走带回复的 request 并回显文件名；
// ④ 外观对齐：卡片底色/描边/主按钮配色必须等于外观设置那套变量，不另起一套颜色；
// ⑤ 安全模式：挂上 html[data-safe-mode]，背景退成纯色、动效压到最低，且能压住主题推送。
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

// 宿主在 ready 之后推的那一条（BuildRecoveryPush → RecoveryWire(true)）
const PUSH = {
  kind: 'celeste.recovery', version: 1,
  data: {
    ok: true, show: true, safeMode: false,
    unclean: true, marker: '{"startedUtc":"2026-10-28T18:41:07+08:00","pid":9128,"version":"26.10.27.0"}',
    crashes: 3, lastCrashUtc: '2026-10-28T10:00:00+08:00', lastCrashVersion: '26.10.27.0',
    appVersion: '26.10.28.0', settingsRecovered: false,
    lastStartText: '2026-10-28 18:41:07',
    logsDirectory: 'C:/Users/MSI/AppData/Local/CelesteMusicPlayer/logs',
    diagnosticsDirectory: 'C:/Users/MSI/AppData/Local/CelesteMusicPlayer/diagnostics'
  }
};

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
      const style = getComputedStyle(document.documentElement);
      const vars = name => style.getPropertyValue(name).trim();
      return {
        hidden: overlay.hidden,
        display: getComputedStyle(overlay).display,
        title: document.getElementById('recovery-title').textContent.trim(),
        detail: document.getElementById('recovery-detail').textContent.trim(),
        facts: [...document.querySelectorAll('#recovery-facts li')].map(li => li.textContent.trim()),
        status: document.getElementById('recovery-status').textContent.trim(),
        focused: (document.activeElement && document.activeElement.dataset ? document.activeElement.dataset.recovery : '') || '',
        buttons: [...overlay.querySelectorAll('[data-recovery]')].map(b => ({ action: b.dataset.recovery, text: b.textContent.trim(), cls: b.className })),
        cardBg: getComputedStyle(card).backgroundColor,
        cardLine: getComputedStyle(card).borderTopColor,
        cardRadius: getComputedStyle(card).borderTopLeftRadius,
        primaryBg: getComputedStyle(primary).backgroundColor,
        primaryColor: getComputedStyle(primary).color,
        vars: { menuBg: vars('--menu-bg'), line: vars('--line'), accent: vars('--accent'), onAccent: vars('--on-accent') }
      };
    });
    // 把变量名解析成实际颜色：变量可能是 hex 也可能是 color-mix，统一交给浏览器算。
    const resolve = async () => page.evaluate(() => {
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

    // ① 宿主没推：恢复页必须完全不存在（连占位都不行）
    const idle = await state();
    assert.equal(idle.hidden, true, '没有恢复推送时不能显示恢复页');
    assert.equal(idle.display, 'none', 'hidden 必须真的不占位');

    // ② 推送后出现，并把现场说清楚
    await page.evaluate(push => window.deliver(push), PUSH);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    const shown = await state();
    assert.ok(shown.title.includes('没有正常退出'), '标题要说清发生了什么：' + shown.title);
    assert.ok(shown.detail.includes('2026-10-28 18:41:07'), '要说上次启动时间：' + shown.detail);
    assert.ok(shown.facts.some(t => t.includes('未正常退出累计') && t.includes('3 次')), '要带累计次数：' + shown.facts.join(' | '));
    assert.ok(shown.facts.some(t => t.includes('26.10.28.0')), '要带当前版本：' + shown.facts.join(' | '));
    assert.equal(shown.focused, 'normal', '弹出时焦点应落在「正常启动」上，回车即可继续');

    // ④ 外观对齐：卡片用外观设置里的 --menu-bg / --line / --accent，不另起一套配色
    const expected = await resolve();
    assert.equal(shown.cardBg, expected.menuBg, '卡片底色必须等于 --menu-bg');
    assert.equal(shown.cardLine, expected.line, '卡片描边必须等于 --line');
    assert.equal(shown.primaryBg, expected.accent, '主按钮底色必须等于 --accent');
    assert.equal(shown.primaryColor, expected.onAccent, '主按钮文字色必须等于 --on-accent');
    assert.equal(shown.cardRadius, '20px', '圆角沿用 .ui-modal 的 20px');
    if (SHOTS) await page.screenshot({ path: path.join(os.tmpdir(), 'recovery-light.png') });

    // ③ 按钮齐全：四个动作 + 日志文件夹，各只有一份
    assert.deepEqual(shown.buttons.map(b => b.action), ['export', 'openFolder', 'reload', 'safe', 'normal']);
    assert.equal(shown.buttons.find(b => b.action === 'normal').cls.includes('primary'), true, '正常启动是主按钮');
    assert.equal(shown.buttons.find(b => b.action === 'safe').cls.includes('secondary'), true);

    // ③-1 导出诊断：走带回复的 request，回显文件名，且不关页面（用户可能还要选别的）
    await page.click('[data-recovery="export"]');
    await page.waitForFunction(() => document.getElementById('recovery-status').textContent.includes('celeste-diagnostics'));
    const exported = await page.evaluate(() => ({
      status: document.getElementById('recovery-status').textContent.trim(),
      hidden: document.getElementById('recovery-overlay').hidden,
      request: window.commands.filter(m => m.command === 'recovery').at(-1)
    }));
    assert.ok(exported.status.includes('celeste-diagnostics-20261028.txt'), '要回显导出的文件名：' + exported.status);
    assert.equal(exported.hidden, false, '导出后不能自动关掉恢复页');
    assert.equal(exported.request.action, 'export');
    assert.ok(exported.request.requestId, '导出要带 requestId 才拿得到文件名');

    // ③-2 打开日志文件夹：单向命令即可
    await page.click('[data-recovery="openFolder"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'openFolder'));

    // ⑤ 安全模式：命令发出去、页面立刻降级、并且压得住随后的主题推送
    await page.click('[data-recovery="safe"]');
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
    if (SHOTS) await page.screenshot({ path: path.join(os.tmpdir(), 'recovery-safe.png') });

    // ③-3 重载界面：用户明确要的按钮，必须发 reload 并关掉恢复页
    await page.reload();
    await page.waitForFunction(() => window.CelesteShell && window.deliver);
    await page.evaluate(push => window.deliver(push), PUSH);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    await page.click('[data-recovery="reload"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'reload'));
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true, '选完就关，不要继续挡着');

    // ③-4 正常启动：发 normal 并关闭
    await page.evaluate(push => window.deliver(push), PUSH);
    await page.waitForFunction(() => document.getElementById('recovery-overlay').hidden === false);
    await page.click('[data-recovery="normal"]');
    await page.waitForFunction(() => window.commands.some(m => m.command === 'recovery' && m.action === 'normal'));
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true);

    // ②-1 show=false（宿主判定不该显示）：不能再冒出来
    await page.evaluate(push => window.deliver({ ...push, data: { ...push.data, show: false } }), PUSH);
    await page.waitForTimeout(60);
    assert.equal(await page.evaluate(() => document.getElementById('recovery-overlay').hidden), true, 'show=false 不能显示');

    assert.deepEqual(errors, []);
    console.log('PASS: 启动恢复页——只在宿主推送时出现、现场信息说清楚、外观取外观设置的变量、导出/日志/重载/安全/正常五个入口各发各的命令、安全模式压得住主题推送。');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
