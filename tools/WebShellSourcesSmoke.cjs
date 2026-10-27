// Browser contract regression: seven source forms, browse/pin/play, sync controls, source filter.
const assert = require('node:assert/strict');
const path = require('node:path');
const { chromium } = require('playwright-core');
(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    const errors = []; page.on('pageerror', error => errors.push(error.message));
    await page.addInitScript(() => {
      const handlers = [], sources = [], commands = [];
      window.__sourceCommands = commands;
      window.__failBrowse = false;      // 用来演一次"目录读不到"，看页面能不能就地重试
      window.chrome = { webview: {
        addEventListener: (_, handler) => handlers.push(handler),
        deliver: data => handlers.forEach(handler => handler({ data })),
        postMessage: message => {
          commands.push(message);
          if (!message.requestId || message.command !== 'sources') return;
          let result = { sources, providers: ['webdav', 'baidu', 'jellyfin', 'emby', 'smb', 'sshfs', 'subsonic'] };
          let ok = true, error = '';
          if (message.operation === 'save') {
            const id = message.sourceId || 'fixture-' + message.provider;
            const source = { id, provider: message.provider, name: message.name, endpoint: message.endpoint, root: message.root,
              enabled: true, count: 0, revision: 1, hasSecret: !!message.secret, job: { running: false, phase: 'idle', discovered: 0 } };
            const existing = sources.findIndex(source => source.id === id);
            if (existing < 0) sources.push(source); else sources[existing] = source;
            result = { ...result, selectedId: id };
          } else if (message.operation === 'test') result = { ok: true, message: '连接成功' };
          else if (message.operation === 'browse') {
            if (window.__failBrowse) { ok = false; error = '模拟目录不可达'; }
            else result = { path: message.path, folders: message.path ? [] : [{ path: '/album', name: 'Album folder' }],
              tracks: [{ id: 'remote-song', title: 'Remote song', artist: 'Artist', album: 'Album', cue: true }] };
          }
          else if (message.operation === 'clearCache') result = { count: 2 };
          setTimeout(() => handlers.forEach(handler => handler({ data: { kind: 'celeste.reply', version: 1, ok, error, requestId: message.requestId, data: result } })), 5);
        }
      } };
    });
    await page.goto('file:///' + path.resolve(__dirname, '../outputs/webview2-shell/index.html').replaceAll('\\', '/'));
    await page.evaluate(async () => {
      document.getElementById('overview-page').hidden = true; document.getElementById('settings-page').hidden = false;
      await window.CelesteSources.mount(document.getElementById('settings-scroll'));
    });
    for (const provider of ['webdav', 'baidu', 'jellyfin', 'emby', 'smb', 'sshfs', 'subsonic']) {
      await page.getByRole('button', { name: '添加来源', exact: true }).click();
      await page.selectOption('#source-provider', provider);
      await page.locator('label').filter({ hasText: '显示名称' }).locator('input').fill('Fixture ' + provider);
      await page.locator('label').filter({ hasText: '服务器地址 / 已挂载路径' }).locator('input').fill(provider === 'smb' || provider === 'sshfs' ? 'Z:\\Music' : 'https://fixture.invalid');
      await page.getByRole('button', { name: '保存来源', exact: true }).click();
      await page.waitForFunction(provider => window.__sourceCommands.some(message => message.operation === 'save' && message.provider === provider), provider);
      await page.getByRole('button', { name: '浏览', exact: true }).last().waitFor();
    }
    assert.equal(await page.getByRole('button', { name: '浏览', exact: true }).count(), 7);
    await page.getByRole('button', { name: '浏览', exact: true }).first().click();
    await page.locator('.source-workspace').getByRole('button', { name: '播放', exact: true }).click();
    await page.getByRole('button', { name: '加入曲库', exact: true }).click();
    await page.getByRole('button', { name: 'Album folder', exact: true }).click();
    await page.getByRole('button', { name: '上一级', exact: true }).click();
    await page.getByRole('button', { name: '加入曲库 / 同步', exact: true }).first().click();
    await page.getByRole('button', { name: '取消同步', exact: true }).first().click();

    // 目录读不到时就地给"重试"，而不是把上一次的内容留在屏幕上假装成功
    await page.evaluate(() => { window.__failBrowse = true; });
    await page.getByRole('button', { name: '浏览', exact: true }).first().click();
    await page.waitForSelector('.source-workspace h2', { state: 'visible' });
    assert.equal(await page.locator('.source-workspace h2').textContent(), '网络目录读取失败');
    assert.match(await page.locator('.source-workspace').textContent(), /模拟目录不可达/);
    await page.evaluate(() => { window.__failBrowse = false; });
    await page.locator('.source-workspace').getByRole('button', { name: '重试', exact: true }).click();
    await page.waitForFunction(() => document.querySelector('.source-workspace')?.textContent.includes('Remote song'));

    const commands = await page.evaluate(() => window.__sourceCommands);
    assert(commands.some(c => c.command === 'playTrack' && c.trackId === 'remote-song'));
    assert(commands.some(c => c.operation === 'pin' && c.trackId === 'remote-song'));
    assert(commands.some(c => c.operation === 'sync') && commands.some(c => c.operation === 'cancel'));
    await page.evaluate(() => {
      const tracks = [
        { id: 'local', title: 'Local', artist: 'Artist', album: 'Album', duration: 1, chips: [], sourceId: '', remote: false },
        { id: 'remote', title: 'Remote', artist: 'Artist', album: 'Album', duration: 1, chips: [], sourceId: 'fixture-webdav', sourceName: 'Fixture webdav', remote: true }
      ];
      const deliver = window.chrome.webview.deliver;
      deliver({ kind: 'celeste.library', version: 1, revision: 1, phase: 'begin', total: 2 });
      deliver({ kind: 'celeste.library', version: 1, revision: 1, phase: 'batch', tracks });
      deliver({ kind: 'celeste.library', version: 1, revision: 1, phase: 'end', albums: [], total: 2 });
      window.CelesteShell.navigate('songs');
    });
    await page.selectOption('#library-source', 'network');
    assert.deepEqual(await page.evaluate(() => window.CelesteShell.getLibrary().map(t => t.id)), ['remote']);
    assert((await page.locator('#songs-count').textContent()).startsWith('1 首'));
    await page.selectOption('#library-source', 'local');
    assert.deepEqual(await page.evaluate(() => window.CelesteShell.getLibrary().map(t => t.id)), ['local']);
    assert.deepEqual(errors, []);
    console.log('PASS seven forms / browse hierarchy / play / pin / sync+cancel / unified source filter');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
