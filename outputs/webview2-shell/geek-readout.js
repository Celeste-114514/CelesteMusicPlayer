/* 极客预设的顶部读数：OUT / FMT / FLAG / BUF。
   数据只来自宿主的 audioReadout（AudioSignalPath 快照拣出来的字段），
   **不从文件格式猜输出状态**：拿不到就显示 —，四项全拿不到就整块不显示。

   刷新时机（都很轻，Capture 一次就是拼几个字符串）：
   - 切到极客预设时立刻要一次
   - 换曲（celeste.state 里的 trackId 变了）
   - 12 秒慢刷新，兜住"没换曲但 DSP/输出模式被改了"的情况
   不是极客预设就整块收起，连定时器都停掉。 */
(() => {
  'use strict';
  const root = document.documentElement;
  const box = document.getElementById('tech-readout');
  if (!box) return;

  const cells = {};
  box.querySelectorAll('.tech-cell').forEach(el => { cells[el.dataset.tech] = el; });
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const slow = 12000;

  const geek = () => (root.dataset.preset || '').indexOf('geek') === 0;

  function set(key, text, tone, title) {
    const cell = cells[key];
    if (!cell) return;
    cell.querySelector('strong').textContent = text;
    if (tone) cell.dataset.tone = tone; else delete cell.dataset.tone;
    if (title) cell.title = title; else cell.removeAttribute('title');
  }

  function reset() {
    ['out', 'fmt', 'flag', 'buf'].forEach(key => { set(key, '—', 'muted'); });
    box.dataset.on = '';
  }

  /* 44100hz / 16bit / 2声道 → 16b/44.1k。解析不出来就原样给，不硬凑。 */
  function compactFormat(text) {
    if (!text) return '—';
    const match = /(\d+)\s*hz\s*\/\s*(\d+)\s*bit/i.exec(text);
    if (!match) return text;
    const rate = Number(match[1]) / 1000;
    const rateText = Number.isFinite(rate) ? (rate % 1 ? rate.toFixed(1) : String(rate)) : '?';
    return match[2] + 'b/' + rateText + 'k';
  }

  function compactMode(text) {
    if (!text) return '—';
    if (/独占/.test(text)) return 'EXCL';
    if (/asio/i.test(text)) return 'ASIO';
    if (/共享/.test(text)) return 'SHARED';
    return text;
  }

  let busy = false, queued = false, lastTrack = '', timer = 0;

  async function refresh() {
    if (!live || !geek()) { reset(); return; }
    if (busy) { queued = true; return; }
    busy = true;
    try {
      const data = await window.CelesteTransport.request('audioReadout', {});
      if (!geek() || !data) { reset(); return; }

      const mode = compactMode(data.mode);
      const fmtRaw = data.deviceFormat || data.internalFormat || '';
      const flag = data.bitPerfect
        ? (data.confirmed ? ['BIT-PERFECT', 'pass'] : ['UNCONFIRMED', 'muted'])
        : ['DSP', 'warn'];

      set('out', mode, data.mode ? '' : 'muted', data.mode || '');
      set('fmt', compactFormat(fmtRaw), fmtRaw ? '' : 'muted', fmtRaw || '');
      set('flag', flag[0], flag[1], data.reason || (data.bitPerfect ? '没有模块参与，数值直通' : ''));
      set('buf', data.bufferMs ? data.bufferMs + 'ms' : '—', data.bufferMs ? '' : 'muted',
        data.bufferMs ? '输出缓冲设置值' : '');

      // 四项全是 —（还没开始播放）就不占地方
      box.dataset.on = (data.mode || fmtRaw) ? '1' : '';
    } catch {
      reset();
    } finally {
      busy = false;
      if (queued) { queued = false; refresh(); }
    }
  }

  function startTimer() {
    if (timer) return;
    timer = window.setInterval(() => {
      if (!geek()) { stopTimer(); reset(); return; }
      if (document.visibilityState === 'visible') refresh();
    }, slow);
  }
  function stopTimer() { if (timer) { window.clearInterval(timer); timer = 0; } }

  /* 换预设可能带交叉淡变：celeste:theme 派发时根元素的 data-preset 还可能是旧的
     （真正写变量发生在 view transition 的 work() 里）。所以两个事件都听，
     再用"预设真的变了才请求"去重，既不漏也不重复发请求。 */
  let lastPreset = '__init__';
  function sync() {
    const preset = root.dataset.preset || '';
    if (preset.indexOf('geek') !== 0) {
      lastPreset = preset;
      stopTimer();
      reset();
      return;
    }
    if (preset === lastPreset) return;
    lastPreset = preset;
    refresh();
    startTimer();
  }

  document.addEventListener('celeste:theme', sync);
  document.addEventListener('celeste:theme-applied', sync);

  // 换曲才重新取一次：输出格式/bit-perfect 是跟着"这一首"变的
  document.addEventListener('celeste:message', event => {
    const message = event.detail;
    if (!message || message.kind !== 'celeste.state' || !geek()) return;
    const trackId = message.state?.trackId || '';
    if (trackId === lastTrack) return;
    lastTrack = trackId;
    refresh();
  });

  reset();
  sync();
  window.CelesteTechReadout = { refresh };
})();
