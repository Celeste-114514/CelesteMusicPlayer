// W13 曲目动作全量采集：trackAction 的 get / favorite / rating / next / addQueue / pin / removeQueue / copy / play
// 与 browse 的转发 + 拒绝路径，用于「搬动前后逐字段比对」。
//
// 数据安全：喜欢/评分跑完翻回原值；队列改动只影响内存（退出前不落盘），
// 仍然建议跑前备份 %LOCALAPPDATA%\CelesteMusicPlayer\track-stats.json。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13TrackActionsProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_trackactions.json'));
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  if (!fs.existsSync(exe)) throw new Error('exe 不存在: ' + exe);
  const child = spawn(exe, ['--w7-background-test'], { stdio: 'ignore' });
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
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 100, null, { timeout: 120000 });

    const out = await page.evaluate(async () => {
      const call = async (command, payload) => {
        try { return { ok: true, data: await CelesteTransport.request(command, payload) }; }
        catch (e) { return { ok: false, error: String((e && e.message) || e) }; }
      };
      const queueNow = async () => {
        const r = await call('queue', { operation: 'get' });
        if (!r.ok) return { ok: false, error: r.error, ids: [], total: 0, revision: -1, currentIndex: -1 };
        return { ok: true, revision: r.data.revision, total: r.data.total, currentIndex: r.data.currentIndex, ids: (r.data.tracks || []).map(t => String(t.id ?? t.trackId ?? '')) };
      };
      const result = {};

      // 曲库：挑三首非远程、时长适中的曲子
      const lib = CelesteShell.getLibrary();
      result.libraryCount = lib.length;
      result.libraryKeys = Object.keys(lib[0] || {}).sort();
      const pool = lib.filter(t => !t.isRemote).sort((a, b) => (a.duration || 0) - (b.duration || 0));
      const three = (pool.length >= 3 ? pool : lib).slice(0, 3);
      if (three.length < 3) { result.fatal = '曲库不足 3 首'; return result; }
      const ids = three.map(t => String(t.id));
      result.picks = three.map(t => ({ id: String(t.id), title: t.title, duration: Math.round((t.duration || 0) * 10) / 10 }));

      // 1) get（曲库分支）
      const get1 = await call('trackAction', { operation: 'get', queue: false, trackIds: ids });
      result.get = get1.ok
        ? { keys: Object.keys(get1.data).sort(), favorite: get1.data.favorite, canDelete: get1.data.canDelete, count: get1.data.count }
        : { error: get1.error };

      // 2) favorite：翻一次、读、再翻回来
      const fav0 = get1.ok ? get1.data.favorite : false;
      const setFav = await call('trackAction', { operation: 'favorite', queue: false, trackIds: ids, value: !fav0 });
      const get2 = await call('trackAction', { operation: 'get', queue: false, trackIds: ids });
      const backFav = await call('trackAction', { operation: 'favorite', queue: false, trackIds: ids, value: fav0 });
      const get3 = await call('trackAction', { operation: 'get', queue: false, trackIds: ids });
      result.favorite = {
        set: setFav.ok ? setFav.data.message : 'ERR:' + setFav.error,
        flipped: get2.ok ? get2.data.favorite : null,
        restored: get3.ok ? get3.data.favorite : null,
        back: backFav.ok ? backFav.data.message : 'ERR:' + backFav.error
      };

      // 3) rating：设 3 → 读回 → 清 0
      const ratingOf = id => {
        const hit = CelesteShell.getLibrary().find(t => String(t.id) === id);
        return hit ? (hit.rating ?? hit.score ?? null) : null;
      };
      const setRating = await call('trackAction', { operation: 'rating', queue: false, trackIds: [ids[0]], value: 3 });
      const ratingAfter = ratingOf(ids[0]);
      const clrRating = await call('trackAction', { operation: 'rating', queue: false, trackIds: [ids[0]], value: 0 });
      result.rating = {
        set: setRating.ok ? setRating.data.message : 'ERR:' + setRating.error,
        after: ratingAfter,
        cleared: ratingOf(ids[0]),
        clear: clrRating.ok ? clrRating.data.message : 'ERR:' + clrRating.error
      };

      // 4) 队列基线：用启动时恢复的队列，全程不播放（播放会让 currentIndex 自己往前走，插值就不可复现）
      const q0 = await queueNow();
      result.queueBefore = { revision: q0.revision, total: q0.total, currentIndex: q0.currentIndex, head: q0.ids.slice(0, 4) };

      // 5) next / addQueue：各测「已在队列」（只挪位置）与「队列外」（长度 +1）
      const outsiderA = String(((pool.length >= 12 ? pool : lib)[10] || lib[lib.length - 1]).id);
      const outsiderB = String(((pool.length >= 12 ? pool : lib)[11] || lib[lib.length - 2]).id);
      result.outsiders = { a: outsiderA, b: outsiderB, distinct: outsiderA !== outsiderB };

      const n1Msg = await call('trackAction', { operation: 'next', queue: false, trackIds: [ids[1]] });
      const qN1 = await queueNow();
      result.nextExisting = {
        message: n1Msg.ok ? n1Msg.data.message : 'ERR:' + n1Msg.error,
        delta: qN1.total - q0.total,
        landedAt: qN1.ids.indexOf(ids[1]),
        expectedAt: q0.currentIndex >= 0 ? q0.currentIndex + 1 : 0
      };

      const n2Msg = await call('trackAction', { operation: 'next', queue: false, trackIds: [outsiderA] });
      const qN2 = await queueNow();
      result.nextNew = {
        message: n2Msg.ok ? n2Msg.data.message : 'ERR:' + n2Msg.error,
        delta: qN2.total - qN1.total,
        landedAt: qN2.ids.indexOf(outsiderA),
        expectedAt: qN1.currentIndex >= 0 ? qN1.currentIndex + 1 : 0
      };

      const a1Msg = await call('trackAction', { operation: 'addQueue', queue: false, trackIds: [ids[2]] });
      const qA1 = await queueNow();
      result.addExisting = {
        message: a1Msg.ok ? a1Msg.data.message : 'ERR:' + a1Msg.error,
        delta: qA1.total - qN2.total,
        landedAt: qA1.ids.indexOf(ids[2]),
        isLast: qA1.ids[qA1.ids.length - 1] === ids[2]
      };

      const a2Msg = await call('trackAction', { operation: 'addQueue', queue: false, trackIds: [outsiderB] });
      const qA2 = await queueNow();
      result.addNew = {
        message: a2Msg.ok ? a2Msg.data.message : 'ERR:' + a2Msg.error,
        delta: qA2.total - qA1.total,
        landedAt: qA2.ids.indexOf(outsiderB),
        isLast: qA2.ids[qA2.ids.length - 1] === outsiderB
      };

      // 6) pin：把队列第 3 行置顶
      const qc = await queueNow();
      if (qc.ids.length >= 2) {
        const idx = Math.min(2, qc.ids.length - 1);
        const pinned = qc.ids[idx];
        const pMsg = await call('trackAction', { operation: 'pin', queue: true, revision: qc.revision, entries: [{ index: idx, trackId: pinned }] });
        const qP = await queueNow();
        result.pin = {
          message: pMsg.ok ? pMsg.data.message : 'ERR:' + pMsg.error,
          fromIndex: idx,
          totalDelta: qP.total - qc.total,
          firstIsPinned: qP.ids[0] === pinned,
          occurrences: qP.ids.filter(x => x === pinned).length
        };

        // 7) removeQueue：删掉队首
        const qr = await queueNow();
        const rMsg = await call('trackAction', { operation: 'removeQueue', queue: true, revision: qr.revision, entries: [{ index: 0, trackId: qr.ids[0] }] });
        const qR = await queueNow();
        result.removeQueue = {
          message: rMsg.ok ? rMsg.data.message : 'ERR:' + rMsg.error,
          totalDelta: qR.total - qr.total,
          removedFirst: qR.ids[0] !== qr.ids[0]
        };
      } else {
        result.pin = { skipped: 'queue-too-short', total: qc.total };
      }

      // 8) 队列分支的 get
      const qg = await queueNow();
      if (qg.ids.length > 0) {
        const qget = await call('trackAction', { operation: 'get', queue: true, revision: qg.revision, entries: [{ index: 0, trackId: qg.ids[0] }] });
        result.getFromQueue = qget.ok ? { keys: Object.keys(qget.data).sort(), count: qget.data.count, canDelete: qget.data.canDelete } : { error: qget.error };
      }

      // 9) copy
      const cMsg = await call('trackAction', { operation: 'copy', queue: false, trackIds: ids });
      result.copy = cMsg.ok ? cMsg.data.message : 'ERR:' + cMsg.error;

      // 10) play（曲线库分支，播 1 秒即停）。togglePlay 不回包，只能发不等。
      const pMsg = await call('trackAction', { operation: 'play', queue: false, trackIds: [ids[0]] });
      await new Promise(r => setTimeout(r, 1200));
      CelesteShell.send('togglePlay', {});
      await new Promise(r => setTimeout(r, 600));
      result.play = {
        message: pMsg.ok ? pMsg.data.message : 'ERR:' + pMsg.error,
        stopSent: true
      };

      // 11) 拒绝路径
      result.rejects = {
        pinOutsideQueue: (await call('trackAction', { operation: 'pin', queue: false, trackIds: ids })).error || 'ACCEPTED(BAD)',
        ratingOutOfRange: (await call('trackAction', { operation: 'rating', queue: false, trackIds: ids, value: 9 })).error || 'ACCEPTED(BAD)',
        unknownTrackOperation: (await call('trackAction', { operation: 'launchRocket', queue: false, trackIds: ids })).error || 'ACCEPTED(BAD)',
        emptySelection: (await call('trackAction', { operation: 'get', queue: false, trackIds: [] })).error || 'ACCEPTED(BAD)',
        staleQueueRevision: (await call('trackAction', { operation: 'pin', queue: true, revision: -1, entries: [{ index: 0, trackId: ids[0] }] })).error || 'ACCEPTED(BAD)'
      };

      // 12) browse 转发 + 拒绝
      const bget = await call('browse', { operation: 'get' });
      result.browse = bget.ok ? { keys: Object.keys(bget.data).sort(), kind: bget.data.kind ?? null } : { error: bget.error };
      result.browseRejects = {
        unknownBrowseOperation: (await call('browse', { operation: 'launchRocket' })).error || 'ACCEPTED(BAD)',
        missingPlaylist: (await call('browse', { operation: 'playPlaylist', name: '__no_such_playlist__' })).error || 'ACCEPTED(BAD)'
      };

      const qEnd = await queueNow();
      result.queueAfter = { revision: qEnd.revision, total: qEnd.total, currentIndex: qEnd.currentIndex };
      result.queueDrift = qEnd.total - q0.total;
      return result;
    });

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({ out: OUT, get: out.get, favorite: out.favorite, rating: out.rating,
      nextExisting: out.nextExisting, nextNew: out.nextNew, addExisting: out.addExisting, addNew: out.addNew,
      pin: out.pin, removeQueue: out.removeQueue,
      getFromQueue: out.getFromQueue, copy: out.copy, play: out.play, rejects: out.rejects,
      browse: out.browse, browseRejects: out.browseRejects, queueDrift: out.queueDrift }, null, 2));
  } finally {
    try { await browser.close(); } catch { }
    try { child.kill(); } catch { }
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
