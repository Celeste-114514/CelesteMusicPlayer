// W13 曲库浏览面回包采集：把 browse(get) 与三类 collectionOverview 的原始回包落成 JSON，
// 用于「搬动前后逐字段比对」。只读，不写收藏/评分、不建播放列表、不播放。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13BrowseWireProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_browse.json'));

function wait(ms) { return new Promise(r => setTimeout(r, ms)); }

(async () => {
  if (!fs.existsSync(exe)) throw new Error('exe 不存在: ' + exe);
  const child = spawn(exe, ['--w7-background-test'], { detached: false, stdio: 'ignore' });

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
    if (!page) throw new Error('未找到主网页');
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 90000 });

    const out = await page.evaluate(async () => {
      const call = async (command, payload) => {
        try { return { ok: true, data: await CelesteTransport.request(command, payload) }; }
        catch (e) { return { ok: false, error: String(e && e.message || e) }; }
      };
      const result = {};

      const browse = await call('browse', { operation: 'get' });
      result.browse = browse;
      if (!browse.ok) return result;

      const tracks = browse.data.tracks || [];
      const playlists = browse.data.playlists || [];
      result.browseSummary = {
        tracks: tracks.length,
        playlists: playlists.length,
        playlistsWithCover: playlists.filter(p => p.coverUrl && !p.coverUrl.includes('v=default')).length,
        missingTotal: playlists.reduce((sum, p) => sum + (p.missingCount || 0), 0),
        favorites: tracks.filter(t => t.favorite).length,
        rated: tracks.filter(t => t.rating > 0).length,
        played: tracks.filter(t => t.playCount > 0).length,
        folders: [...new Set(tracks.map(t => t.folder))].length,
        keys: Object.keys(browse.data).sort(),
        trackKeys: tracks.length ? Object.keys(tracks[0]).sort() : []
      };

      // 挑样本：曲目最多的专辑 / 艺术家 / 专辑艺人，覆盖面最大也最能暴露统计差异。
      // ⚠ browse(get) 回的是精简行（id/folder/收藏/评分），没有专辑与艺术家字段，
      // 所以样本名从网页侧曲库镜像取，别从 browse 回包里找。
      const shellLibrary = window.CelesteShell?.getLibrary?.() || [];
      const pickSource = shellLibrary.length ? shellLibrary : tracks;
      const top = (key) => {
        const counts = new Map();
        for (const t of pickSource) { const v = t[key] || ''; if (!v) continue; counts.set(v, (counts.get(v) || 0) + 1); }
        return [...counts.entries()].sort((a, b) => b[1] - a[1]).slice(0, 2).map(e => e[0]);
      };
      const picks = {
        albums: top('album'),
        artists: top('artist'),
        albumArtists: top('albumArtist')
      };
      result.picks = picks;
      result.pickSourceSize = pickSource.length;

      result.overviews = {};
      for (const category of Object.keys(picks)) {
        for (const name of picks[category]) {
          const key = category + '|' + name;
          const overview = await call('collectionOverview', { category, name });
          result.overviews[key] = overview.ok
            ? { ok: true, keys: Object.keys(overview.data).sort(), data: overview.data }
            : { ok: false, error: overview.error };
        }
      }

      // 拒绝路径
      result.rejects = {
        browseUnknown: (await call('browse', { operation: 'launchRocket' })).error || 'ACCEPTED(BAD)',
        browseEmptyName: (await call('browse', { operation: 'createPlaylist', name: '   ' })).error || 'ACCEPTED(BAD)',
        overviewBadCategory: (await call('collectionOverview', { category: 'genres', name: 'x' })).error || 'ACCEPTED(BAD)',
        overviewUnknownName: (await call('collectionOverview', { category: 'albums', name: '__no_such_album__' })).error || 'ACCEPTED(BAD)',
        ratingOutOfRange: (await call('browse', { operation: 'rating', trackId: tracks[0]?.id || '', value: 9 })).error || 'ACCEPTED(BAD)'
      };
      return result;
    });

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({
      exe, out: OUT,
      browseOk: out.browse?.ok,
      tracks: out.browseSummary?.tracks,
      playlists: out.browseSummary?.playlists,
      overviewKeys: Object.keys(out.overviews || {}),
      rejects: out.rejects
    }, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
