// 一次性探针：把 queue / nowPlaying 的原始回包落到文件，用于改动前后逐字段比对。
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
const out = process.argv[3] || '_w13_queue_probe.json';
const wait = ms => new Promise(r => setTimeout(r, ms));

(async () => {
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
    await page.waitForFunction(() => (window.CelesteShell?.getLibrary()?.length || 0) > 4000, null, { timeout: 90000 });
    const data = await page.evaluate(async () => ({
      queue: await CelesteTransport.request('queue', { operation: 'get' }),
      nowPlaying: await CelesteTransport.request('nowPlaying', {}),
      home: await CelesteTransport.request('home', { operation: 'get', offsetMinutes: 0, today: new Date().toISOString().slice(0, 10) }),
      folders: await CelesteTransport.request('folders', { operation: 'roots' }),
      audioChain: await CelesteTransport.request('audioChain', {}),
      audioReadout: await CelesteTransport.request('audioReadout', {}),
      settings: await CelesteTransport.request('appSettings', { operation: 'get' }),
      updateStatus: await CelesteTransport.request('appSettings', { operation: 'update', action: 'status' }),
      hotkeys: await CelesteTransport.request('appSettings', { operation: 'hotkeys', action: 'get' })
    }));
    fs.writeFileSync(out, JSON.stringify(data, null, 2));
    console.log('WROTE', out, 'queueTracks=', data.queue?.tracks?.length, 'previous=', JSON.stringify(data.queue?.previous), 'next=', JSON.stringify(data.queue?.next));
    await page.evaluate(() => { try { CelesteShell.send('window', { action: 'close' }); } catch { } });
    for (let i = 0; i < 20 && child.exitCode === null; i++) await wait(300);
  } finally {
    try { await browser.close(); } catch { }
    if (child.exitCode === null) child.kill();
  }
  process.exit(0);
})().catch(e => { console.error('FAIL', e); process.exit(1); });
