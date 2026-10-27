// 列表重排与设置分类指示条（方案 §6 歌曲/专辑/艺术家行、设置行；§8 第三批验收）：
//   ① 歌曲列表换排序 → 行从旧位置滑到新位置（不是整块重画）
//   ② 专辑 / 艺术家网格换排序 → 卡片同样滑过去
//   ③ 设置页分类指示条：落在选中项上、点击后跟过去、导航栏没有被整体重建
//   ④ 关掉「列表重排」分类后，换排序是瞬时的（没人给行建动画对象）
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellListMotionSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href;

// 设置页只关心导航，字段表给空数组即可（render 自己能处理空 defs）
const SETTINGS = {
  revision: 3,
  groups: [
    // 只有 general 用真实渲染器；其余用测试专用 id 落到通用渲染，
    // 免得为了测导航去伪造 audio/network 那一整套字段表
    { id: 'general', label: '通用', icon: 'settings' },
    { id: 'case-a', label: '分类 A', icon: 'wave' },
    { id: 'case-b', label: '分类 B', icon: 'folder' },
    { id: 'case-c', label: '分类 C', icon: 'cloud' },
    { id: 'case-d', label: '分类 D', icon: 'palette' },
    { id: 'case-e', label: '分类 E', icon: 'info' }
  ],
  defs: [],
  values: {}
};

