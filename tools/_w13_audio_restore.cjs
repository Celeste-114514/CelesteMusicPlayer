// 一次性恢复脚本：把输出模式与设备改回指定值。
// 用法：node tools/_w13_audio_restore.cjs <exe> <模式> <设备id>
const path = require('node:path');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2];
const mode = process.argv[3] || 'Shared';
const deviceId = process.argv[4] || '';
function wait(ms) { return new Promise(r => setTimeout(r, ms)); }

(async () => {
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
    const out = await page.evaluate(async ([mode, deviceId]) => {
      const r = {};
      try { const d = await CelesteTransport.request('audioSettings', { operation: 'setMode', mode }); r.setMode = { ok: true, selectedMode: d.selectedMode, selectedDeviceId: d.selectedDeviceId }; }
      catch (e) { r.setMode = { ok: false, error: String((e && e.message) || e) }; }
      if (deviceId !== null) {
        try { const d = await CelesteTransport.request('audioSettings', { operation: 'setDevice', mode, deviceId }); r.setDevice = { ok: true, selectedDeviceId: d.selectedDeviceId }; }
        catch (e) { r.setDevice = { ok: false, error: String((e && e.message) || e) }; }
      }
      try { const d = await CelesteTransport.request('audioSettings', { operation: 'get' }); r.after = { selectedMode: d.selectedMode, selectedDeviceId: d.selectedDeviceId, devices: (d.devices || []).length }; }
      catch (e) { r.after = { ok: false, error: String((e && e.message) || e) }; }
      return r;
    }, [mode, deviceId]);
    console.log(JSON.stringify(out, null, 2));
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('RESTORE FAILED:', e.message); process.exit(1); });
