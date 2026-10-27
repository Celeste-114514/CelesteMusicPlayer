// DSP 动效回归（方案 §8 第三批的 DSP 一行）：
//   ① 导航共享指示条：存在、落在当前项上、换项后跟过去
//   ② 分类开关关掉「导航」后，指示条的过渡真的接近瞬时（不是只改了变量没人用）
//   ③ 模块读取失败 → 就地出现重试，点重试能恢复
//   ④ 机架重排 → 行从旧位置滑到新位置（FLIP）
//   ⑤ 参数写回失败 → 值回滚 + 出现回滚提示，界面不停在没生效的位置上
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellDspMotionSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href;

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1440, height: 960 } });
    const errors = [];
    page.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });

    await page.addInitScript(() => {
      window.failDspRead = '';       // 非空 = 这个模块的读取要失败
      window.failDspWrites = false;
      let rack = ['EQ', '卷积'];
      const sw = (name, value = true) => ({ name, kind: 'switch', value, enabled: true });
      const slider = (name, value = 0, min = -18, max = 18) => ({ name, kind: 'slider', value, min, max, step: 0.1, enabled: true });
      const controls = {
        comp: [sw('DspPowerComp'), sw('DspCompToggle'), slider('DspCompThresholdSlider', -12), slider('DspCompRatioSlider', 4, 1, 20)],
        headroom: [sw('DspPowerHeadroom'), slider('AudioFxSafetyHeadroomSlider', -3, -24, 0)]
      };
      window.chrome ||= {};
      window.chrome.webview = {
        addEventListener: (_, cb) => { window.deliver = data => cb({ data }); },
        postMessage: m => {
          if (!m.requestId) return;
          let data = { ok: true }, ok = true, error = '';
          if (m.command === 'dsp') {
            if (m.operation === 'module') {
              if (window.failDspRead === m.module) { ok = false; error = '模拟模块读取失败'; }
              else data = { controls: controls[m.module] || [], info: { irName: 'room.wav', taps: 2048, sampleRate: 48000, channels: 2, src: '44.1 → 48 kHz', clippingRisk: false }, active: true };
            } else if (m.operation === 'modules') data = { modules: { eq: true } };
            else if (m.operation === 'bypass') data = { bypass: false };
            else if (m.operation === 'levels') data = { session: true, peakDb: -6, chain: '44.1 → 48 kHz', left: { peak: -6 }, right: { peak: -6 } };
            else if (m.operation === 'rack') data = { isDefault: false, modules: rack.map((name, index) => ({ id: index, index, name })) };
            else if (m.operation === 'rackMove') {
              const from = m.index, to = from + m.delta;
              if (to >= 0 && to < rack.length) { const [row] = rack.splice(from, 1); rack.splice(to, 0, row); }
            } else if (m.operation === 'setControl') {
              if (window.failDspWrites) { ok = false; error = '模拟参数提交失败'; }
              else { const c = Object.values(controls).flat().find(c => c.name === m.name); if (c && m.value !== undefined) c.value = m.value; }
            } else if (m.operation === 'profiles') data = { profiles: [] };
          }
          setTimeout(() => window.deliver({ kind: 'celeste.reply', version: 1, requestId: m.requestId, ok, data, error }), 5);
        }
      };
    });

    await page.goto(URL, { waitUntil: 'load' });
    await page.evaluate(() => CelesteShell.navigate('dsp'));
    await page.waitForSelector('.dsp-chain-item');
    await page.waitForTimeout(300);

    // ① 指示条存在并且落在当前项上
    const first = await page.evaluate(() => {
      const bar = document.querySelector('.dsp-nav-indicator');
      const active = document.querySelector('.dsp-chain-item[aria-current="true"]');
      const b = bar.getBoundingClientRect(), a = active.getBoundingClientRect();
      return {
        exists: !!bar,
        ready: bar.dataset.ready,
        hidden: !!bar.hidden,
        y: bar.style.getPropertyValue('--ind-y'),
        expectY: active.offsetTop + 'px',
        h: bar.style.getPropertyValue('--ind-h'),
        expectH: active.offsetHeight + 'px',
        width: getComputedStyle(bar).width,
        // 指示条右缘应该和当前项右缘重合（差不到 1.5px），而不是缩在里面或压到文字上
        rightGap: Math.round((b.right - a.right) * 100) / 100,
        border: getComputedStyle(active).borderRightColor
      };
    });
    assert.equal(first.exists, true, '共有指示条存在');
    assert.equal(first.ready, 'true', '指示条已定位');
    assert.equal(first.y, first.expectY, '指示条纵向对齐当前项');
    assert.equal(first.h, first.expectH, '指示条高度跟随当前项');
    assert.equal(first.width, '3px', '指示条宽度');
    assert.ok(Math.abs(first.rightGap) < 1.5, '指示条贴着当前项右缘：偏差 ' + first.rightGap + 'px');
    // 当前项自己的右边框要让位给指示条，否则会看到两根
    assert.match(first.border, /rgba\(0, 0, 0, 0\)|transparent/, '当前项右边框已透明：' + first.border);

    // 换模块 → 指示条跟过去
    await page.locator('.dsp-chain-item[data-module="comp"]').click();
    await page.waitForTimeout(320);
    const moved = await page.evaluate(() => {
      const bar = document.querySelector('.dsp-nav-indicator');
      const active = document.querySelector('.dsp-chain-item[aria-current="true"]');
      return { y: bar.style.getPropertyValue('--ind-y'), expectY: active.offsetTop + 'px', name: active.dataset.module };
    });
    assert.equal(moved.name, 'comp');
    assert.equal(moved.y, moved.expectY, '换项后指示条跟到新项');
    assert.notEqual(moved.y, first.y, '指示条位置确实变了');
    // SHOTS=1 时留一张对照图，肉眼确认指示条贴在当前项右缘、而不是偏出去半格
    if (process.env.SHOTS === '1') await page.screenshot({ path: path.join(__dirname, '../outputs/web-dsp-nav-indicator.png'), clip: { x: 16, y: 430, width: 300, height: 130 } });

    // ② 关掉「导航」类 → 指示条过渡接近瞬时
    const durations = await page.evaluate(() => {
      const bar = document.querySelector('.dsp-nav-indicator');
      const read = () => parseFloat(getComputedStyle(bar).transitionDuration.split(',')[0]);
      const before = read();
      CelesteMotion.setOff(['nav']);
      const after = read();
      const cat = getComputedStyle(document.documentElement).getPropertyValue('--motion-cat-nav').trim();
      CelesteMotion.setOff([]);
      return { before, after, cat };
    });
    assert.ok(durations.before > 0.1, '正常状态下指示条有过渡：' + durations.before);
    assert.ok(durations.after < 0.01, '关掉导航类后过渡接近瞬时：' + durations.after);
    assert.equal(durations.cat, '0.001', '分类变量写回根元素');

    // ③ 模块读取失败 → 就地重试
    await page.evaluate(() => { window.failDspRead = 'comp'; });
    await page.locator('.dsp-chain-item[data-module="comp"]').click();
    await page.waitForSelector('.dsp-fail');
    assert.equal(await page.locator('.dsp-fail button').textContent(), '重试');
    assert.match(await page.locator('.dsp-fail p').textContent(), /模拟模块读取失败/);
    await page.evaluate(() => { window.failDspRead = ''; });
    await page.locator('.dsp-fail button').click();
    await page.waitForSelector('.dsp-row', { timeout: 2000 });
    assert.equal(await page.locator('.dsp-row').count() > 0, true, '重试后内容回来了');

    // ④ 机架重排：行从旧位置滑到新位置
    await page.locator('.dsp-chain-item[data-module="rack"]').click();
    await page.waitForSelector('.dsp-rack-list li');
    assert.equal(await page.locator('.dsp-rack-list li strong').first().textContent(), 'EQ');
    await page.locator('.dsp-rack-list li').first().locator('button[title="下移"]').click();
    await page.waitForFunction(() => document.querySelector('.dsp-rack-list li strong')?.textContent === '卷积', { timeout: 2000 });
    const flipped = await page.evaluate(() => [...document.querySelectorAll('.dsp-rack-list li')].some(li => li.getAnimations().length > 0));
    assert.equal(flipped, true, '重排后行上有位移动画（FLIP）');

    // ⑤ 参数拖动不被动画拖慢：不重绘整块内容、不产生动画对象、正在拖的滑杆不被换掉
    await page.locator('.dsp-chain-item[data-module="comp"]').click();
    await page.waitForSelector('.dsp-row input[type="range"]');
    const drag = await page.evaluate(async () => {
      const moduleBody = document.querySelector('.dsp-module');
      moduleBody.dataset.probe = 'kept';
      const input = document.querySelector('.dsp-row input[type="range"]');
      const out = () => document.querySelector('.dsp-row-value')?.textContent || '';
      const before = out();
      for (let step = 0; step < 12; step += 1) {
        input.value = String(Number(input.min) + step);
        input.dispatchEvent(new Event('input', { bubbles: true }));
      }
      await new Promise(resolve => setTimeout(resolve, 60));
      return { kept: moduleBody.dataset.probe === 'kept', animations: moduleBody.getAnimations().length, connected: input.isConnected, changed: out() !== before };
    });
    assert.equal(drag.kept, true, '拖动参数不重绘整块内容');
    assert.equal(drag.animations, 0, '拖动期间没有动画对象');
    assert.equal(drag.connected, true, '正在操作的滑杆没有被换掉');
    assert.equal(drag.changed, true, '读数跟着手走');
    await page.waitForTimeout(350);   // 等 200ms 的防抖写回落定，免得干扰下一条

    // ⑥ 写回失败：值回滚 + 回滚提示（用模块头的启用开关，每个控制模块都有一枚）
    await page.locator('.dsp-chain-item[data-module="headroom"]').click();
    await page.waitForSelector('.dsp-power');
    await page.evaluate(() => { window.failDspWrites = true; });
    const pressedBefore = await page.locator('.dsp-power').getAttribute('aria-pressed');
    await page.locator('.dsp-power').click();
    await page.waitForFunction(() => document.querySelector('.dsp-module')?.classList.contains('dsp-reject'), { timeout: 2000 });
    await page.waitForTimeout(400);
    const rolledBack = await page.locator('.dsp-power').getAttribute('aria-pressed');
    assert.equal(rolledBack, pressedBefore, '写回失败后开关回到真值');
    assert.match(await page.locator('#toast').textContent(), /模拟参数提交失败/);

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: 导航指示条跟随 / 分类开关令其瞬时 / 模块失败可就地重试 / 机架重排 FLIP / 拖动不重绘 / 写回失败回滚提示');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
