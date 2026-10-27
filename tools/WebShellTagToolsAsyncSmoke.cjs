// 标签工具「歌词」两个异步状态（方案 §6 标签工具行 / §8 第三批验收）：
//   ① 预览读取失败 → 就地给重试，点重试能恢复（不是停在一句错误上）
//   ② 保存时个别文件失败 → 失败文件名和原因就地列出，剩下的照常提示成功
// 不依赖宿主：直接给 CelesteTagTools.mount 注入一个可控的 request。
//   NODE_PATH=C:\Users\MSI\_pwtmp\node_modules node tools/WebShellTagToolsAsyncSmoke.cjs
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
let chromium;
try { ({ chromium } = require('playwright')); } catch { ({ chromium } = require('playwright-core')); }

(async () => {
  const browser = await chromium.launch({
    executablePath: 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
    headless: true
  });
  try {
    const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });
    const errors = [];
    page.on('pageerror', e => { errors.push(e.message); console.error('PAGEERROR:', e.stack || e.message); });

    await page.goto(pathToFileURL(path.join(__dirname, '../outputs/webview2-shell/index.html')).href, { waitUntil: 'load' });

    await page.evaluate(() => {
      window.__failPreview = true;      // 第一次预览读取失败
      window.__saved = [];
      const panel = document.createElement('div');
      panel.id = 'tag-tool-fixture';
      document.querySelector('.main')?.append(panel) || document.body.append(panel);
      const request = async (operation, payload) => {
        if (operation === 'searchLyrics') return { candidates: [{ index: 0, Title: '测试歌词', Artist: '艺人', Album: '专辑' }] };
        if (operation === 'lyricPreview') {
          if (window.__failPreview) { window.__failPreview = false; throw new Error('模拟歌词预览读取失败'); }
          return { text: '[00:01.00]测试歌词' };
        }
        if (operation === 'lyricSave') {
          window.__saved.push(payload);
          return { results: [{ id: 'a', ok: true }, { id: 'b', ok: false, name: '02 - Test.flac', error: '文件被占用' }] };
        }
        return {};
      };
      window.CelesteTagTools.mount(panel, 'lyrics', {
        request,
        targets: () => ['a', 'b'],
        current: () => ({ title: '原始标题', artist: '艺人', album: '专辑' }),
        writeRows: async () => true,
        notify: () => {},
        reload: async () => {}
      });
    });

    // 搜索出候选
    await page.locator('#tag-tool-fixture button[type="submit"]').click();
    await page.waitForSelector('.tags-candidate');
    await page.locator('.tags-candidate').click();

    // ① 预览失败 → 就地重试 → 恢复
    await page.waitForSelector('.tags-online-preview button');
    assert.equal(await page.locator('.tags-online-preview button').textContent(), '重试');
    assert.match(await page.locator('#tag-tool-fixture').textContent(), /模拟歌词预览读取失败/);
    await page.locator('.tags-online-preview button').click();
    await page.waitForSelector('.tags-lyric-preview');
    assert.match(await page.locator('.tags-lyric-preview').textContent(), /测试歌词/);

    // ② 保存时个别文件失败 → 就地列出，不吞掉其余结果
    await page.getByRole('button', { name: '下载并保存', exact: true }).click();
    await page.waitForSelector('.tags-tool-fail');
    const failText = await page.locator('.tags-tool-fail').textContent();
    assert.match(failText, /02 - Test\.flac/, '失败文件名要出现');
    assert.match(failText, /文件被占用/, '失败原因要出现');
    assert.equal(await page.locator('.tags-tool-fail p').count(), 2, '提示一句 + 一个失败文件');
    assert.match(await page.locator('#tag-tool-fixture [role="status"]').textContent(), /1 个文件保存失败/);
    assert.equal(await page.evaluate(() => window.__saved.length), 1, '保存请求只发一次');

    assert.deepEqual(errors, []);
    console.log('PASS: 歌词预览失败可就地重试 / 逐文件失败就地列出且不吞结果');
  } finally {
    await browser.close();
  }
})().catch(error => { console.error('FAIL:', error.message); process.exit(1); });
