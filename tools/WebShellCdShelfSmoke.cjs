// CD 架冒烟：专辑页的第二种浏览方式（网格 / CD 架）。
// 覆盖：视图切换与记忆、盒子与搁板几何、封面、悬停抬起、方向键、Enter 打开、
// 返回还原滚动与焦点、正在播放标记、搜索/排序、播放按钮、窄窗不溢出、切回网格。
const assert = require('node:assert/strict'), path = require('node:path'), { pathToFileURL } = require('node:url');
let chromium; try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const NAMES = ['夜航西飞', '雨中的信号塔', 'Seventh Wave', '慢速快门', 'Aurora Line', '旧磁带'];
const ARTISTS = ['林澈', 'Northbound', 'Mono Lake', 'Kite'];
const count = 24;
const albums = Array.from({ length: count }, (_, index) => ({
  id: 'al-' + String(index).padStart(2, '0'),
  title: NAMES[index % NAMES.length] + ' ' + (index + 1),
  artist: ARTISTS[index % ARTISTS.length],
  year: 1988 + index,
  added: index,
  trackCount: 2,
  duration: 420 + index,
  // 第 5 张故意没有封面：占位图不能是破图
  coverUrl: index === 5 ? '' : `https://celeste-cover.internal/cover/${index}.png`,
  trackIds: [`al-${String(index).padStart(2, '0')}-t1`, `al-${String(index).padStart(2, '0')}-t2`]
}));
const tracks = albums.flatMap((album, index) => [1, 2].map(track => ({
  id: `${album.id}-t${track}`, index: index * 2 + track - 1, title: `${album.title} · ${track}`, artist: album.artist,
  album: album.title, year: album.year, track, disc: 1, genre: 'Ambient', sortPath: `C:/${album.id}${track}.flac`,
  duration: 200 + index, chips: [], coverUrl: album.coverUrl
})));

