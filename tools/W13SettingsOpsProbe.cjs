// W13 设置域命令探针：在真实进程里逐个驱动 appSettings 的操作，验证从 MainWindow 搬进
// WebSettingsService 之后每个回包的字段还在。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13SettingsOpsProbe.cjs <exe路径>
//
// 覆盖：get / patch（含冲突分支 + 真实写入后还原）/ reset（改完按原值还原）/
//       folders list / webdav test（未配置分支）/ credentials（空=保留）/ hotkeys get / miniStyles get
// 不覆盖：pickFolder / pickFile / pickLyricFont / export / import —— 都要弹原生对话框，
//       没有脚本入口，只能靠人手点（见交接文档「没验到的」）。
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(__dirname, '../_w13_settings_ops_probe.json');

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

    const jsErrors = [];
    page.on('pageerror', e => jsErrors.push(e.message));

    const result = await page.evaluate(async () => {
      const call = async (command, payload) => {
        try { return { ok: true, data: await CelesteTransport.request(command, payload) }; }
        catch (e) { return { ok: false, error: String(e && e.message || e) }; }
      };
      const out = {};

      const base = await call('appSettings', { operation: 'get' });
      if (!base.ok) { out.get = { ok: false, error: base.error }; return out; }
      const g = base.data;
      const ids = Object.keys(g.values || {});
      out.get = {
        ok: true,
        defs: (g.defs || []).length,
        groups: (g.groups || []).length,
        values: ids.length,
        hotkeyDefaults: Object.keys(g.hotkeys?.defaults || {}).length,
        hotkeyFields: Object.keys(g.hotkeys || {}).sort(),
        accountFields: Object.keys(g.accounts || {}).sort(),
        folderCount: g.folderCount,
        hasAbout: !!g.about,
        readOnlyIds: (g.defs || []).filter(d => d.readOnly).map(d => d.id)
      };

      // —— patch 冲突分支：故意报一个过期修订号，应回 conflict 且不落盘 ——
      const probeId = 'ShowTaskbarProgress';
      const before = g.values[probeId];
      const conflict = await call('appSettings', {
        operation: 'patch',
        revision: (g.revision || 0) - 1,
        changes: { [probeId]: !before }
      });
      const afterConflict = await call('appSettings', { operation: 'get' });
      out.patchConflict = {
        ok: conflict.ok,
        conflict: conflict.data?.conflict,
        hasValues: !!conflict.data?.values && Object.keys(conflict.data.values || {}).length > 0,
        wroteAnyway: afterConflict.ok ? afterConflict.data.values[probeId] !== before : 'unknown'
      };

      // —— patch 真实写入：改一个无副作用的布尔项，再改回原值 ——
      const write = await call('appSettings', { operation: 'patch', changes: { [probeId]: !before } });
      const restored = await call('appSettings', { operation: 'patch', changes: { [probeId]: before } });
      const afterWrite = await call('appSettings', { operation: 'get' });
      out.patchWrite = {
        ok: write.ok && restored.ok,
        flippedOk: write.data?.conflict === false && write.data?.values?.[probeId] === !before,
        note: write.data?.note,
        revisionAdvanced: (write.data?.revision || 0) > (g.revision || 0),
        restoredOk: afterWrite.ok ? afterWrite.data.values[probeId] === before : 'unknown'
      };

      // —— 只读 / 未知字段的拒绝路径 ——
      const readOnlyReject = await call('appSettings', { operation: 'patch', changes: { OutputMode: 'wasapi' } });
      const unknownReject = await call('appSettings', { operation: 'patch', changes: { NotARealField: 1 } });
      const emptyReject = await call('appSettings', { operation: 'patch', changes: {} });
      const badOp = await call('appSettings', { operation: 'nope' });
      out.rejects = {
        readOnly: readOnlyReject.ok ? 'ACCEPTED(BAD)' : readOnlyReject.error,
        unknown: unknownReject.ok ? 'ACCEPTED(BAD)' : unknownReject.error,
        empty: emptyReject.ok ? 'ACCEPTED(BAD)' : emptyReject.error,
        unknownOperation: badOp.ok ? 'ACCEPTED(BAD)' : badOp.error
      };

      // —— folders list ——
      const folders = await call('appSettings', { operation: 'folders', action: 'list' });
      out.foldersList = {
        ok: folders.ok,
        folderCount: folders.data?.folders?.length,
        manualCount: folders.data?.manualCount,
        sample: (folders.data?.folders || []).slice(0, 3)
      };
      const foldersBad = await call('appSettings', { operation: 'folders', action: 'explode' });
      out.foldersReject = foldersBad.ok ? 'ACCEPTED(BAD)' : foldersBad.error;

      // —— webdav test：本机没配服务器，走的是「还没填地址」分支，不发网络请求 ——
      const webdav = await call('appSettings', { operation: 'webdav', action: 'test' });
      out.webdavTest = webdav.ok
        ? { ok2: webdav.data.ok, headline: webdav.data.headline }
        : { ok: false, error: webdav.error };
      const webdavBad = await call('appSettings', { operation: 'webdav', action: 'sync' });
      out.webdavReject = webdavBad.ok ? 'ACCEPTED(BAD)' : webdavBad.error;

      // —— credentials：空 fields = 保留原值（不写、不清）——
      const credKeys = ['configured', 'message'];
      const fanart = await call('appSettings', { operation: 'credentials', service: 'fanart', fields: {} });
      const discogs = await call('appSettings', { operation: 'credentials', service: 'discogs', fields: {} });
      const webdavPwd = await call('appSettings', { operation: 'credentials', service: 'webdav', fields: {} });
      const credBad = await call('appSettings', { operation: 'credentials', service: 'spotify', fields: {} });
      const accountFields = (a) => a.ok ? Object.keys(a.data).sort() : null;
      out.credentials = {
        fanartKeys: accountFields(fanart), fanart: fanart.data?.[credKeys[0]],
        discogsKeys: accountFields(discogs), discogs: discogs.data,
        webdavKeys: accountFields(webdavPwd), webdav: webdavPwd.data?.[credKeys[0]],
        unknownService: credBad.ok ? 'ACCEPTED(BAD)' : credBad.error
      };

      // —— hotkeys get ——
      const hotkeys = await call('appSettings', { operation: 'hotkeys', action: 'get' });
      out.hotkeys = {
        ok: hotkeys.ok,
        fields: hotkeys.ok ? Object.keys(hotkeys.data).sort() : null,
        defaults: hotkeys.data ? Object.keys(hotkeys.data.defaults || {}).length : null,
        enabled: hotkeys.data?.enabled
      };
      const hotkeysBad = await call('appSettings', { operation: 'hotkeys', action: 'teleport' });
      out.hotkeysReject = hotkeysBad.ok ? 'ACCEPTED(BAD)' : hotkeysBad.error;

      // —— miniStyles get ——
      const mini = await call('appSettings', { operation: 'miniStyles', action: 'get' });
      out.miniStyles = { ok: mini.ok, keys: mini.ok ? Object.keys(mini.data).sort() : null, sample: mini.data };

      // —— reset：挑 playback 组，先记原值 → 重置 → 按原值写回 → 对比，必须完全还原 ——
      // mini 组是纯界面分组（样式归 miniStyles 管），没有可恢复字段，单独验证它的拒绝分支。
      const group = 'playback';
      const groupIds = (g.defs || []).filter(d => d.group === group && !d.readOnly).map(d => d.id);
      const original = {};
      for (const id of groupIds) original[id] = g.values[id];
      // 先把 CrossfadeMs 顶到一个肯定不是默认值的数，否则"重置"看不出效果（本机原值就等于默认）。
      const plantId = 'CrossfadeMs';
      const planted = original[plantId] === 5000 ? 1000 : 5000;
      const plant = await call('appSettings', { operation: 'patch', changes: { [plantId]: planted } });
      const reset = await call('appSettings', { operation: 'reset', group });
      const afterReset = await call('appSettings', { operation: 'get' });
      const resetChanged = groupIds.filter(id => afterReset.ok && JSON.stringify(afterReset.data.values[id]) !== JSON.stringify(original[id]));
      const restoreChanges = {};
      for (const id of groupIds) restoreChanges[id] = original[id];
      const restore = Object.keys(restoreChanges).length
        ? await call('appSettings', { operation: 'patch', changes: restoreChanges })
        : { ok: true };
      const afterRestore = await call('appSettings', { operation: 'get' });
      const restoreMiss = groupIds.filter(id => afterRestore.ok && JSON.stringify(afterRestore.data.values[id]) !== JSON.stringify(original[id]));
      const resetEmpty = await call('appSettings', { operation: 'reset', group: 'mini' });
      out.reset = {
        ok: reset.ok,
        error: reset.ok ? null : reset.error,
        ids: groupIds,
        planted,
        plantOk: plant.ok && plant.data?.values?.[plantId] === planted,
        valueAfterReset: afterReset.ok ? afterReset.data.values[plantId] : 'unknown',
        message: reset.data?.message,
        changedByReset: resetChanged,
        restoreOk: restore.ok && restore.data?.conflict === false,
        restoreMissed: restoreMiss,
        emptyGroupRejects: resetEmpty.ok ? 'ACCEPTED(BAD)' : resetEmpty.error
      };
      const resetBad = await call('appSettings', { operation: 'reset', group: 'nope' });
      out.resetReject = resetBad.ok ? 'ACCEPTED(BAD)' : resetBad.error;

      return out;
    });

    const report = { exe, ...result, jsErrors };
    fs.writeFileSync(OUT, JSON.stringify(report, null, 2), 'utf8');
    console.log(JSON.stringify(report, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
