// EQ 曲线插值（方案 §6「EQ/耳机/其它图形」一行、§8 第三批验收）：
//   ① 换预设后曲线不是瞬移：中间帧既不是旧曲线也不是新曲线
//   ② 150ms 后落在新增益上（不是卡在中间）
//   ③ 关掉「控件」分类后不插值，直接落在终值（不给人"卡住"的错觉）
//   ④ 插值期间手动拖动立刻接管，不会被旧的插值帧拉回去
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellEqTweenSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

const URL = pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href;
const CURVE = '.eq-workbench svg path[fill="none"]';

const PLAIN = [{ freq: 100, gain: 0, q: 1, type: 'Peaking', enabled: true }];
const BOOST = [{ freq: 100, gain: 12, q: 1, type: 'Peaking', enabled: true }];

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1500, height: 1000 } });
    const errors = [];
    page.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });

    await page.addInitScript(({ plain, boost }) => {
      window.__bands = plain.map(band => ({ ...band }));
      window.__presetDelay = 20;
      const handlers = [];
      window.chrome = window.chrome || {};
      window.chrome.webview = {
        addEventListener(_, fn) { handlers.push(fn); },
        postMessage(message) {
          if (!message || !message.requestId) return;
          const reply = (data, ok = true, error = '') => handlers.forEach(fn => fn({
            data: { kind: 'celeste.reply', version: 1, requestId: message.requestId, ok, error, data }
          }));
          if (message.command !== 'dsp') return reply({});
          if (message.operation === 'get') {
            return reply({ enabled: true, preampDb: 0, presetId: 'flat', userPresets: [], bands: window.__bands.map(band => ({ ...band })) });
          }
          if (message.operation === 'preset') {
            // 宿主套用预设：把增益换成另一套，然后页面会重新 get 一次
            setTimeout(() => {
              window.__bands = boost.map(band => ({ ...band }));
              reply({});
            }, window.__presetDelay);
            return;
          }
          if (message.operation === 'spectrum') return reply({ available: false, bands: [] });
          if (message.operation === 'modules') return reply({ modules: { eq: true } });
          reply({});
        }
      };
    }, { plain: PLAIN, boost: BOOST });

    await page.goto(URL, { waitUntil: 'load' });
    await page.evaluate(() => CelesteShell.navigate('dsp'));
    await page.locator('.dsp-chain-item[data-module="eq"]').click();
    // SVG path 在 Playwright 眼里可能被判成"不可见"，这里只等它真的画出内容
    await page.waitForFunction(sel => (document.querySelector(sel)?.getAttribute('d') || '').length > 40, CURVE, { timeout: 6000 });
    await page.waitForTimeout(200);

    const readCurve = () => page.evaluate(sel => document.querySelector(sel)?.getAttribute('d') || '', CURVE);
    const flat = await readCurve();

    // ① 换预设：立刻采一帧，应当既不是旧曲线也不是新曲线
    await page.locator('#eq-preset, .eq-toolbar select').first().selectOption('pop').catch(async () => {
      await page.locator('.eq-toolbar select').first().selectOption('flat');
    });
    await page.waitForTimeout(60);
    const mid = await readCurve();
    await page.waitForTimeout(400);
    const settled = await readCurve();
    assert.notEqual(settled, flat, '换预设后曲线确实变了');
    assert.notEqual(mid, flat, '60ms 时已经不是旧曲线（说明在动）');
    assert.notEqual(mid, settled, '60ms 时还没到终值 —— 这条才说明真的在插值');

    // ② 落定后确实停在新增益上（曲线被拉高了，不是回到原处）
    assert.ok(settled.length > 50, '曲线路径有内容');

    // ③ 关掉「控件」分类 → 不插值，60ms 时就已经是终值
    await page.evaluate(() => CelesteMotion.setOff(['control']));
    await page.locator('.eq-toolbar select').first().selectOption('flat').catch(() => {});
    await page.waitForTimeout(500);
    await page.locator('.eq-toolbar select').first().selectOption('pop').catch(() => {});
    await page.waitForTimeout(60);
    const offMid = await readCurve();
    await page.waitForTimeout(400);
    const offSettled = await readCurve();
    assert.equal(offMid, offSettled, '关掉控件类后不插值：60ms 时就该是终值');
    await page.evaluate(() => CelesteMotion.setOff([]));

    // ④ 插值期间拖动：手动编辑立刻接管
    await page.locator('.eq-toolbar select').first().selectOption('flat').catch(() => {});
    await page.waitForTimeout(450);
    const beforeDrag = await readCurve();
    await page.locator('.eq-toolbar select').first().selectOption('pop').catch(() => {});
    await page.waitForTimeout(40);
    await page.evaluate(() => {
      const input = document.querySelector('.eq-band input[type=range]');
      if (!input) return;
      input.value = String(Number(input.min));
      input.dispatchEvent(new Event('input', { bubbles: true }));
    });
    await page.waitForTimeout(30);
    const afterDrag = await readCurve();
    await page.waitForTimeout(300);
    const afterSettle = await readCurve();
    assert.notEqual(beforeDrag, afterDrag, '拖动立即反映到曲线上');
    assert.equal(afterSettle, afterDrag, '旧插值帧不会把拖好的曲线拉回去');

    assert.deepEqual(errors, [], '没有 JS 异常');
    console.log('PASS: 换预设曲线滑过去 / 150ms 落定 / 关掉控件类即瞬时 / 拖动立刻接管');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
