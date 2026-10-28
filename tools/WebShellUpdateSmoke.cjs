// 设置页「关于」更新检查冒烟：
// ① 四种结论分开显示（有新版 / 没有适配包 / 检查失败 / 已是最新），断网不能被说成"已是最新"；
// ② 失败时保留上次发现的新版本；
// ③ 下载入口只建一份、不随重复检查叠加；
// ④ 外链走宿主 openExternal，不在 WebView 里直接导航。
const assert = require('node:assert/strict'), path = require('node:path'), { pathToFileURL } = require('node:url');
// SMOKE_SHOTS=1 时把各状态截图存到临时目录，便于人工过一眼排版。
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
  about: { version: '26.10.20.0', variant: 'fd', configDirectory: 'C:\\Users\\MSI\\AppData', settingsFile: 'app-settings.json', recovered: false }
};

// 每次「检查更新」返回什么，由测试逐段改写
const REPLIES = {
  available: {
    status: 'available', current: '26.10.20.0', latestTag: 'v26.11.1', latest: 'v26.11.1', hasUpdate: true, canInstall: true,
    packageKind: '框架依赖版', downloadUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/download/v26.11.1/CelesteMusicPlayer-Setup-26.11.1.0.exe',
    releaseUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/tag/v26.11.1', checksum: 'verified',
    errorCategory: '', error: '', prereleaseIgnored: false, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:04:11'
  },
  unsupported: {
    status: 'unsupported', current: '26.10.20.0', latestTag: 'v26.11.1', latest: 'v26.11.1', hasUpdate: true, canInstall: false,
    packageKind: '框架依赖版', downloadUrl: '', releaseUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/tag/v26.11.1', checksum: 'unpublished',
    errorCategory: '', error: '', prereleaseIgnored: false, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:05:02'
  },
  failed: {
    status: 'failed', current: '26.10.20.0', latestTag: '', latest: '', hasUpdate: false, canInstall: false,
    packageKind: '', downloadUrl: '', releaseUrl: '', checksum: '',
    errorCategory: 'network', error: '连不上 GitHub（No such host is known），请检查网络或代理。',
    prereleaseIgnored: false, stale: true, lastKnownTag: 'v26.11.1',
    lastKnownDownloadUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/download/v26.11.1/CelesteMusicPlayer-Setup-26.11.1.0.exe',
    lastKnownReleaseUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/tag/v26.11.1', checkedAt: '21:06:30'
  },
  current: {
    status: 'up-to-date', current: '26.10.20.0', latestTag: 'v26.10.20', latest: '', hasUpdate: false, canInstall: false,
    packageKind: '', downloadUrl: '', releaseUrl: '', checksum: '',
    errorCategory: '', error: '', prereleaseIgnored: false, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:07:00'
  },
  prerelease: {
    status: 'up-to-date', current: '26.10.20.0', latestTag: 'v26.12.0-beta.1', latest: '', hasUpdate: false, canInstall: false,
    packageKind: '', downloadUrl: '', releaseUrl: '', checksum: '',
    errorCategory: '', error: '', prereleaseIgnored: true, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:08:00'
  }
};

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 900 } }), errors = [];
    page.on('pageerror', error => errors.push(error.message));

    await page.addInitScript(snapshot => {
      window.commands = [];
      window.updateReply = null;
      window.chrome ||= {};
      window.chrome.webview = {
        addEventListener: (_, callback) => { window.deliver = data => callback({ data }); },
        postMessage: message => {
          window.commands.push(message);
          if (!message.requestId) return;
          let data = { message: 'ok' };
          if (message.command === 'appSettings') {
            if (message.operation === 'get') data = snapshot;
            if (message.operation === 'update') data = window.updateReply;
          }
          if (message.command === 'openExternal') data = { url: message.url };
          setTimeout(() => window.deliver({ kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data }), 5);
        }
      };
    }, SNAPSHOT);

    await page.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href);
    await page.evaluate(() => window.CelesteShell.navigate('settings'));
    await page.waitForSelector('.settings-nav-item[data-group="about"]');
    await page.click('.settings-nav-item[data-group="about"]');
    await page.waitForSelector('#about-check');

    const runCheck = async key => {
      await page.evaluate(reply => { window.updateReply = reply; }, REPLIES[key]);
      await page.click('#about-check');
      await page.waitForFunction(expected => {
        const note = document.getElementById('about-update-note');
        return note && note.textContent.includes(expected);
      }, key === 'available' ? '有新版本' : key === 'unsupported' ? '没有适配' : key === 'failed' ? '检查失败' : '已经是最新版');
      if (SHOTS) await page.screenshot({ path: path.join(os.tmpdir(), 'update-' + key + '.png') });
      return page.evaluate(() => {
        const note = document.getElementById('about-update-note');
        const download = document.getElementById('about-download');
        return {
          text: note.textContent,
          color: note.style.color,
          downloadHidden: download.hidden,
          downloadDisplay: getComputedStyle(download).display,
          downloadText: download.textContent,
          downloadUrl: download.dataset.url || '',
          downloadCount: document.querySelectorAll('#about-download').length
        };
      });
    };

    // ① 有新版：说清版本、包型、校验结论，并给出下载入口
    const available = await runCheck('available');
    assert.ok(available.text.includes('v26.11.1'), '应显示新版本号：' + available.text);
    assert.ok(available.text.includes('框架依赖版'), '应显示包型：' + available.text);
    assert.ok(available.text.includes('哈希与发布清单一致'), '应说明校验通过：' + available.text);
    assert.equal(available.color, 'var(--warning)');
    assert.equal(available.downloadHidden, false);
    assert.notEqual(available.downloadDisplay, 'none', '有新版本时下载入口必须真的可见');
    assert.equal(available.downloadText, '打开下载页');
    assert.ok(available.downloadUrl.includes('CelesteMusicPlayer-Setup-26.11.1.0.exe'));

    // ② 点下载页：走宿主 openExternal，不在 WebView 里导航
    const hrefBefore = await page.evaluate(() => location.href);
    await page.click('#about-download');
    await page.waitForFunction(() => window.commands.some(message => message.command === 'openExternal'));
    const external = await page.evaluate(() => window.commands.filter(message => message.command === 'openExternal').at(-1));
    assert.equal(external.url, REPLIES.available.downloadUrl);
    assert.equal(await page.evaluate(() => location.href), hrefBefore, '打开外链不应改变网页地址');

    // ③ 检查失败：不能说"已是最新"，还要带出上次发现的新版本；下载入口不叠加
    const failed = await runCheck('failed');
    assert.ok(failed.text.includes('连不上 GitHub'), '应显示失败原因：' + failed.text);
    assert.ok(!failed.text.includes('最新版'), '失败时不能出现"已是最新"：' + failed.text);
    assert.ok(failed.text.includes('上次检查发现过 v26.11.1'), '失败时应保留上次发现的新版本：' + failed.text);
    assert.equal(failed.color, 'var(--danger)');
    assert.equal(failed.downloadCount, 1, '反复检查不能叠出多个下载入口');

    // ④ 有新版本但没有适配包：指向发布页手动下载
    const unsupported = await runCheck('unsupported');
    assert.ok(unsupported.text.includes('没有适配'), '应说明没有适配包：' + unsupported.text);
    assert.equal(unsupported.downloadText, '打开发布页');
    assert.ok(unsupported.downloadUrl.endsWith('/releases/tag/v26.11.1'), '应指向发布页：' + unsupported.downloadUrl);

    // ⑤ 已是最新：明确说没有更新，不显示下载入口
    const current = await runCheck('current');
    assert.ok(current.text.includes('已经是最新版'), '应显示已是最新：' + current.text);
    assert.equal(current.color, 'var(--success)');
    assert.equal(current.downloadHidden, true);
    assert.equal(current.downloadDisplay, 'none', '没有更新时下载入口必须真的隐藏（hidden 不能被 display 规则盖掉）');
    assert.equal(current.downloadCount, 1);

    // ⑥ 只发现预览版：稳定通道不收，但要说出来
    const prerelease = await runCheck('prerelease');
    assert.ok(prerelease.text.includes('预览版 v26.12.0-beta.1'), '应提示发现了预览版：' + prerelease.text);
    assert.equal(prerelease.downloadHidden, true);

    // ⑦ 页面里任意 https 外链也交给宿主（不只关于页的按钮）
    await page.evaluate(() => {
      const anchor = document.createElement('a');
      anchor.id = 'smoke-external';
      anchor.href = 'https://github.com/Celeste-114514/CelesteMusicPlayer';
      anchor.textContent = 'GitHub';
      document.querySelector('.settings-card').append(anchor);
      window.commands.length = 0;
    });
    await page.click('#smoke-external');
    await page.waitForFunction(() => window.commands.some(message => message.command === 'openExternal'));
    const linkCommand = await page.evaluate(() => window.commands.find(message => message.command === 'openExternal'));
    assert.equal(linkCommand.url, 'https://github.com/Celeste-114514/CelesteMusicPlayer');
    assert.equal(await page.evaluate(() => location.href), hrefBefore, '普通外链也不应把外壳页面顶掉');

    assert.deepEqual(errors, []);
    console.log('PASS: 关于页四种更新结论分开显示、失败保留上次结果、下载入口不重复、外链统一走宿主 openExternal。');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
