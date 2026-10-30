// W13 目录浏览回包采集：roots / children / songs 三条命令的原始回包 + 拒绝路径。
// 用于「搬动前后逐字段比对」。只读：不播放、不入队、不改设置、不删文件。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13FolderWireProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_folders.json'));

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
      const strip = (nodes) => nodes.map(n => ({ name: n.name, folder: n.folder, pathTail: String(n.path || '').split(/[\\/]/).slice(-2).join('/') }));
      const result = {};

      // 1) roots
      const roots = await call('folders', { operation: 'roots' });
      result.roots = roots.ok ? {
        ok: true, keys: Object.keys(roots.data).sort(), rootCount: roots.data.rootCount, total: roots.data.total,
        nodeCount: (roots.data.nodes || []).length, nodes: strip(roots.data.nodes || []).slice(0, 12)
      } : { ok: false, error: roots.error };
      if (!roots.ok) return result;

      const rootNodes = roots.data.nodes || [];
      const folderRoot = rootNodes.find(n => n.folder);
      const fileRoot = rootNodes.find(n => !n.folder);
      result.hasFolderRoot = !!folderRoot;

      // 2) children（真实磁盘一层）
      if (folderRoot) {
        const children = await call('folders', { operation: 'children', nodeId: folderRoot.id });
        result.children = children.ok ? {
          ok: true, keys: Object.keys(children.data).sort(), count: (children.data.nodes || []).length,
          nodes: strip(children.data.nodes || []).slice(0, 20)
        } : { ok: false, error: children.error };

        // 3) songs：优先挑一个有音频的文件夹（整根也行，索引过的直接克隆）
        const kids = children.ok ? children.data.nodes || [] : [];
        const pickFolder = kids.find(n => n.folder) || folderRoot;
        const songs = await call('folders', { operation: 'songs', nodeId: pickFolder.id });
        result.songs = songs.ok ? {
          ok: true, keys: Object.keys(songs.data).sort(), nodePathMatches: songs.data.path === pickFolder.path,
          count: (songs.data.tracks || []).length,
          trackKeys: (songs.data.tracks || []).length ? Object.keys(songs.data.tracks[0]).sort() : [],
          head: (songs.data.tracks || []).slice(0, 8).map(t => ({ title: t.title, songTitle: t.songTitle, artist: t.artist, album: t.album, duration: Math.round((t.duration || 0) * 1000) / 1000 })),
          // 封面通道：登记簿里存了曲目 → 内部 url 能取到图
          coverStatus: await (async () => {
            const id = (songs.data.tracks || [])[0]?.id;
            if (!id) return 'no-track';
            try { const r = await fetch('https://celeste-cover.internal/cover/' + id); return r.status; }
            catch (e) { return 'ERR:' + String((e && e.message) || e).slice(0, 40); }
          })()
        } : { ok: false, error: songs.error };
        result.songsNodeIsFolder = !!pickFolder.folder;

        // 4) 单文件节点（若根下就有音频文件）
        const fileKid = kids.find(n => !n.folder);
        result.fileNode = fileKid ? await (async () => {
          const one = await call('folders', { operation: 'songs', nodeId: fileKid.id });
          return one.ok
            ? { ok: true, count: (one.data.tracks || []).length, first: (one.data.tracks || [])[0]?.title, nodeFolder: one.data.path === fileKid.path }
            : { ok: false, error: one.error };
        })() : null;

        // 4b) 对文件节点做 children：必须被拒（类型检查）
        result.childrenOnFileNode = fileKid
          ? ((await call('folders', { operation: 'children', nodeId: fileKid.id })).error || 'ACCEPTED(BAD)')
          : 'no-file-kid';

        // 4c) 整根递归列歌：跨子目录的顺序（碟号/轨道号/文件名）
        const rootSongs = await call('folders', { operation: 'songs', nodeId: folderRoot.id });
        result.rootSongs = rootSongs.ok ? {
          ok: true, count: (rootSongs.data.tracks || []).length,
          order: (rootSongs.data.tracks || []).map(t => (t.title || '').replace(/^.*[\\/]/, ''))
        } : { ok: false, error: rootSongs.error };

        // 5) 拒绝路径：拿旧 id（roots 之外的新 epoch 不成立）与错类型
        result.rejects = {
          unknownOperation: (await call('folders', { operation: 'launchRocket' })).error || 'ACCEPTED(BAD)',
          badNodeId: (await call('folders', { operation: 'children', nodeId: 'no-such-node' })).error || 'ACCEPTED(BAD)',
          childrenOnFile: fileRoot
            ? ((await call('folders', { operation: 'children', nodeId: fileRoot.id })).error || 'ACCEPTED(BAD)')
            : 'no-file-root'
        };

        // 6) epoch：refresh 后旧 id 必须作废
        const staleId = pickFolder.id;
        await call('folders', { operation: 'roots' });
        result.staleAfterRoots = (await call('folders', { operation: 'songs', nodeId: staleId })).error || 'ACCEPTED(BAD)';
      }
      return result;
    });

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({
      exe, out: OUT,
      roots: out.roots?.nodeCount, rootCount: out.roots?.rootCount, total: out.roots?.total,
      children: out.children?.count, songs: out.songs?.count, cover: out.songs?.coverStatus,
      rejects: out.rejects, staleAfterRoots: out.staleAfterRoots
    }, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