(async () => {
  const browser = await chromium.launch({ executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } }), errors = [];
    page.on('pageerror', error => errors.push(error.message));
    const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII=', 'base64');
    await page.route('https://celeste-cover.internal/**', route => route.fulfill({ status: 200, contentType: 'image/png', body: png }));
    await page.addInitScript(() => {
      window.commands = [];
      window.chrome ||= {};
      window.chrome.webview = {
        postMessage: message => window.commands.push(message),
        addEventListener: (_, handler) => { window.deliver = data => handler({ data }); }
      };
    });
    await page.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href);
    // 刷新会丢曲库（数据来自宿主推送），重新灌一遍
    const loadLibrary = () => page.evaluate(data => {
      deliver({ kind: 'celeste.library', version: 1, phase: 'begin', revision: 1, total: data.tracks.length });
      deliver({ kind: 'celeste.library', version: 1, phase: 'batch', revision: 1, tracks: data.tracks });
      deliver({ kind: 'celeste.library', version: 1, phase: 'end', revision: 1, albums: data.albums });
      deliver({ kind: 'celeste.state', version: 1, state: { trackId: 'al-02-t1', title: 'x', artist: 'y', album: 'z', duration: 200, position: 0, playing: false } });
    }, { albums, tracks });
    await loadLibrary();

    const go = async name => {
      await page.evaluate(name => CelesteShell.navigate(name), name);
      await page.waitForFunction(() => document.getAnimations().filter(a => a.playState === 'running').length === 0).catch(() => { });
      await page.waitForTimeout(120);
    };

    // ① 默认还是网格；切换按钮存在且状态正确
    await go('albums');
    assert.equal(await page.locator('#albums-grid').isVisible(), true, '默认应是网格');
    assert.equal(await page.locator('#albums-shelf').isVisible(), false, '默认不该显示架子');
    assert.equal(await page.locator('.album-card').count(), count);
    assert.deepEqual(await page.locator('.album-view-switch button').allTextContents(), ['网格', 'CD 架']);
    assert.equal(await page.locator('[data-album-view="grid"]').getAttribute('aria-pressed'), 'true');

    // ② 切到架子：网格让位，24 张全部摆出来，每张一个 CD 盒
    await page.locator('[data-album-view="shelf"]').click();
    await page.waitForSelector('#albums-shelf .cd-case');
    assert.equal(await page.locator('#albums-grid').isVisible(), false, '切到架子后网格应隐藏');
    assert.equal(await page.locator('#albums-shelf').isVisible(), true);
    assert.equal(await page.locator('#albums-shelf .cd-case').count(), count);
    assert.equal(await page.locator('#albums-count').textContent(), `${count} 张专辑 · ${tracks.length} 首歌曲`);
    assert.equal(await page.locator('[data-album-view="shelf"]').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.evaluate(() => localStorage.getItem('celeste.albums.view')), 'shelf', '浏览方式要记在本机');

    // ③ 盒子几何：封面是方的、右侧有 7px 书脊、搁板贴着封面下沿
    const geometry = await page.evaluate(() => {
      const first = document.querySelector('#albums-shelf .cd-case'), row = first.closest('.cd-shelf-row');
      const art = first.querySelector('.cd-case-art').getBoundingClientRect();
      const spine = getComputedStyle(first.querySelector('.cd-case-body'), '::after');
      const plank = getComputedStyle(row, '::after');
      return {
        width: Math.round(art.width), height: Math.round(art.height),
        spineWidth: spine.width, spineTop: spine.top,
        plankHeight: parseFloat(plank.height), plankTop: parseFloat(plank.top),
        coverVar: parseFloat(row.style.getPropertyValue('--cover-h')),
        columns: Number(row.style.getPropertyValue('--cols')),
        radius: getComputedStyle(first.querySelector('.cd-case-art')).borderRadius
      };
    });
    assert.ok(Math.abs(geometry.width - geometry.height) <= 1, `封面不是方的：${geometry.width}x${geometry.height}`);
    assert.equal(geometry.spineWidth, '7px', '右侧书脊应是 7px');
    assert.ok(geometry.plankHeight >= 10, '搁板厚度不合理');
    assert.ok(Math.abs(geometry.plankTop - (geometry.coverVar - 5)) < 1.5, '搁板没贴在封面下沿');
    assert.ok(Math.abs(geometry.coverVar - geometry.height) < 1.5, '--cover-h 与实测封面高度不一致');
    assert.ok(geometry.columns >= 2, `列数应至少 2：${geometry.columns}`);
    assert.equal(geometry.radius, '3px', 'CD 盒是方角，不该沿用网格卡片的大圆角');

    // ④ 封面：有图的全画出来，没封面的用占位、不留破图
    await page.waitForFunction(() => [...document.querySelectorAll('.cd-case-art img')].every(image => image.complete && image.naturalWidth > 0));
    assert.equal(await page.locator('.cd-case-art img').count(), count - 1);
    const noCover = page.locator('.cd-case[data-album-id="al-05"]');
    assert.equal(await noCover.locator('img').count(), 0, '没封面不该塞一个加载失败的 img');
    assert.ok((await noCover.locator('.cd-case-art').innerHTML()).includes('i-note'), '没封面应有占位图标');

    // ⑤ 悬停抬起 4px；播放按钮随即出现
    await page.evaluate(() => { document.querySelector('.main').scrollTop = 0; });
    const card = page.locator('.cd-case[data-album-id="al-00"]');
    const before = await card.locator('.cd-case-body').boundingBox();
    await card.locator('.cd-case-open').hover();
    await page.waitForTimeout(320);
    const after = await card.locator('.cd-case-body').boundingBox();
    const lift = before.y - after.y;
    assert.ok(lift >= 2.5 && lift <= 6, `悬停位移应为 4px 左右，实测 ${lift.toFixed(2)}px`);
    assert.equal(await card.locator('.cd-case-play').evaluate(node => getComputedStyle(node).opacity), '1', '悬停后播放按钮应可见');

    // ⑥ 键盘：方向键在盒之间走，Enter 打开，返回还原滚动位置与焦点
    // 默认按专辑名排序，架上的先后不等于 id 顺序 —— 期望值一律从实际顺序里取
    const order = await page.$$eval('#albums-shelf .cd-case', nodes => nodes.map(node => node.dataset.albumId));
    await page.locator(`.cd-case[data-album-id="${order[0]}"] .cd-case-open`).focus();
    await page.keyboard.press('ArrowRight');
    assert.equal(await page.evaluate(() => document.activeElement.closest('.cd-case').dataset.albumId), order[1], '右方向键应到下一张');
    const columns = await page.evaluate(() => Number(document.querySelector('.cd-shelf-row').style.getPropertyValue('--cols')));
    await page.keyboard.press('ArrowDown');
    assert.equal(await page.evaluate(() => document.activeElement.closest('.cd-case').dataset.albumId), order[1 + columns], '下方向键应到同一列下一行');
    const scrollBefore = await page.evaluate(() => { const main = document.querySelector('.main'); main.scrollTop = 300; return main.scrollTop; });
    assert.ok(scrollBefore > 100, `滚动位置没设上：${scrollBefore}`);
    const focusedId = await page.evaluate(() => document.activeElement.closest('.cd-case').dataset.albumId);
    await page.keyboard.press('Enter');
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#album-detail').isVisible(), true, 'Enter 应打开专辑详情');
    assert.equal(await page.locator('#album-detail-title').textContent(), albums[Number(focusedId.slice(3))].title);
    assert.deepEqual(await page.locator('#breadcrumb button').allTextContents(), ['主页', '专辑', albums[Number(focusedId.slice(3))].title]);
    // 专辑页的返回走面包屑（#album-back 被 navigation.css 有意隐藏）
    await page.locator('#breadcrumb button').filter({ hasText: /^专辑$/ }).click();
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#albums-shelf').isVisible(), true, '返回后应还在架子上');
    assert.equal(await page.evaluate(() => document.activeElement.closest('.cd-case')?.dataset.albumId), focusedId, '返回后焦点应回到刚才那张');
    assert.equal(await page.evaluate(() => document.querySelector('.main').scrollTop), scrollBefore, '返回后应还原滚动位置');

    // ⑦ 正在播放：只有一张带标记，且有文字说明（不只靠颜色）
    const playing = page.locator('#albums-shelf .cd-case[data-playing="true"]');
    assert.equal(await playing.count(), 1);
    assert.equal(await playing.getAttribute('data-album-id'), 'al-02');
    assert.equal((await playing.locator('.cd-case-flag').textContent()).trim(), '正在播放');
    assert.equal(await playing.locator('.cd-case-open').getAttribute('aria-current'), 'true');
    await page.evaluate(() => deliver({ kind: 'celeste.state', version: 1, state: { trackId: 'al-04-t2', title: 'x', artist: 'y', album: 'z', duration: 200, position: 12, playing: true } }));
    await page.waitForFunction(() => document.querySelector('#albums-shelf .cd-case[data-playing="true"]')?.dataset.albumId === 'al-04');
    assert.equal(await page.locator('#albums-shelf .cd-case[data-playing="true"]').count(), 1, '旧的那张要摘掉标记');
    assert.equal(await page.locator('#albums-shelf .cd-case[data-album-id="al-02"]').getAttribute('data-playing'), null);

    // ⑧ 搜索只在架子上过滤，清空后复原
    await page.fill('#search', 'Aurora');
    await page.waitForTimeout(150);
    assert.equal(await page.locator('#albums-shelf .cd-case').count(), 4);
    assert.equal(await page.locator('#albums-count').textContent(), `4 张匹配 · 共 ${count} 张专辑`);
    assert.equal(await page.locator('#albums-empty').isVisible(), false);
    await page.fill('#search', '绝对不存在的专辑名');
    await page.waitForTimeout(150);
    assert.equal(await page.locator('#albums-shelf .cd-case').count(), 0);
    assert.equal(await page.locator('#albums-empty').isVisible(), true);
    assert.match(await page.locator('#albums-empty').textContent(), /没有找到相关专辑/);
    await page.fill('#search', '');
    await page.waitForTimeout(150);
    assert.equal(await page.locator('#albums-shelf .cd-case').count(), count);

    // ⑨ 排序在架子上一样生效
    await page.locator('#albums-sort').selectOption('year');
    await page.waitForTimeout(150);
    assert.equal(await page.locator('#albums-shelf .cd-case').first().getAttribute('data-album-id'), 'al-00', '按年份升序第一张应是 1988 那张');
    await page.locator('#albums-direction').click();
    await page.waitForTimeout(150);
    assert.equal(await page.locator('#albums-shelf .cd-case').first().getAttribute('data-album-id'), 'al-23', '降序应翻过来');
    await page.locator('#albums-direction').click();
    await page.locator('#albums-sort').selectOption('title');
    await page.waitForTimeout(150);

    // ⑩ 播放按钮走真实专辑播放命令
    const target = page.locator('.cd-case[data-album-id="al-04"]');
    await target.locator('.cd-case-play').click();
    await page.waitForTimeout(120);
    const played = await page.evaluate(() => window.commands.filter(message => message.command === 'playAll').at(-1));
    assert.deepEqual(played.trackIds, albums[4].trackIds, '播放按钮应播这张专辑的全部曲目');

    // ⑪ 窄窗：列数跟着收，不出现横向滚动，搁板仍然贴住封面
    for (const width of [1440, 1080, 860]) {
      await page.setViewportSize({ width, height: 900 });
      await page.waitForTimeout(300);
      const fit = await page.evaluate(() => {
        const main = document.querySelector('.main'), row = document.querySelector('.cd-shelf-row');
        const art = row.querySelector('.cd-case-art').getBoundingClientRect();
        return {
          overflow: main.scrollWidth - main.clientWidth,
          columns: Number(row.style.getPropertyValue('--cols')),
          coverVar: parseFloat(row.style.getPropertyValue('--cover-h')),
          coverHeight: art.height,
          pageOverflow: document.documentElement.scrollWidth - innerWidth
        };
      });
      assert.ok(fit.overflow <= 0, `窗口 ${width}px 主区域横向溢出 ${fit.overflow}px`);
      assert.ok(fit.pageOverflow <= 0, `窗口 ${width}px 整页横向溢出 ${fit.pageOverflow}px`);
      assert.ok(fit.columns >= 2, `窗口 ${width}px 只剩 ${fit.columns} 列`);
      assert.ok(Math.abs(fit.coverVar - fit.coverHeight) < 2, `窗口 ${width}px 搁板与封面脱节`);
    }
    await page.setViewportSize({ width: 1440, height: 960 });
    await page.waitForTimeout(300);

    // ⑫ 切回网格：卡片回来，架子和它的封面一起清掉
    await page.locator('[data-album-view="grid"]').click();
    await page.waitForTimeout(200);
    assert.equal(await page.locator('#albums-grid').isVisible(), true);
    assert.equal(await page.locator('#albums-shelf').isVisible(), false);
    assert.equal(await page.locator('.album-card').count(), count);
    assert.equal(await page.locator('#albums-shelf .cd-case').count(), 0, '切回网格后架子应被清空');
    assert.equal(await page.evaluate(() => localStorage.getItem('celeste.albums.view')), 'grid');

    // ⑬ 重新打开程序：仍记得上次选的网格；改成架子后刷新也记得
    await page.reload();
    await page.waitForTimeout(200);
    await loadLibrary();
    await go('albums');
    assert.equal(await page.locator('#albums-grid').isVisible(), true);
    await page.locator('[data-album-view="shelf"]').click();
    await page.waitForSelector('#albums-shelf .cd-case');
    await page.reload();
    await page.waitForTimeout(200);
    await loadLibrary();
    await go('albums');
    assert.equal(await page.locator('#albums-shelf').isVisible(), true, '刷新后应还停在 CD 架');
    assert.equal(await page.locator('[data-album-view="shelf"]').getAttribute('aria-pressed'), 'true');
    assert.equal(await page.locator('.album-card').count(), 0, '架子模式下不该同时渲染网格卡片');

    // ⑭ 空曲库 / 正在扫库：两种"一张都没有"要说清是哪一种
    const blank = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    await blank.addInitScript(() => {
      window.chrome ||= {};
      window.chrome.webview = { postMessage: () => { }, addEventListener: (_, handler) => { window.deliver = data => handler({ data }); } };
    });
    await blank.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href);
    await blank.evaluate(() => CelesteShell.navigate('albums'));
    await blank.waitForTimeout(200);
    assert.match(await blank.locator('#albums-empty').textContent(), /正在载入曲库/, '还在扫库时应说正在载入');
    await blank.evaluate(() => {
      deliver({ kind: 'celeste.library', version: 1, phase: 'begin', revision: 2, total: 0 });
      deliver({ kind: 'celeste.library', version: 1, phase: 'batch', revision: 2, tracks: [] });
      deliver({ kind: 'celeste.library', version: 1, phase: 'end', revision: 2, albums: [] });
    });
    await blank.waitForTimeout(200);
    assert.match(await blank.locator('#albums-empty').textContent(), /曲库里还没有专辑/, '空曲库不该说"没找到相关专辑"');
    assert.equal(await blank.locator('#albums-count').textContent(), '0 张专辑 · 0 首歌曲');
    await blank.close();

    assert.deepEqual(errors, []);
    console.log('PASS: 网格/CD 架切换并记忆、盒子书脊与搁板几何、封面与占位、悬停抬起、方向键、Enter 打开、返回还原滚动与焦点、正在播放标记跟随、搜索/排序、播放按钮、窄窗不溢出、切回网格、刷新后记住、空曲库/扫库中的文案。');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
