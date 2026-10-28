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
    stage: 'available', status: 'available', current: '26.10.20.0', latestTag: 'v26.11.1', latest: 'v26.11.1', hasUpdate: true,
    canInstall: true, canDownload: true, canCancel: false, canInstallNow: false,
    packageKind: '框架依赖版', downloadUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/download/v26.11.1/CelesteMusicPlayer-Setup-26.11.1.0.exe',
    releaseUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/tag/v26.11.1', checksum: 'verified',
    errorCategory: '', error: '', prereleaseIgnored: false, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:04:11'
  },
  unsupported: {
    stage: 'available', status: 'unsupported', current: '26.10.20.0', latestTag: 'v26.11.1', latest: 'v26.11.1', hasUpdate: true, canInstall: false,
    packageKind: '框架依赖版', downloadUrl: '', releaseUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/tag/v26.11.1', checksum: 'unpublished',
    errorCategory: '', error: '', prereleaseIgnored: false, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:05:02'
  },
  failed: {
    stage: 'failed', status: 'failed', current: '26.10.20.0', latestTag: '', latest: '', hasUpdate: false, canInstall: false,
    packageKind: '', downloadUrl: '', releaseUrl: '', checksum: '',
    errorCategory: 'network', error: '连不上 GitHub（No such host is known），请检查网络或代理。',
    prereleaseIgnored: false, stale: true, lastKnownTag: 'v26.11.1',
    lastKnownDownloadUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/download/v26.11.1/CelesteMusicPlayer-Setup-26.11.1.0.exe',
    lastKnownReleaseUrl: 'https://github.com/Celeste-114514/CelesteMusicPlayer/releases/tag/v26.11.1', checkedAt: '21:06:30'
  },
  current: {
    stage: 'idle', status: 'up-to-date', current: '26.10.20.0', latestTag: 'v26.10.20', latest: '', hasUpdate: false, canInstall: false,
    packageKind: '', downloadUrl: '', releaseUrl: '', checksum: '',
    errorCategory: '', error: '', prereleaseIgnored: false, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:07:00'
  },
  prerelease: {
    stage: 'idle', status: 'up-to-date', current: '26.10.20.0', latestTag: 'v26.12.0-beta.1', latest: '', hasUpdate: false, canInstall: false,
    packageKind: '', downloadUrl: '', releaseUrl: '', checksum: '',
    errorCategory: '', error: '', prereleaseIgnored: true, stale: false, lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:08:00'
  },
  // 下载 / 安装这一路：状态由 stage 决定，不再靠「上次点过什么」猜。
  downloading: {
    stage: 'downloading', status: 'available', current: '26.10.20.0', latest: 'v26.11.1', latestTag: 'v26.11.1', hasUpdate: true,
    canDownload: false, canCancel: true, canInstallNow: false, packageKind: '框架依赖版',
    downloadUrl: '', releaseUrl: '', checksum: 'verified', errorCategory: '', error: '', prereleaseIgnored: false, stale: false,
    lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:10:00',
    bytesReceived: 1048576, bytesTotal: 4194304, transactionId: '20261028-211000-4242', installDirectory: ''
  },
  ready: {
    stage: 'ready', status: 'available', current: '26.10.20.0', latest: 'v26.11.1', latestTag: 'v26.11.1', hasUpdate: true,
    canDownload: false, canCancel: false, canInstallNow: true, packageKind: '框架依赖版',
    downloadUrl: '', releaseUrl: '', checksum: 'verified', errorCategory: '', error: '', prereleaseIgnored: false, stale: false,
    lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:11:00',
    bytesReceived: 4194304, bytesTotal: 4194304, transactionId: '20261028-211000-4242', installDirectory: 'C:\\Users\\MSI\\AppData\\Local\\Programs\\CelesteMusicPlayer'
  },
  installing: {
    stage: 'installing', status: 'available', current: '26.10.20.0', latest: 'v26.11.1', latestTag: 'v26.11.1', hasUpdate: true,
    canDownload: false, canCancel: false, canInstallNow: false, packageKind: '框架依赖版',
    downloadUrl: '', releaseUrl: '', checksum: 'verified', errorCategory: '', error: '', prereleaseIgnored: false, stale: false,
    lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:12:00',
    bytesReceived: 4194304, bytesTotal: 4194304, transactionId: '20261028-211000-4242', installDirectory: ''
  },
  cancelled: {
    stage: 'cancelled', status: 'available', current: '26.10.20.0', latest: 'v26.11.1', latestTag: 'v26.11.1', hasUpdate: true,
    canDownload: false, canCancel: false, canInstallNow: false, packageKind: '框架依赖版',
    downloadUrl: '', releaseUrl: '', checksum: 'verified', errorCategory: '', error: '', prereleaseIgnored: false, stale: false,
    lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:13:00',
    bytesReceived: 0, bytesTotal: 0, transactionId: '', installDirectory: ''
  },
  // 下载这一侧失败：不能写成"检查更新失败"，否则用户会去折腾网络。
  installFailed: {
    stage: 'failed', status: 'failed', current: '26.10.20.0', latest: 'v26.11.1', latestTag: 'v26.11.1', hasUpdate: true,
    canDownload: false, canCancel: false, canInstallNow: false, packageKind: '框架依赖版',
    downloadUrl: '', releaseUrl: '', checksum: 'verified',
    errorCategory: 'hash-mismatch', error: '安装包哈希与官方发布不一致，已拒绝安装并删除文件。',
    prereleaseIgnored: false, stale: false,
    lastKnownTag: '', lastKnownDownloadUrl: '', lastKnownReleaseUrl: '', checkedAt: '21:14:00',
    bytesReceived: 0, bytesTotal: 0, transactionId: '', installDirectory: ''
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
      window.updateReplies = {};   // 按 action 分开回：check / download / cancel / install / status
      window.chrome ||= {};
      window.chrome.webview = {
        addEventListener: (_, callback) => { window.deliver = data => callback({ data }); },
        postMessage: message => {
          window.commands.push(message);
          if (!message.requestId) return;
          let data = { message: 'ok' };
          if (message.command === 'appSettings') {
            if (message.operation === 'get') data = snapshot;
            if (message.operation === 'update') {
              const action = message.action || 'check';
              const replies = window.updateReplies || {};
              data = action in replies
                ? replies[action]
                : (window.updateReply || { stage: 'idle', status: 'up-to-date', current: '26.10.20.0' });
            }
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

    // 读一次卡片状态：四个按钮的显隐/文案 + 进度条 + 提示文字。
    const readCard = () => page.evaluate(() => {
      const pick = id => {
        const el = document.getElementById(id);
        return { hidden: !el || el.hidden, display: el ? getComputedStyle(el).display : 'none', text: el ? el.textContent : '' };
      };
      const note = document.getElementById('about-update-note');
      const progress = document.getElementById('about-progress');
      const fill = progress?.querySelector('i');
      return {
        text: note.textContent,
        color: note.style.color,
        check: pick('about-check'),
        install: pick('about-install'),
        cancel: pick('about-cancel'),
        apply: pick('about-apply'),
        download: pick('about-download'),
        progressHidden: !progress || progress.hidden,
        progressDisplay: progress ? getComputedStyle(progress).display : 'none',
        progressWidth: fill ? fill.style.width : '',
        progressIndeterminate: progress ? progress.classList.contains('indeterminate') : false,
        downloadUrl: document.getElementById('about-download')?.dataset.url || '',
        counts: ['about-check', 'about-install', 'about-cancel', 'about-apply', 'about-download']
          .map(id => document.querySelectorAll('#' + id).length)
      };
    });

    const runCheck = async (key, expect) => {
      await page.evaluate(reply => { window.updateReplies.check = reply; }, REPLIES[key]);
      await page.click('#about-check');
      await page.waitForFunction(expected => {
        const note = document.getElementById('about-update-note');
        return note && note.textContent.includes(expected);
      }, expect);
      if (SHOTS) await page.screenshot({ path: path.join(os.tmpdir(), 'update-' + key + '.png') });
      return readCard();
    };

    // 宿主推送一条状态（下载进度、状态切换都走这条路）。
    const pushStatus = async status => {
      await page.evaluate(payload => window.deliver({ kind: 'celeste.update', version: 1, status: payload }), status);
    };

    // ① 有新版且哈希可校验：给的是真下载按钮，不再把人往外链赶
    const available = await runCheck('available', '有新版本');
    assert.ok(available.text.includes('v26.11.1'), '应显示新版本号：' + available.text);
    assert.ok(available.text.includes('框架依赖版'), '应显示包型：' + available.text);
    assert.ok(available.text.includes('哈希与发布清单一致'), '应说明校验通过：' + available.text);
    assert.equal(available.color, 'var(--warning)');
    assert.equal(available.install.hidden, false, '能校验的包应该直接给下载按钮');
    assert.equal(available.install.text, '下载更新包');
    assert.equal(available.download.hidden, true, '能自动下载时不该再把人赶去发布页');
    assert.equal(available.progressHidden, true, '没开始下载不该出现进度条');
    assert.equal(available.apply.hidden, true);
    assert.equal(available.cancel.hidden, true);

    // ② 点下载：发的是 download 动作（不是打开网页），期间进度只认宿主推送
    await page.evaluate(() => { window.commands.length = 0; });
    await page.evaluate(reply => { window.updateReplies.download = reply; }, REPLIES.downloading);
    await page.click('#about-install');
    await page.waitForFunction(() => document.getElementById('about-update-note').textContent.includes('正在下载'));
    const downloadCommand = await page.evaluate(() =>
      window.commands.filter(m => m.command === 'appSettings' && m.operation === 'update').at(-1));
    assert.equal(downloadCommand.action, 'download', '下载按钮必须真的走宿主下载，而不是打开网页');

    const downloading = await readCard();
    assert.ok(downloading.text.includes('25%'), '应按已收/总量算百分比：' + downloading.text);
    assert.ok(downloading.text.includes('1.0 MB'), '应显示已下载体积：' + downloading.text);
    assert.equal(downloading.progressHidden, false, '下载中必须显示进度条');
    assert.notEqual(downloading.progressDisplay, 'none');
    assert.equal(downloading.progressWidth, '25%');
    assert.equal(downloading.progressIndeterminate, false);
    assert.equal(downloading.cancel.hidden, false, '下载中必须能取消');
    assert.equal(downloading.install.hidden, true);

    // ③ 进度靠推送刷新（不是靠轮询/重发请求）
    await pushStatus({ ...REPLIES.downloading, bytesReceived: 3145728 });
    await page.waitForFunction(() => document.getElementById('about-update-note').textContent.includes('75%'));
    assert.equal((await readCard()).progressWidth, '75%');

    // 总量未知时不能画假百分比，走流动条纹
    await pushStatus({ ...REPLIES.downloading, bytesTotal: 0, bytesReceived: 3145728 });
    await page.waitForFunction(() => document.getElementById('about-progress').classList.contains('indeterminate'));
    const unknownTotal = await readCard();
    assert.ok(unknownTotal.text.includes('已下载'), '总量未知时应只说已下载体积：' + unknownTotal.text);
    assert.ok(!unknownTotal.text.includes('%'), '总量未知时不能编一个百分比：' + unknownTotal.text);

    // ④ 校验完：出现"立即安装并重启"，进度条收起
    await pushStatus(REPLIES.ready);
    await page.waitForFunction(() => !document.getElementById('about-apply').hidden);
    const ready = await readCard();
    assert.ok(ready.text.includes('已下载并校验'), '应说明已下载并校验：' + ready.text);
    assert.equal(ready.progressHidden, true, '校验完不该继续显示进度条');
    assert.equal(ready.cancel.hidden, true);
    assert.equal(ready.install.hidden, true);

    // ⑤ 点安装：交接成功就不该把按钮放开（主程序马上要退出）
    await page.evaluate(() => { window.commands.length = 0; });
    await page.evaluate(reply => { window.updateReplies.install = reply; }, REPLIES.installing);
    await page.click('#about-apply');
    await page.waitForFunction(() => document.getElementById('about-update-note').textContent.includes('正在交给更新助手'));
    const installCommand = await page.evaluate(() =>
      window.commands.filter(m => m.command === 'appSettings' && m.operation === 'update').at(-1));
    assert.equal(installCommand.action, 'install');
    assert.equal((await readCard()).apply.hidden, true, '已交接就不该还给用户一个能点的安装按钮');

    // ⑥ 取消下载：给出"重新下载更新"，而不是让人重新点检查更新
    await pushStatus(REPLIES.cancelled);
    await page.waitForFunction(() => document.getElementById('about-update-note').textContent.includes('下载已取消'));
    const cancelled = await readCard();
    assert.equal(cancelled.install.hidden, false, '取消后要能直接重下');
    assert.equal(cancelled.install.text, '重新下载更新');
    assert.equal(cancelled.progressHidden, true);

    // ⑦ 检查失败：不能说"已是最新"，还要带出上次发现的新版本
    const failed = await runCheck('failed', '检查更新失败');
    assert.ok(failed.text.includes('连不上 GitHub'), '应显示失败原因：' + failed.text);
    assert.ok(!failed.text.includes('已经是最新版'), '失败时不能出现"已是最新"：' + failed.text);
    assert.ok(failed.text.includes('上次检查发现过 v26.11.1'), '失败时应保留上次发现的新版本：' + failed.text);
    assert.equal(failed.color, 'var(--danger)');
    assert.equal(failed.download.hidden, false, '检查失败但有历史地址时给发布页兜底');
    assert.equal(failed.download.text, '打开发布页');

    // ⑧ 点发布页：走宿主 openExternal，不在 WebView 里导航
    const hrefBefore = await page.evaluate(() => location.href);
    await page.click('#about-download');
    await page.waitForFunction(() => window.commands.some(message => message.command === 'openExternal'));
    const external = await page.evaluate(() => window.commands.filter(message => message.command === 'openExternal').at(-1));
    assert.equal(external.url, REPLIES.failed.lastKnownReleaseUrl);
    assert.equal(await page.evaluate(() => location.href), hrefBefore, '打开外链不应改变网页地址');

    // ⑨ 下载侧失败：说"更新没有完成"，不能诬赖网络
    const installFailed = await runCheck('installFailed', '更新没有完成');
    assert.ok(!installFailed.text.includes('检查更新失败'), '下载/校验失败不能写成检查失败：' + installFailed.text);
    assert.ok(installFailed.text.includes('哈希'), '应保留原始失败原因：' + installFailed.text);

    // ⑩ 有新版本但没有适配包：指向发布页手动下载
    const unsupported = await runCheck('unsupported', '没有适配');
    assert.equal(unsupported.download.text, '打开发布页');
    assert.ok(unsupported.downloadUrl.endsWith('/releases/tag/v26.11.1'), '应指向发布页：' + unsupported.downloadUrl);
    assert.equal(unsupported.install.hidden, true, '没有适配包时不能给自动下载按钮');

    // ⑪ 已是最新：明确说没有更新，四个按钮全不露面
    const current = await runCheck('current', '已经是最新版');
    assert.equal(current.color, 'var(--success)');
    assert.equal(current.download.hidden, true);
    assert.equal(current.download.display, 'none', '没有更新时下载入口必须真的隐藏（hidden 不能被 display 规则盖掉）');
    assert.equal(current.install.hidden, true);
    assert.deepEqual(current.counts, [1, 1, 1, 1, 1], '按钮各只有一份，反复检查不能叠加');

    // ⑫ 只发现预览版：稳定通道不收，但要说出来
    const prerelease = await runCheck('prerelease', '预览版');
    assert.ok(prerelease.text.includes('v26.12.0-beta.1'), '应提示发现了预览版：' + prerelease.text);
    assert.equal(prerelease.download.hidden, true);

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
    console.log('PASS: 关于页更新卡片——四种检查结论分开显示、下载进度只认宿主推送、校验后可安装、取消后可重下、检查失败与安装失败文案不混用、外链统一走宿主 openExternal。');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