const animated = (selector, page) => page.waitForFunction(
  sel => [...document.querySelectorAll(sel)].some(node => node.getAnimations().length > 0),
  selector, { timeout: 1500 }
).then(() => true, () => false);

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
    const errors = [];
    page.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });

    await page.addInitScript(snapshot => {
      const handlers = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        postMessage(message) {
          if (!message || !message.requestId) return;
          setTimeout(() => handlers.forEach(fn => fn({
            data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok: true, data: snapshot }
          })), 5);
        },
        addEventListener(_, fn) { handlers.push(fn); }
      };
      window.__deliver = message => handlers.forEach(fn => fn({ data: message }));
    }, SETTINGS);

    await page.goto(URL, { waitUntil: 'load' });
    await page.waitForTimeout(200);

    // 喂一份能排序的曲库（歌曲/专辑/艺术家三个列表都要有货才测得出来）
    await page.evaluate(() => {
      const titles = ['Chicago', 'Amber', 'Zephyr', 'Bloom', 'Delta', 'Cinder'];
      const tracks = titles.map((title, index) => ({
        id: 'track-' + index,
        title,
        artist: '艺人 ' + String.fromCharCode(65 + index),
        album: '专辑 ' + String.fromCharCode(65 + Math.floor(index / 2)),
        albumArtist: '艺人 ' + String.fromCharCode(65 + index),
        year: 2020 + index,
        duration: 180 + index,
        track: String(index + 1),
        disc: '1',
        chips: [],
        sourceId: '',
        remote: false
      }));
      const albums = [0, 1, 2].map(group => ({
        id: 'album-' + group,
        title: '专辑 ' + String.fromCharCode(65 + group),
        artist: '艺人 ' + String.fromCharCode(65 + group * 2),
        year: 2020 + group,
        added: group,
        trackCount: 2,
        duration: 360 + group,
        trackIds: [tracks[group * 2].id, tracks[group * 2 + 1].id],
        art: 'teal',
        sleeve: 'ALBUM ' + group
      }));
      window.__deliver({ kind: 'celeste.library', version: 1, revision: 1, phase: 'begin', total: tracks.length });
      window.__deliver({ kind: 'celeste.library', version: 1, revision: 1, phase: 'batch', tracks });
      window.__deliver({ kind: 'celeste.library', version: 1, revision: 1, phase: 'end', albums, total: tracks.length });
    });
    await page.waitForTimeout(300);

    // ① 歌曲列表
    await page.evaluate(() => CelesteShell.navigate('songs'));
    await page.waitForSelector('#songs-list li');
    const songsBefore = await page.locator('#songs-list li .song-title').first().textContent();
    await page.locator('#songs-direction').click();
    assert.equal(await animated('#songs-list li', page), true, '歌曲换排序时行上有位移动画');
    const songsAfter = await page.locator('#songs-list li .song-title').first().textContent();
    assert.notEqual(songsAfter, songsBefore, '顺序确实变了');

    // ④ 关掉「列表重排」后是瞬时的
    await page.evaluate(() => CelesteMotion.setOff(['list']));
    await page.waitForTimeout(260);           // 等上一批动画走完，免得误判
    await page.locator('#songs-direction').click();
    const offAnimated = await animated('#songs-list li', page);
    assert.equal(offAnimated, false, '关掉列表类后不再有行动画');
    await page.evaluate(() => CelesteMotion.setOff([]));
    await page.waitForTimeout(120);

    // ② 专辑网格
    await page.evaluate(() => CelesteShell.navigate('albums'));
    await page.waitForSelector('.album-card');
    const albumsBefore = await page.evaluate(() => [...document.querySelectorAll('.album-card')].map(card => card.dataset.flipkey));
    await page.locator('#albums-direction').click();
    const albumsProbe = await page.evaluate(() => ({
      after: [...document.querySelectorAll('.album-card')].map(card => card.dataset.flipkey),
      anims: [...document.querySelectorAll('.album-card')].map(card => card.getAnimations().length),
      motion: !!window.CelesteMotion,
      canMove: window.CelesteMotion?.canMove(),
      off: window.CelesteMotion?.getOff()
    }));
    assert.equal(await animated('.album-card', page), true, '专辑换排序时卡片有位移动画');
    assert.notDeepEqual(albumsProbe.after, albumsBefore, '专辑顺序确实变了');

    // ② 艺术家网格
    await page.evaluate(() => CelesteShell.navigate('artists'));
    await page.waitForSelector('.artist-card');
    await page.locator('#artists-direction').click();
    assert.equal(await animated('.artist-card', page), true, '艺术家换排序时卡片有位移动画');

    // ③ 设置页分类指示条
    await page.evaluate(() => CelesteShell.navigate('settings'));
    await page.waitForSelector('.settings-nav-item');
    await page.waitForTimeout(250);
    const before = await page.evaluate(() => {
      const bar = document.querySelector('.settings-nav-indicator');
      const active = document.querySelector('.settings-nav-item.selected');
      const b = bar.getBoundingClientRect(), a = active.getBoundingClientRect();
      return {
        items: document.querySelectorAll('.settings-nav-item').length,
        ready: bar.dataset.ready,
        topGap: Math.round((b.top - a.top) * 100) / 100,
        heightGap: Math.round((b.height - a.height) * 100) / 100,
        group: active.dataset.group,
        firstItem: document.querySelector('.settings-nav-item')
      };
    });
    assert.equal(before.items, 6, '六个分类都画出来了');
    assert.equal(before.ready, 'true', '指示条已定位');
    assert.ok(Math.abs(before.topGap) < 1.5, '指示条和选中项顶部对齐：' + before.topGap + 'px');
    assert.ok(Math.abs(before.heightGap) < 1.5, '指示条高度等于选中项：' + before.heightGap + 'px');

    // 记住第一个分类按钮的引用：换分类后它还应该是同一个节点（导航没被整体重建）
    await page.evaluate(() => { window.__firstNavItem = document.querySelector('.settings-nav-item'); });
    await page.locator('.settings-nav-item').nth(3).click();
    await page.waitForTimeout(320);   // 指示条有 180ms 的滑动，量早了会读到半路位置
    const after = await page.evaluate(() => {
      const bar = document.querySelector('.settings-nav-indicator');
      const active = document.querySelector('.settings-nav-item.selected');
      return {
        group: active.dataset.group,
        same: window.__firstNavItem === document.querySelector('.settings-nav-item'),
        topGap: Math.round((bar.getBoundingClientRect().top - active.getBoundingClientRect().top) * 100) / 100,
        moved: bar.style.getPropertyValue('--ind-y')
      };
    });
    assert.equal(after.group, 'case-c', '选中项跟着换');
    assert.equal(after.same, true, '导航栏没有被整体重建（指示条才能连续滑）');
    assert.ok(Math.abs(after.topGap) < 1.5, '指示条跟到新分类：' + after.topGap + 'px');
    assert.notEqual(after.moved, '0px', '指示条确实换了位置');

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: 歌曲/专辑/艺术家换排序有位移动画 / 关掉列表类即瞬时 / 设置分类指示条跟随且不重建导航');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
