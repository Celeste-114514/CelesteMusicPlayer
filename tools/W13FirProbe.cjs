// FIR / 房间校正参数基线探针：真实进程 + CDP，迁移前后各跑一次逐字段对比。
// 只改设置、不播放；备份并还原 room-correction.json，IR 用探针自己生成的 PCM WAV。
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13FirProbe.cjs <exe> <out.json>
// 输出三段：main（正常路径，迁移前后必须逐字段一致）/ restart / noIr（IR 文件被删，只比 error 与落盘）
const fs = require('fs'), path = require('path'), os = require('os'), { spawn } = require('child_process');
const { chromium } = require('playwright-core');

const wait = ms => new Promise(r => setTimeout(r, ms));
const CONFIG = path.join(process.env.LOCALAPPDATA, 'CelesteMusicPlayer');
const ROOM = path.join(CONFIG, 'room-correction.json');
const IR = path.join(os.tmpdir(), 'celeste-fir-probe-ir.wav');

// 1024 采样 / 立体声 / 44.1k / 16-bit PCM：ConvolutionIr.Load 能读的最小可用形态
function writeIr() {
  const frames = 1024, ch = 2, rate = 44100, bits = 16;
  const data = Buffer.alloc(frames * ch * (bits / 8));
  for (let i = 0; i < frames; i++) {
    const v = Math.round(Math.sin(i / 32) * 8000);
    data.writeInt16LE(v, i * 4);
    data.writeInt16LE(v, i * 4 + 2);
  }
  const head = Buffer.alloc(44);
  head.write('RIFF', 0); head.writeUInt32LE(36 + data.length, 4); head.write('WAVE', 8);
  head.write('fmt ', 12); head.writeUInt32LE(16, 16); head.writeUInt16LE(1, 20);
  head.writeUInt16LE(ch, 22); head.writeUInt32LE(rate, 24);
  head.writeUInt32LE(rate * ch * bits / 8, 28); head.writeUInt16LE(ch * bits / 8, 32);
  head.writeUInt16LE(bits, 34);
  head.write('data', 36); head.writeUInt32LE(data.length, 40);
  fs.writeFileSync(IR, Buffer.concat([head, data]));
}

const readRoom = () => {
  if (!fs.existsSync(ROOM)) return null;
  try { return JSON.parse(fs.readFileSync(ROOM, 'utf8')); } catch { return 'unreadable'; }
};

const savedRoom = fs.existsSync(ROOM) ? fs.readFileSync(ROOM) : null;

async function open(exe) {
  const child = spawn(exe, ['--w7-background-test'], { stdio: 'ignore' });
  let browser;
  // 连不上通常是上一个进程的 WebView2 还没退干净（9347 端口被占），多等一会儿再试
  for (let i = 0; i < 120; i++) {
    try { browser = await chromium.connectOverCDP('http://127.0.0.1:9347'); break; }
    catch { await wait(500); }
  }
  if (!browser) { child.kill(); throw Error('CDP unavailable'); }
  let page;
  for (let i = 0; i < 90; i++) {
    page = browser.contexts()[0].pages().find(p => p.url().includes('celeste.internal'));
    if (page) break;
    await wait(500);
  }
  await page.waitForFunction(() => window.CelesteTransport && window.CelesteShell);
  await page.exposeFunction('readRoom', readRoom);
  return { child, browser, page };
}

async function close(child, browser, page) {
  try {
    if (page) await page.evaluate(() => CelesteShell.send('window', { action: 'close' }));
  } catch { /* 页面已关 */ }
  for (let i = 0; i < 40 && child.exitCode === null; i++) await wait(250);
  if (child.exitCode === null) { child.kill(); await new Promise(r => child.once('exit', r)); }
  await browser?.close();
  await wait(2500);
}

(async () => {
  writeIr();
  fs.writeFileSync(ROOM, JSON.stringify({
    Enabled: false, IrPath: IR, GainDb: 0, TrimDb: 0,
    IrName: path.basename(IR), IrTapCount: 1024, IrSampleRate: 44100, IrChannels: 2
  }, null, 2));

  const out = { ir: IR, main: null, restart: null, noIr: null };
  let s = await open(process.argv[2]);
  try {
    out.main = await s.page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const moduleFir = () => call('module', { module: 'fir' });
      const data = { initial: await call('get'), modules: await call('modules'), fir: await moduleFir(), steps: [] };
      const steps = [
        ['RoomIrEnabledToggle', true],      // 有 IR → 允许启用
        ['RoomIrGainSlider', 4.5],
        ['RoomTrimSlider', -6],
        ['RoomIrGainSlider', 999],          // 上越界 → 12
        ['RoomTrimSlider', 99],             // 上越界 → 6
        ['RoomTrimSlider', -99],            // 下越界 → -24
        ['DspPowerFir', false],             // 电源往返
        ['DspPowerFir', true],
        ['RoomIrEnabledToggle', false],     // 关掉后路径必须还在
        ['RoomIrGainSlider', 0],            // 收尾回中性，供重启比对
        ['RoomTrimSlider', 0]
      ];
      for (const [name, value] of steps) {
        let reply;
        try { reply = await call('setControl', { module: 'fir', name, value }); }
        catch (e) { reply = { error: String(e && e.message || e) }; }
        data.steps.push({ name, value, reply, fir: await moduleFir(), disk: await window.readRoom() });
      }
      return data;
    });

    await close(s.child, s.browser, s.page);

    // 重启：参数必须回到盘中值
    s = await open(process.argv[2]);
    out.restart = await s.page.evaluate(async () => ({
      fir: await CelesteTransport.request('dsp', { operation: 'module', module: 'fir' }),
      disk: await window.readRoom()
    }));
    await close(s.child, s.browser, s.page);

    // IR 文件消失：只比"有没有报错"与"落盘有没有被改"，不比界面值（旧版在这里有半改状态）
    fs.unlinkSync(IR);
    s = await open(process.argv[2]);
    out.noIr = await s.page.evaluate(async () => {
      const call = (operation, extra = {}) => CelesteTransport.request('dsp', { operation, ...extra });
      const out = { fir: await call('module', { module: 'fir' }), disk0: await window.readRoom(), steps: [] };
      for (const [name, value] of [['RoomIrEnabledToggle', true], ['DspPowerFir', true], ['RoomTrimSlider', -3]]) {
        let reply;
        try { reply = await call('setControl', { module: 'fir', name, value }); }
        catch (e) { reply = { error: String(e && e.message || e) }; }
        out.steps.push({ name, value, error: reply.error ?? null, ok: reply.ok ?? null, disk: await window.readRoom() });
      }
      return out;
    });
    await close(s.child, s.browser, s.page);

    fs.writeFileSync(process.argv[3], JSON.stringify(out, null, 2));
    console.log('WROTE FIR baseline/scenario', process.argv[3]);
  } finally {
    await s?.browser?.close().catch(() => {});
    if (savedRoom) fs.writeFileSync(ROOM, savedRoom);
    else if (fs.existsSync(ROOM)) fs.unlinkSync(ROOM);
    if (fs.existsSync(IR)) fs.unlinkSync(IR);
  }
})().catch(e => { console.error(e); process.exitCode = 1; });
