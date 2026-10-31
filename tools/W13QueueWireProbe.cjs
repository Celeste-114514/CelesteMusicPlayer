// W13 队列页 / 「更多」菜单回包采集：搬动前后逐字段比对。
//
// 只读约束：不播放、不改设置、不删文件、不弹资源管理器。
// 唯一会动的是「拖动换序」—— 做完立刻拖回原位，队列顺序与开头一致。
// （修订号会 +2，属预期；比对时把它单独列出来看。）
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13QueueWireProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_queue.json'));

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
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 100, null, { timeout: 120000 });

    const out = await page.evaluate(async () => {
      const call = async (command, payload) => {
        try { return { ok: true, data: await CelesteTransport.request(command, payload) }; }
        catch (e) { return { ok: false, error: String((e && e.message) || e) }; }
      };
      const result = {};

      // 1) 整份队列（字段全收，空队列也照收）
      const q0 = await call('queue', { operation: 'get' });
      if (!q0.ok) { result.queueGet = { ok: false, error: q0.error }; return result; }
      const d0 = q0.data;
      result.queueGet = {
        ok: true, keys: Object.keys(d0).sort(), revision: d0.revision, total: d0.total, offset: d0.offset,
        currentIndex: d0.currentIndex, order: d0.order,
        summaryKeys: d0.summary ? Object.keys(d0.summary).sort() : null,
        summary: d0.summary, previous: d0.previous, next: d0.next,
        trackKeys: d0.tracks?.[0] ? Object.keys(d0.tracks[0]).sort() : [],
        tracks: (d0.tracks || []).map(t => ({
          index: t.index, title: t.title, artist: t.artist, album: t.album,
          duration: Math.round((t.duration || 0) * 1000) / 1000,
          format: t.format, remote: t.remote, hasId: typeof t.id === 'string' && t.id.length > 0,
          coverIsInternal: String(t.coverUrl || '').startsWith('https://celeste-cover.internal/cover/')
        }))
      };
      const R = d0.revision;
      const firstId = (d0.tracks || [])[0]?.id;
      const secondId = (d0.tracks || [])[1]?.id;

      // 2) 分页：offset 越界要回落 0，正常 offset 要按位置切片
      const page1 = await call('queue', { operation: 'get', offset: 1 });
      result.pageOffset1 = page1.ok ? {
        ok: true, offset: page1.data.offset, total: page1.data.total,
        revisionStable: page1.data.revision === R,
        firstIndex: (page1.data.tracks || [])[0]?.index ?? null,
        count: (page1.data.tracks || []).length
      } : { ok: false, error: page1.error };

      for (const bad of [-5, 999]) {
        const r = await call('queue', { operation: 'get', offset: bad });
        result['offset' + bad] = r.ok ? { ok: true, offset: r.data.offset, count: (r.data.tracks || []).length } : { ok: false, error: r.error };
      }

      // 3) 拒绝路径：修订号 / 下标 / trackId / 未知操作
      result.rejects = {
        playStaleRevision: (await call('queue', { operation: 'play', index: 0, revision: R + 999, trackId: firstId })).error || 'ACCEPTED(BAD)',
        playBadIndex: (await call('queue', { operation: 'play', index: 9999, revision: R, trackId: firstId })).error || 'ACCEPTED(BAD)',
        playWrongTrack: (await call('queue', { operation: 'play', index: 0, revision: R, trackId: 'no-such-track' })).error || 'ACCEPTED(BAD)',
        moveStaleRevision: (await call('queue', { operation: 'move', from: 0, to: 1, revision: R + 999, trackId: firstId })).error || 'ACCEPTED(BAD)',
        moveBadIndex: (await call('queue', { operation: 'move', from: -1, to: 0, revision: R, trackId: firstId })).error || 'ACCEPTED(BAD)',
        unknownOperation: (await call('queue', { operation: 'launchRocket' })).error || 'ACCEPTED(BAD)'
      };

      // 4) 真拖动：换过去再换回来，最后顺序必须与开头一致
      if (firstId && secondId) {
        const m1 = await call('queue', { operation: 'move', from: 0, to: 1, revision: R, trackId: firstId });
        const after1 = m1.ok ? m1.data : null;
        const m2 = after1
          ? await call('queue', { operation: 'move', from: 1, to: 0, revision: after1.revision, trackId: firstId })
          : null;
        const after2 = m2 && m2.ok ? m2.data : null;
        result.moveRoundTrip = {
          first: after1 ? {
            ok: true, revisionDelta: after1.revision - R,
            order: (after1.tracks || []).map(t => t.index + ':' + t.title),
            currentIndex: after1.currentIndex,
            previous: after1.previous?.kind, next: after1.next?.kind
          } : { ok: false, error: m1.error },
          back: after2 ? {
            ok: true, revisionDelta: after2.revision - R,
            order: (after2.tracks || []).map(t => t.index + ':' + t.title),
            currentIndex: after2.currentIndex,
            previous: after2.previous?.kind, next: after2.next?.kind,
            orderRestored: JSON.stringify((after2.tracks || []).map(t => t.title)) === JSON.stringify((d0.tracks || []).map(t => t.title))
          } : { ok: false, error: m2 ? m2.error : 'skipped' }
        };
      }

      // 5) 「更多」菜单：播放列表清单 + 两条不产生副作用的拒绝路径
      const more = await call('more', { operation: 'get' });
      result.moreGet = more.ok ? {
        ok: true, keys: Object.keys(more.data).sort(), count: (more.data.playlists || []).length,
        playlists: more.data.playlists
      } : { ok: false, error: more.error };
      result.moreRejects = {
        unknownOperation: (await call('more', { operation: 'launchRocket', trackId: 'x' })).error || 'ACCEPTED(BAD)',
        wrongTrackId: (await call('more', { operation: 'bogus', trackId: 'definitely-not-current' })).error || 'ACCEPTED(BAD)'
      };
      return result;
    });

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({
      exe, out: OUT,
      total: out.queueGet?.total, revision: out.queueGet?.revision, order: out.queueGet?.order,
      tracks: (out.queueGet?.tracks || []).length, pageOffset: out.pageOffset1?.offset,
      rejects: out.rejects, moveRoundTrip: out.moveRoundTrip?.back?.orderRestored,
      playlists: out.moreGet?.count, moreRejects: out.moreRejects
    }, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
