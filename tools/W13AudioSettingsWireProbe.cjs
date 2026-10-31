// W13「音频设置」回包采集：搬动前后逐字段比对。
//
// 状态约束：不播放、不切输出模式、不换输出设备、不弹资源管理器。
// 会动到的都是可还原项 —— 倍速 / 保调 / 睡眠定时 / AB 循环 / 设备记忆开关，
// 每一项改完立刻改回原值（数值以第一次 get 的回包为准）。
// 唯一留下的痕迹是 `abHint` 文案（清空后变成「已清除 A / B。」），只在内存里，退出即复位。
//
// 运行：NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/W13AudioSettingsWireProbe.cjs <exe> <输出json>
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const exe = process.argv[2] || path.resolve(__dirname, '../CelesteMusicPlayer/bin/Release/net10.0-windows10.0.19041.0/win-x64/publish/CelesteMusicPlayer.exe');
const OUT = path.resolve(process.argv[3] || path.join(__dirname, '../_w13_audio_settings.json'));

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
      // 只留判据字段，别把设备名里的「（默认）」这类本地化文案当成不稳定项单列。
      const pick = d => ({
        keys: Object.keys(d).sort(),
        selectedMode: d.selectedMode, selectedDeviceId: d.selectedDeviceId,
        actualMode: d.actualMode, actualDevice: d.actualDevice,
        deviceCount: (d.devices || []).length,
        deviceIds: (d.devices || []).map(x => x.id),
        deviceNames: (d.devices || []).map(x => x.name),
        rate: d.rate, preservePitch: d.preservePitch, dsdDirect: d.dsdDirect,
        a: d.a, b: d.b, abActive: d.abActive, abHint: d.abHint,
        sleepSelection: d.sleepSelection, sleepTracks: d.sleepTracks, sleepMinutes: d.sleepMinutes,
        deviceMemory: d.deviceMemory, canSaveProfile: d.canSaveProfile, hasDeviceProfile: d.hasDeviceProfile
      });
      const result = {};
      const fail = e => ({ ok: false, error: e });

      // 1) 首次读数（同时作为后面还原的基准）
      const g0 = await call('audioSettings', { operation: 'get' });
      if (!g0.ok) { result.get0 = fail(g0.error); return result; }
      const base = g0.data;
      result.get0 = pick(base);

      // 2) 强制重新枚举设备
      const r = await call('audioSettings', { operation: 'refresh' });
      result.refresh = r.ok ? pick(r.data) : fail(r.error);

      // 3) 倍速：改走再改回
      const rUp = await call('audioSettings', { operation: 'setRate', rate: 1.25 });
      result.rateUp = rUp.ok ? { ok: true, rate: rUp.data.rate } : fail(rUp.error);
      const rBack = await call('audioSettings', { operation: 'setRate', rate: base.rate });
      result.rateBack = rBack.ok ? { ok: true, rate: rBack.data.rate } : fail(rBack.error);

      // 4) 保调开关：翻转再翻回
      const pOn = await call('audioSettings', { operation: 'setPitch', preservePitch: !base.preservePitch });
      result.pitchFlipped = pOn.ok ? { ok: true, preservePitch: pOn.data.preservePitch } : fail(pOn.error);
      const pBack = await call('audioSettings', { operation: 'setPitch', preservePitch: base.preservePitch });
      result.pitchRestored = pBack.ok ? { ok: true, preservePitch: pBack.data.preservePitch } : fail(pBack.error);

      // 5) 睡眠定时：三种模式各设一次，最后关闭
      const sh = {};
      for (const [name, payload] of [
        ['afterTracks', { operation: 'setSleep', selection: 7, tracks: 3 }],
        ['custom', { operation: 'setSleep', selection: 8, tracks: 1, minutes: 45 }],
        ['afterTrack', { operation: 'setSleep', selection: 6, tracks: 1 }]
      ]) {
        const s = await call('audioSettings', payload);
        sh[name] = s.ok ? { ok: true, sleepSelection: s.data.sleepSelection, sleepTracks: s.data.sleepTracks, sleepMinutes: s.data.sleepMinutes } : fail(s.error);
      }
      const sOff = await call('audioSettings', { operation: 'setSleep', selection: 0, tracks: 1 });
      sh.off = sOff.ok ? { ok: true, sleepSelection: sOff.data.sleepSelection, sleepTracks: sOff.data.sleepTracks, sleepMinutes: sOff.data.sleepMinutes } : fail(sOff.error);
      result.sleep = sh;

      // 6) AB 循环：没在播时四个动作各回一句话（不产生循环点）
      const ab = {};
      for (const action of ['a', 'b', 'toggle', 'clear']) {
        const a = await call('audioSettings', { operation: 'ab', action });
        ab[action] = a.ok ? { ok: true, a: a.data.a, b: a.data.b, abActive: a.data.abActive, abHint: a.data.abHint } : fail(a.error);
      }
      result.abLoop = ab;

      // 7) 设备记忆开关：翻转再翻回
      const dmOn = await call('audioSettings', { operation: 'deviceMemory', enabled: !base.deviceMemory });
      result.deviceMemoryFlipped = dmOn.ok ? { ok: true, deviceMemory: dmOn.data.deviceMemory, hasDeviceProfile: dmOn.data.hasDeviceProfile } : fail(dmOn.error);
      const dmBack = await call('audioSettings', { operation: 'deviceMemory', enabled: base.deviceMemory });
      result.deviceMemoryRestored = dmBack.ok ? { ok: true, deviceMemory: dmBack.data.deviceMemory } : fail(dmBack.error);

      // 8) setMode 成功分支：用当前模式重设一次，不跨命名空间所以不会重开输出链路
      const sm = await call('audioSettings', { operation: 'setMode', mode: base.selectedMode });
      result.setModeSame = sm.ok
        ? { ok: true, selectedMode: sm.data.selectedMode, deviceCount: (sm.data.devices || []).length }
        : fail(sm.error);

      // 9) 拒绝路径（全部在动真家伙之前抛，不产生副作用）
      //
      // ⚠ 故意不测「没有任何 ASIO 驱动」那条分支：本机装着实 ASIO 驱动，真调 setMode('Asio')
      // 会把输出模式切过去（第一次跑就踩了，靠 stateDrift 才发现）。要测那条得先卸驱动。
      result.rejects = {
        unknownOperation: (await call('audioSettings', { operation: 'launchRocket' })).error || 'ACCEPTED(BAD)',
        badMode: (await call('audioSettings', { operation: 'setMode', mode: 'Bogus' })).error || 'ACCEPTED(BAD)',
        rateTooFast: (await call('audioSettings', { operation: 'setRate', rate: 5 })).error || 'ACCEPTED(BAD)',
        rateTooSlow: (await call('audioSettings', { operation: 'setRate', rate: 0.1 })).error || 'ACCEPTED(BAD)',
        sleepBadSelection: (await call('audioSettings', { operation: 'setSleep', selection: 99, tracks: 1 })).error || 'ACCEPTED(BAD)',
        sleepBadMinutes: (await call('audioSettings', { operation: 'setSleep', selection: 8, tracks: 1, minutes: 0 })).error || 'ACCEPTED(BAD)',
        sleepBadTracks: (await call('audioSettings', { operation: 'setSleep', selection: 7, tracks: 0 })).error || 'ACCEPTED(BAD)',
        abBadAction: (await call('audioSettings', { operation: 'ab', action: 'rewind' })).error || 'ACCEPTED(BAD)',
        deviceWrongMode: (await call('audioSettings', { operation: 'setDevice', mode: 'Nope', deviceId: '' })).error || 'ACCEPTED(BAD)',
        deviceUnknownId: (await call('audioSettings', { operation: 'setDevice', mode: base.selectedMode, deviceId: 'no-such-device-42' })).error || 'ACCEPTED(BAD)',
        saveProfileWithoutEngine: (await call('audioSettings', { operation: 'saveDeviceProfile' })).error || 'ACCEPTED(BAD)'
      };

      // 10) 收尾读数：除了 abHint，其余必须回到基准
      const g1 = await call('audioSettings', { operation: 'get' });
      result.get1 = g1.ok ? pick(g1.data) : fail(g1.error);

      const drift = [];
      if (g1.ok) {
        for (const k of ['selectedMode', 'selectedDeviceId', 'rate', 'preservePitch', 'sleepSelection', 'sleepTracks', 'sleepMinutes', 'deviceMemory', 'a', 'b', 'abActive']) {
          if (JSON.stringify(base[k]) !== JSON.stringify(g1.data[k])) drift.push(k + ': ' + JSON.stringify(base[k]) + ' -> ' + JSON.stringify(g1.data[k]));
        }
      }
      result.stateDrift = drift;
      return result;
    });

    fs.writeFileSync(OUT, JSON.stringify(out, null, 2), 'utf8');
    console.log(JSON.stringify({
      exe, out: OUT,
      fields: out.get0?.keys, devices: out.get0?.deviceCount,
      mode: out.get0?.selectedMode, rate: out.get0?.rate,
      sleep: out.sleep, abHintAfterClear: out.abLoop?.clear?.abHint,
      rejects: out.rejects, stateDrift: out.stateDrift
    }, null, 2));
    // 状态没归位就直接判失败：这套用例碰的是用户的输出模式与设置，静默留下改动比探针挂掉更糟。
    if (out.stateDrift && out.stateDrift.length > 0) {
      console.error('\nSTATE NOT RESTORED: ' + out.stateDrift.join(' | '));
      process.exitCode = 2;
    }
  } finally {
    try { await browser.close(); } catch {}
    try { child.kill(); } catch {}
  }
})().catch(e => { console.error('PROBE FAILED:', e.message); process.exit(1); });
