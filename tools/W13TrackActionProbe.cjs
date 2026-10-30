// W13 曲目动作线上回包探针：对 trackAction 的「加入队列 / 置顶 / 移出队列」
// 采集队列回包序列，用于「搬动前后逐字段比对」。不碰收藏/评分（会写用户数据）。
// 运行：node tools/W13TrackActionProbe.cjs <exe> <输出.json>
const { spawn } = require('node:child_process');
const fs = require('node:fs');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }
const exe = process.argv[2];
const outFile = process.argv[3] || '_w13_trackaction.json';
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
  const child = spawn(exe, ['--w7-background-test'], { stdio: 'ignore' });
  let browser = null;
  for (let i = 0; i < 60; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; } catch { await wait(1000); }
  }
  if (!browser) throw new Error('CDP 连接失败');
  const result = { exe };
  try {
    let page = null;
    for (let i = 0; i < 90; i++) {
      page = browser.contexts()[0]?.pages().find(p => p.url().includes('celeste.internal'));
      if (page) break;
      await wait(1000);
    }
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 90000 });

    const result0 = await page.evaluate(async () => {
      const lib = CelesteShell.getLibrary().filter(t => !t.isRemote);
      const short = lib.filter(t => t.duration > 5 && t.duration < 90).sort((a, b) => a.duration - b.duration);
      return short.slice(0, 3).map(t => ({ id: t.id, title: t.title }));
    });
    result.picks = result0;
    if (result0.length < 2) { console.log('短曲不足，放弃'); return; }

    // 1) playAll 两首短的（填队列）
    await page.evaluate(ids => CelesteShell.send('playAll', { trackIds: ids }), result0.slice(0, 2).map(t => t.id));
    await wait(1500);
    const q0 = await page.evaluate(async () => await CelesteTransport.request('queue', { operation: 'get' }));

    // 2) 第三首「下一首播放」（走 ResolveById + Insert next）
    await page.evaluate(async payload => await CelesteTransport.request('trackAction', payload),
      { operation: 'next', trackIds: [result0[1].id] });
    await wait(800);
    const q1 = await page.evaluate(async () => await CelesteTransport.request('queue', { operation: 'get' }));

    // 3) 置顶第 2 首（走 ResolveQueueIndices + RemoveAndPin）
    const entry = { index: 1, trackId: q1.tracks[1] ? q1.tracks[1].id || q1.tracks[1].trackId || String(q1.tracks[1].path || '') : '' };
    // 回包里曲目身份字段名不确定：取与 trackAction 校验同源的 trackId —— 从 queue 回包直接拿
    const raw1 = JSON.stringify(q1);
    const idKey = ['trackId', 'id', 'identity'].find(k => q1.tracks?.[0] && k in q1.tracks[0]) || 'trackId';
    entry.trackId = q1.tracks[1][idKey];
    result.idKey = idKey;
    await page.evaluate(async payload => await CelesteTransport.request('trackAction', payload),
      { operation: 'pin', queue: true, revision: q1.revision, entries: [entry] });
    await wait(800);
    const q2 = await page.evaluate(async () => await CelesteTransport.request('queue', { operation: 'get' }));

    // 4) 拒绝路径：旧修订号（必须抛"播放队列已变化"）
    let staleError = '';
    try {
      await page.evaluate(async payload => await CelesteTransport.request('trackAction', payload),
        { operation: 'pin', queue: true, revision: q1.revision, entries: [{ index: 0, trackId: q2.tracks[0][idKey] }] });
    } catch (e) { staleError = String(e.message || e); }
    result.staleError = staleError;

    // 5) 移出队列第 1 首
    await page.evaluate(async payload => await CelesteTransport.request('trackAction', payload),
      { operation: 'removeQueue', queue: true, revision: q2.revision, entries: [{ index: 0, trackId: q2.tracks[0][idKey] }] });
    await wait(800);
    const q3 = await page.evaluate(async () => await CelesteTransport.request('queue', { operation: 'get' }));

    const strip = q => ({ revision: q.revision, index: q.currentIndex, titles: (q.tracks || []).map(t => t.title) });
    result.queueSteps = { afterPlayAll: strip(q0), afterNext: strip(q1), afterPin: strip(q2), afterRemove: strip(q3) };
    console.log(JSON.stringify(result.queueSteps, null, 1));
    console.log('拒绝路径:', staleError ? '已抛（预期）' : '未抛！');
  } finally {
    fs.writeFileSync(outFile, JSON.stringify(result, null, 1));
    try { await browser.close(); } catch { }
    try { child.kill(); } catch { }
  }
})();
