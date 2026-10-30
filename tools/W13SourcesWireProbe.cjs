// W13 在线来源回包采集：sources 命令的只读回包与拒绝路径，用于搬动前后逐字段比对。
// 只读：不新建来源、不改开关、不触发同步、不联网。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13SourcesWireProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_sources.json'));

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
      const reject = async (payload) => { const r = await call('sources', payload); return r.ok ? 'ACCEPTED(BAD)' : r.error; };
      const result = {};

      const list = await call('sources', { operation: 'list' });
      result.list = list;
      if (list.ok) {
        result.summary = {
          keys: Object.keys(list.data).sort(),
          providers: list.data.providers,
          sourceCount: (list.data.sources || []).length,
          sourceKeys: list.data.sources?.length ? Object.keys(list.data.sources[0]).sort() : [],
          jobKeys: list.data.sources?.length ? Object.keys(list.data.sources[0].job || {}).sort() : [],
          selectedId: list.data.selectedId
        };
      }

      // 拒绝路径：全部不落盘
      result.rejects = {
        noProvider: await reject({ operation: 'save', name: 'x', endpoint: 'https://a.example.com' }),
        badProvider: await reject({ operation: 'save', provider: 'ftp', name: 'x', endpoint: 'https://a.example.com' }),
        badEndpoint: await reject({ operation: 'save', provider: 'webdav', name: 'x', endpoint: 'not-a-url' }),
        emptyName: await reject({ operation: 'save', provider: 'webdav', name: '   ', endpoint: 'https://a.example.com' }),
        unknownSourceBrowse: await reject({ operation: 'browse', sourceId: 'no-such-source' }),
        unknownSourceTest: await reject({ operation: 'test', sourceId: 'no-such-source' }),
        unknownSourceToggle: await reject({ operation: 'toggle', sourceId: 'no-such-source' }),
        unknownOperation: await reject({ operation: 'launchRocket' })
      };

      // 取消下载：没有在途下载时应当只是返回 cancelled=true
      result.cancelDownload = await call('sources', { operation: 'cancelDownload' });
      return result;
    });

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({ exe, out: OUT, summary: out.summary, rejects: out.rejects, cancelDownload: out.cancelDownload }, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
