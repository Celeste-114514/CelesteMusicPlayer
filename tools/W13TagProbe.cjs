// W13 标签页回包采集（tags 命令）：list / read / write / batchTransform / rename / cover /
// 歌词与在线搜索的拒绝路径。用于「搬动前后逐字段比对」。
//
// 需要先把测试目录配进 LibraryWatchFolders（app-settings.json），否则没有可写的曲目。
// 跑完要还原 app-settings.json、library.db 与测试素材。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13TagProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_tags.json'));
const FIX = '_w13tagfix';

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

    // 等测试目录被扫进曲库（后台补库，可能要几秒）
    await page.waitForFunction((fix) => {
      const lib = (window.CelesteShell && window.CelesteShell.getLibrary && window.CelesteShell.getLibrary()) || [];
      return lib.some(t => String(t.sortPath || '').toLowerCase().includes(fix));
    }, FIX, { timeout: 180000 });

    const out = await page.evaluate(async (fix) => {
      const call = async (command, payload) => {
        try { return { ok: true, data: await CelesteTransport.request(command, payload) }; }
        catch (e) { return { ok: false, error: String((e && e.message) || e) }; }
      };
      const base = (p) => String(p || '').split(/[\\/]/).pop();
      const lib = window.CelesteShell.getLibrary();
      const fixTracks = lib.filter(t => String(t.sortPath || '').toLowerCase().includes(fix))
        .sort((a, b) => String(a.sortPath).localeCompare(String(b.sortPath)));
      const ids = fixTracks.map(t => t.id);
      const result = { fixture: { count: ids.length, files: fixTracks.map(t => base(t.sortPath)), ids } };
      if (!ids.length) return result;

      const short = (v) => (typeof v === 'string' && v.startsWith('data:') ? 'data-len:' + v.length : v);
      const rowsOf = (data) => (data.rows || []).map(r => ({ ...r, cover: undefined }));
      const err = (r) => (r.ok ? 'ACCEPTED(BAD)' : r.error);

      // 1) list：显式工作集（顺序、字段、值）
      const l1 = await call('tags', { operation: 'list', scopeIds: ids, all: true });
      result.listScope = l1.ok ? { keys: Object.keys(l1.data).sort(), total: l1.data.total, offset: l1.data.offset, rowCount: (l1.data.rows || []).length, rows: rowsOf(l1.data) } : { error: l1.error };

      // 2) list：查询过滤 + 分页
      const l2 = await call('tags', { operation: 'list', scopeIds: ids, query: 'Artio', all: true });
      result.listQuery = l2.ok ? { total: l2.data.total, files: (l2.data.rows || []).map(r => r.file) } : { error: l2.error };
      const l3 = await call('tags', { operation: 'list', scopeIds: ids, limit: 2, offset: 2 });
      result.listPaged = l3.ok ? { total: l3.data.total, offset: l3.data.offset, files: (l3.data.rows || []).map(r => r.file) } : { error: l3.error };

      // 3) list：全库（只比规模，不比顺序）
      const l4 = await call('tags', { operation: 'list', limit: 3 });
      result.listLibrary = l4.ok ? { keys: Object.keys(l4.data).sort(), total: l4.data.total, offset: l4.data.offset, rowCount: (l4.data.rows || []).length } : { error: l4.error };

      // 4) list：未知 id 直接被丢掉；空工作集
      const l5a = await call('tags', { operation: 'list', scopeIds: [ids[0], 'ghost-id'], all: true });
      result.listUnknownScope = l5a.ok ? { total: l5a.data.total, files: (l5a.data.rows || []).map(r => r.file) } : { error: l5a.error };
      const l5b = await call('tags', { operation: 'list', scopeIds: [] });
      result.listEmptyScope = l5b.ok ? { total: l5b.data.total, rowCount: (l5b.data.rows || []).length } : { error: l5b.error };

      // 5) read：完整字段（含封面长度桶）
      const r1 = await call('tags', { operation: 'read', ids: [ids[0], ids[1]], cover: true });
      result.read = r1.ok ? {
        keys: Object.keys(r1.data).sort(),
        count: (r1.data.tracks || []).length,
        tracks: (r1.data.tracks || []).map(t => ({ ...t, cover: short(t.cover) }))
      } : { error: r1.error };

      // 6) read：不存在的 id
      const r2 = await call('tags', { operation: 'read', ids: ['no-such-track'] });
      result.readMissing = r2.ok ? (r2.data.tracks || []).map(t => ({ id: t.id, ok: t.ok, error: t.error })) : { error: r2.error };

      // 7) write：单组字段写多首（快速编辑）
      const w1 = await call('tags', { operation: 'write', ids: [ids[0]], fields: { title: 'PROBE TITLE', artist: 'PROBE ARTIST', year: '1999', track: '7' } });
      result.writeFields = w1.ok ? (w1.data.results || []).map(x => ({ id: x.id, ok: x.ok, skipped: x.skipped, error: x.error })) : { error: w1.error };
      const r3 = await call('tags', { operation: 'read', ids: [ids[0]] });
      result.writeReadBack = r3.ok ? (r3.data.tracks || []).map(t => ({ title: t.title, artist: t.artist, year: t.year, track: t.track })) : { error: r3.error };

      // 8) write：逐行字段（表格内联编辑）
      const w2 = await call('tags', { operation: 'write', rows: [{ id: ids[1], fields: { album: 'PROBE ALBUM' } }, { id: ids[2], fields: { genre: 'ProbeGenre' } }] });
      result.writeRows = w2.ok ? (w2.data.results || []).map(x => ({ id: x.id, ok: x.ok, skipped: x.skipped, error: x.error })) : { error: w2.error };

      // 9) write：坏值必须在任何文件落盘前被拒（组事务）
      const w3 = await call('tags', { operation: 'write', ids: [ids[3]], fields: { year: 'abc' } });
      result.writeBadValue = w3.ok ? (w3.data.results || []).map(x => ({ ok: x.ok, skipped: x.skipped, error: x.error })) : { error: w3.error };
      const w4 = await call('tags', { operation: 'write', ids: [ids[3]], fields: {} });
      result.writeNoFields = err(w4);
      const w5 = await call('tags', { operation: 'write', ids: Array.from({ length: 2001 }, (_, i) => 'x' + i), fields: { title: 'x' } });
      result.writeTooMany = err(w5);

      // 10) batchTransform：只回预览，不落盘
      for (const spec of [
        { name: 'replace', mode: 'replace', field: 'title', find: 'PROBE', replacement: 'X' },
        { name: 'upper', mode: 'upper', field: 'title' },
        { name: 'lower', mode: 'lower', field: 'artist' },
        { name: 'titleCase', mode: 'titleCase', field: 'title' },
        { name: 'fromFilename', mode: 'fromFilename' },
        { name: 'clearAll', mode: 'clearAll' }
      ]) {
        const payload = { operation: 'batchTransform', ids: [ids[0], ids[1]], ...spec };
        delete payload.name;
        const b = await call('tags', payload).catch(e => ({ ok: false, error: String(e) }));
        result['batch_' + spec.name] = b.ok ? { count: (b.data.rows || []).length, rows: b.data.rows } : { error: b.error };
      }
      const bBad = await call('tags', { operation: 'batchTransform', ids: [ids[0]], mode: 'upper', field: 'nope' });
      result.batchBadField = err(bBad);

      // 11) rename：拒绝路径
      result.renameReject = {
        badName: err(await call('tags', { operation: 'rename', rows: [{ id: ids[0], file: 'a/b' }] })),
        emptyName: err(await call('tags', { operation: 'rename', rows: [{ id: ids[0], file: '' }] })),
        duplicate: err(await call('tags', { operation: 'rename', rows: [{ id: ids[3], file: 'Same.m4a' }, { id: ids[4], file: 'Same.m4a' }] })),
        missingId: err(await call('tags', { operation: 'rename', rows: [{ id: 'no-such-track', file: 'x.m4a' }] })),
        emptyRows: err(await call('tags', { operation: 'rename', rows: [] }))
      };

      // 12) rename：真改一次再改回来
      const oldFile = base(fixTracks[5].sortPath);
      const newFile = oldFile.replace(/\.m4a$/i, '-renamed.m4a');
      const rn = await call('tags', { operation: 'rename', rows: [{ id: ids[5], file: newFile }] });
      result.rename = rn.ok ? { ok: rn.data.ok, count: rn.data.count, remapKeys: Object.keys(rn.data.remap || {}).length } : { error: rn.error };
      const l5 = await call('tags', { operation: 'list', scopeIds: ids, all: true });
      result.renameSeen = l5.ok ? (l5.data.rows || []).map(r => r.file) : { error: l5.error };
      const backId = rn.ok ? Object.values(rn.data.remap || {})[0] : null;
      const rnBack = backId ? await call('tags', { operation: 'rename', rows: [{ id: backId, file: oldFile }] }) : { ok: false, error: 'no remap id' };
      result.renameBack = rnBack.ok ? { ok: rnBack.data.ok, count: rnBack.data.count } : { error: rnBack.error };

      // 13) cover：读 → 删 → 再删
      const c1 = await call('tags', { operation: 'read', ids: [ids[4]], cover: true });
      result.coverBefore = c1.ok ? short((c1.data.tracks || [])[0]?.cover) : { error: c1.error };
      const c2 = await call('tags', { operation: 'coverRemove', ids: [ids[4]] });
      result.coverRemove = c2.ok ? { ok: c2.data.ok, count: c2.data.count } : { error: c2.error };
      const c3 = await call('tags', { operation: 'read', ids: [ids[4]], cover: true });
      result.coverAfterRemove = c3.ok ? short((c3.data.tracks || [])[0]?.cover) : { error: c3.error };
      const c4 = await call('tags', { operation: 'coverRemove', ids: [ids[4]] });
      result.coverRemoveAgain = c4.ok ? { ok: c4.data.ok, count: c4.data.count } : { error: c4.error };
      const c5 = await call('tags', { operation: 'coverNope', ids: [ids[4]] });
      result.coverUnknown = err(c5);

      // 14) 在线搜索 / 歌词：只测拒绝路径（不碰网络）
      result.toolsReject = {
        searchOnlineEmpty: err(await call('tags', { operation: 'searchOnline', source: 'musicbrainz', title: '', artist: '', album: '' })),
        searchLyricsEmpty: err(await call('tags', { operation: 'searchLyrics', source: 'netease', artist: '', title: '' })),
        lyricPreviewNoSearch: err(await call('tags', { operation: 'lyricPreview', searchId: 'nope', index: 0 })),
        onlineDetailNoSearch: err(await call('tags', { operation: 'onlineDetail', searchId: 'nope', index: 0 })),
        coverOnlineNoSearch: err(await call('tags', { operation: 'coverOnline', searchId: 'nope', index: 0 })),
        lyricBatchTooEarly: err(await call('tags', { operation: 'lyricSave', searchId: 'nope', index: 0 })),
        noTargets: err(await call('tags', { operation: 'lyricBatchStart', ids: [], source: 'netease' })),
        unknown: err(await call('tags', { operation: 'launchRocket' }))
      };

      // 15) 批量歌词：状态形状 + 停止
      const s1 = await call('tags', { operation: 'lyricBatchStatus' });
      result.lyricBatchStatus = s1.ok ? { keys: Object.keys(s1.data).sort(), running: s1.data.running, summary: s1.data.summary, error: s1.data.error } : { error: s1.error };
      const s2 = await call('tags', { operation: 'lyricBatchStop' });
      result.lyricBatchStop = s2.ok ? s2.data : { error: s2.error };
      const s3 = await call('tags', { operation: 'lyricBatchStart', ids: ids, source: 'netease', location: 'lyricFolder' });
      result.lyricBatchNoFolder = err(s3);

      return result;
    }, FIX);

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({
      exe, out: OUT,
      fixture: out.fixture?.files,
      listTotal: out.listScope?.total,
      readCount: out.read?.count,
      writeOk: out.writeFields,
      rename: out.rename, renameSeen: out.renameSeen,
      coverBefore: out.coverBefore, coverAfterAsk: out.coverAfterRemove,
      rejects: out.renameReject
    }, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
