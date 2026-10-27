(() => {
  'use strict';
  const shell = window.CelesteShell;
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const SVGNS = 'http://www.w3.org/2000/svg';
  const node = (tag, text, cls) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n; };
  const svgEl = (tag, attrs) => { const n = document.createElementNS(SVGNS, tag); for (const k in attrs) n.setAttribute(k, attrs[k]); return n; };
  // Shared with the rest of the shell: icons come from the <symbol> sheet in index.html, never
  // from a text character (see outputs/避坑清单-网页图标必须用SVG-2026-10-26.md).
  const icon = href => {
    const svg = svgEl('svg', { 'aria-hidden': 'true' });
    const use = svgEl('use', { href });
    svg.append(use);
    return svg;
  };

  const log2 = Math.log2;

  // —— 动效接入（方案 §6 DSP 行）———————————————————————————————————————————
  // 所有动画都问 CelesteMotion 要时长，这里不写毫秒数。
  const motion = () => window.CelesteMotion;
  // 参数写回后的重绘是"刷新"，不是"换页"：不放入场动画，否则每拨一下开关整块内容都在晃。
  let animateModule = false;
  const revealIn = target => { if (animateModule && target) motion()?.reveal(target, 'nav'); };

  /** 读取失败就地给重试，不留一个永远停在"读取中…"的空架子。 */
  function failIn(container, error, retry) {
    const box = node('div', undefined, 'dsp-fail');
    box.append(node('p', (error && error.message) || '读取失败。', 'dsp-row-hint'));
    if (retry) {
      const again = node('button', '重试', 'eq-pill');
      again.type = 'button';
      again.addEventListener('click', () => { again.disabled = true; retry(); });
      box.append(again);
    }
    container.replaceChildren(box);
    motion()?.reveal(box, 'list');
  }

  /** 写回被内核拒绝时的回滚提示：值已经重绘回真值，这里只补一下"刚才那下没生效"。 */
  function flashReject() {
    if (!body.isConnected) return;
    body.classList.remove('dsp-reject');
    void body.offsetWidth;
    body.classList.add('dsp-reject');
    clearTimeout(flashReject.timer);
    flashReject.timer = setTimeout(() => body.classList.remove('dsp-reject'), 600);
  }

  // Exact port of the native ApproxBandGain (MainWindow.EqDsp.cs:803) — same branches, same
  // maths. This is what makes the web curve the shape the native page already draws: an invented
  // gaussian is about three times wider and would no longer touch its own nodes.
  function bandGainAt(f, b) {
    const type = b.type || 'Peaking';
    if (type === 'LowPass') {
      const cutoff = Math.max(20, b.freq);
      if (f >= cutoff) { const x = f / cutoff; return -6 * Math.log10(1 + x * x); }
      return 0;
    }
    if (type === 'HighPass') {
      const hc = Math.max(20, b.freq);
      if (f <= hc) { const x = hc / f; return -6 * Math.log10(1 + x * x); }
      return 0;
    }
    if (type === 'Notch') {
      const d = Math.abs(log2(f / b.freq));
      const q = b.q <= 0 ? 1 : b.q;
      if (d <= 0.5 / q) return -6 * Math.min(1, (0.5 / q - d) * q * 2);
      return 0;
    }
    if (type === 'LowShelf' || type === 'HighShelf') {
      const gain = Math.max(-24, Math.min(24, b.gain || 0));
      const pivot = type === 'LowShelf' ? 200 : 4000;
      const dPivot = Math.abs(log2(f / pivot));
      const reach = type === 'LowShelf' ? (f < pivot ? 0 : dPivot) : (f > pivot ? 0 : dPivot);
      const ratio = Math.max(0, Math.min(1, reach > 0 ? 0.95 : 1));
      const extent = Math.abs(log2(f / b.freq));
      return gain * Math.max(0.2, Math.min(1, 1 / (1 + 0.5 * extent))) * ratio;
    }
    if (Math.abs(b.gain || 0) < 0.01) return 0;
    const x = Math.log10(f / b.freq) * 6;
    const q = Math.max(0.1, b.q || 1);
    const sigma = 0.7 / q;
    return b.gain * Math.exp(-(x * x) / (2 * sigma * sigma));
  }

  async function dsp(operation, payload = {}) {
    if (!live) throw new Error('请在桌面播放器中使用此功能。');
    return window.CelesteTransport.request('dsp', { operation, ...payload });
  }

  const page = node('section', undefined, 'page dsp-page'); page.id = 'dsp-page'; page.hidden = true;
  const heading = node('div', undefined, 'dsp-heading');
  const copy = node('div');
  copy.append(node('p', 'SIGNAL PATH', 'eyebrow'), node('h1', 'DSP 音频处理'));
  heading.append(copy);
  const rackToggle = node('button', '总旁路关闭', 'dsp-rack-toggle');
  rackToggle.setAttribute('aria-pressed', 'false');
  rackToggle.title = '总旁路：打开 = 所有 DSP 都不参与处理（bit-perfect 直通）';
  rackToggle.addEventListener('click', async () => {
    if (!live) { shell.notify('请在桌面播放器中使用这个功能。'); return; }
    const next = rackToggle.getAttribute('aria-pressed') !== 'true';
    rackToggle.setAttribute('aria-pressed', String(next));
    try {
      const result = await dsp('setBypass', { value: next });
      rackToggle.setAttribute('aria-pressed', String(!!result.bypass));
      rackToggle.textContent = result.bypass ? '总旁路已开启' : '总旁路关闭';
      refreshModuleDots();
      if (current === 'safety') pollLevels();
    } catch (error) {
      rackToggle.setAttribute('aria-pressed', String(!next));
      shell.notify(error.message || '切换失败。');
    }
  });
  heading.append(rackToggle);
  page.append(heading);

  // ECHO DspPage: a read-only pipeline map of the signal path plus a focus strip naming the open
  // module. Both read the same state as the nav dots and the editor, so nothing can drift.
  const pipeline = node('div', undefined, 'dsp-pipeline-map');
  pipeline.setAttribute('aria-label', '信号管线条');
  const pipelineCells = new Map();
  [['input', '输入'], ['headroom', '余量'], ['src', 'SRC'], ['eq', 'EQ'], ['comp', '压缩'], ['channel', '声道'], ['fir', '卷积'], ['output', '输出']].forEach(([id, label]) => {
    const cell = node('span'), value = node('strong', '—');
    cell.dataset.node = id;
    cell.append(node('em', label), value);
    pipeline.append(cell);
    pipelineCells.set(id, { cell, value });
  });
  page.append(pipeline);

  const focus = node('div', undefined, 'dsp-focus-strip');
  const focusModule = node('strong', '—'), focusState = node('strong', '—'), focusBit = node('strong', '—');
  const focusCell = (label, valueNode) => { const cell = node('span'); cell.append(node('em', label), valueNode); return cell; };
  const focusRefresh = node('button', '刷新');
  focusRefresh.addEventListener('click', () => { refreshModuleDots(); renderModule(); });
  focus.append(focusCell('当前模块', focusModule), focusCell('模块状态', focusState), focusCell('直通状态', focusBit), focusRefresh);
  page.append(focus);

  const layout = node('div', undefined, 'dsp-layout');
  const nav = node('aside', undefined, 'dsp-nav');
  const body = node('div', undefined, 'dsp-module');

  // 导航共享指示条：只有这一根，跟着当前项走。原来是每项自己改右边框颜色 ——
  // 换项时是"旧的一下消失、新的一下出现"，没有任何连续感。现在由一根条滑过去。
  const navIndicator = node('span', undefined, 'dsp-nav-indicator');
  navIndicator.setAttribute('aria-hidden', 'true');
  nav.append(navIndicator);
  nav.classList.add('has-indicator');

  /** 把指示条摆到当前项上。instant 用于窗口尺寸变化（那一下不该看到滑动）。 */
  function placeIndicator(instant) {
    const active = nav.querySelector('.dsp-chain-item[aria-current="true"]');
    if (!active || !active.offsetHeight) { navIndicator.dataset.ready = 'false'; return; }
    if (instant) {
      navIndicator.style.transition = 'none';
      requestAnimationFrame(() => { navIndicator.style.transition = ''; });
    }
    navIndicator.dataset.ready = 'true';
    navIndicator.style.setProperty('--ind-y', active.offsetTop + 'px');
    navIndicator.style.setProperty('--ind-h', active.offsetHeight + 'px');
    navIndicator.style.setProperty('--ind-x', (active.offsetLeft + active.offsetWidth - 3) + 'px');
  }
  // 窗口缩放 / 导航换行都会改变每项的位置，量一次重摆。这里不监听滚动（导航自身不滚）。
  if (typeof ResizeObserver === 'function') {
    new ResizeObserver(() => placeIndicator(true)).observe(nav);
  }

  // Mirrors MainWindow.DspNav.cs:124-140 — same 12 entries, same order, same group names.
  // `icon`/`desc`/`accent` drive the ECHO chain-item rail (DspPage.tsx 6218-6255); accents reuse
  // ECHO's four-colour palette. `ready` marks what has a bridge; everything here does now.
  const MODULES = [
    { id: 'profiles', name: '听音方案', group: '', ready: true, icon: 'file', desc: '把整套设置存成命名快照', accent: 'blue' },
    { id: 'rack', name: 'DSP 机架编排', group: '', ready: true, icon: 'route', desc: '调整模块的处理顺序', accent: 'blue' },
    { id: 'headroom', name: '输入余量', group: '输入', ready: true, icon: 'shield', desc: '进入处理链前先留余量', accent: 'amber' },
    { id: 'rg', name: '响度 · ReplayGain', group: '输入', ready: true, icon: 'gauge', desc: '按响度标签自动补偿音量', accent: 'violet' },
    { id: 'src', name: 'SRC / 升频', group: '采样率', ready: true, icon: 'cpu', desc: '重采样到声卡支持的速率', accent: 'blue' },
    { id: 'eq', name: '参数 EQ', group: '塑形', ready: true, icon: 'sliders', desc: '参数均衡，塑形音色', accent: 'violet' },
    { id: 'comp', name: '动态压缩器', group: '塑形', ready: true, icon: 'activity', desc: '压缩动态，压住过载', accent: 'amber' },
    { id: 'channel', name: '声道工具', group: '声道', ready: true, icon: 'speaker', desc: '平衡、反相、延迟与串扰', accent: 'blue' },
    { id: 'stereo', name: '立体声场', group: '声道', ready: true, icon: 'waves', desc: '调整声场宽度与中侧配比', accent: 'violet' },
    { id: 'matrix', name: '声道矩阵', group: '声道', ready: true, icon: 'layers', desc: '四路系数决定左右混音', accent: 'blue' },
    { id: 'fir', name: 'FIR / 房间校正', group: '空间', ready: true, icon: 'target', desc: '脉冲响应做房间与耳机校正', accent: 'green' },
    { id: 'safety', name: '输出安全', group: '输出安全', ready: true, icon: 'shield', desc: '实时电平、削波与保护状态', accent: 'green' }
  ];
  let current = 'eq';
  let disposeEq = null, moduleEpoch = 0, moduleVisual = null, telemetryBusy = false;

  // Controls the ECHO visual layer already renders as its own card / button group. Listing them
  // here keeps one writer per parameter: the generic row would otherwise duplicate the slider.
  const VISUAL_OWNED = {
    headroom: ['AudioFxSafetyHeadroomSlider', 'AudioFxSafetyLimiterToggle'],
    fir: ['RoomTrimSlider', 'RoomIrGainSlider', 'RoomIrEnabledToggle'],
    comp: ['DspCompToggle', 'DspCompPeakToggle'],
    rg: ['AudioFxRgModeCombo'],
    channel: ['AudioFxChannelMonoCombo', 'AudioFxChannelSwapToggle', 'AudioFxChannelInvertLToggle', 'AudioFxChannelInvertRToggle']
  };

  // ---- generic module renderer ------------------------------------------------------------
  // Every non-EQ module is a set of native controls, and the bridge hands back that set in page
  // order with its kind, range and current value. So this renderer is the same code for all of
  // them: it never grows when a module gains a slider. Only wording lives here.
  //
  // Writing back goes through setControl, which sets the native control itself — that fires the
  // control's own handler, which saves and pushes to the kernel. No second state model exists.
  const CONTROL_COPY = {
    DspPowerHeadroom: { label: '模块开关' },
    AudioFxSafetyHeadroomSlider: { label: '输入余量', unit: ' dB' },
    AudioFxSafetyLimiterToggle: { label: '安全限幅', hint: '源信号不超过 0 dBFS 时只待命，不做削波' },

    DspPowerRg: { label: '模块开关' },
    AudioFxRgModeCombo: { label: '响度模式' },
    AudioFxRgPreampSlider: { label: '预增益', unit: ' dB' },
    AudioFxRgPreventClippingToggle: { label: '防止削波' },

    DspPowerSrc: { label: '模块开关' },
    SrcRateCombo: { label: '目标采样率' },
    SrcQualityCombo: { label: '重采样质量' },
    SrcDitherCombo: { label: '抖动' },

    DspPowerComp: { label: '模块开关' },
    DspCompToggle: { label: '压缩器' },
    DspCompThresholdSlider: { label: '阈值', unit: ' dB' },
    DspCompRatioSlider: { label: '压缩比', unit: ':1' },
    DspCompAttackSlider: { label: '启动时间', unit: ' ms' },
    DspCompReleaseSlider: { label: '释放时间', unit: ' ms' },
    DspCompKneeSlider: { label: '拐点', unit: ' dB' },
    DspCompMakeupSlider: { label: '补偿增益', unit: ' dB' },
    DspCompMixSlider: { label: '干湿比', unit: ' %' },
    DspCompPeakToggle: { label: '峰值检测' },

    DspPowerChannel: { label: '模块开关' },
    AudioFxChannelToggle: { label: '声道处理' },
    AudioFxChannelBalanceSlider: { label: '左右平衡' },
    AudioFxChannelLeftGainSlider: { label: '左声道增益', unit: ' dB' },
    AudioFxChannelRightGainSlider: { label: '右声道增益', unit: ' dB' },
    AudioFxChannelMonoCombo: { label: '单声道' },
    AudioFxChannelSwapToggle: { label: '交换左右' },
    AudioFxChannelInvertLToggle: { label: '左声道反相' },
    AudioFxChannelInvertRToggle: { label: '右声道反相' },
    AudioFxChannelLeftDelaySlider: { label: '左声道延迟', unit: ' ms' },
    AudioFxChannelRightDelaySlider: { label: '右声道延迟', unit: ' ms' },
    DspPowerXfeed: { label: 'Crossfeed 开关' },
    AudioFxChannelCrossfeedToggle: { label: 'Crossfeed' },
    AudioFxChannelCrossfeedSlider: { label: 'Crossfeed 强度' },
    AudioFxChannelCrossfeedCutoffSlider: { label: 'Crossfeed 截止', unit: ' Hz' },

    DspPowerField: { label: '模块开关' },
    DspFieldToggle: { label: '立体声场' },
    DspFieldWidthSlider: { label: '宽度' },
    DspFieldCenterSlider: { label: '中置' },
    DspFieldSideSlider: { label: '侧向' },

    DspPowerMatrix: { label: '模块开关' },
    DspMatrixToggle: { label: '声道矩阵' },
    DspMatrixLlSlider: { label: 'L → L' },
    DspMatrixLrSlider: { label: 'L → R' },
    DspMatrixRlSlider: { label: 'R → L' },
    DspMatrixRrSlider: { label: 'R → R' },

    DspPowerFir: { label: '模块开关' },
    RoomIrEnabledToggle: { label: '房间校正' },
    RoomIrGainSlider: { label: '增益', unit: ' dB' },
    RoomTrimSlider: { label: '预衰减', unit: ' dB' }
  };

  const MODULE_COPY = {
    profiles: { title: '听音方案', desc: '把整套 DSP 设置存成一份命名快照，换耳机、换房间时一键切过去。' },
    rack: { title: 'DSP 机架编排', desc: '这里的先后顺序就是信号真正被处理的顺序，越靠上越先经过。' },
    safety: { title: '输出安全', desc: '实时看送往声卡的电平、削波次数和当前在跑的模块。' },
    headroom: { title: '输入余量', desc: '进入处理链之前先留出余量，后面的模块就不容易过载。' },
    rg: { title: '响度 · ReplayGain', desc: '按曲目响度标签自动补偿音量，避免下一首突然变响。' },
    src: { title: 'SRC / 升频', desc: '把源采样率重采样到声卡支持的速率。' },
    comp: { title: '动态压缩器', desc: '压缩动态范围，让弱小段落更清楚，同时压住过载。' },
    channel: { title: '声道工具', desc: '平衡、增益、反相、延迟，以及 Crossfeed 耳機串扰。' },
    stereo: { title: '立体声场', desc: '调整宽度与中置／侧向的配比。' },
    matrix: { title: '声道矩阵', desc: '四路系数直接决定左右如何混到左右。' },
    fir: { title: 'FIR / 房间校正', desc: '加载脉冲响应做房间或耳机校正。' }
  };


  function formatValue(value, step) {
    const decimals = step && step < 1 ? (step < 0.1 ? 2 : 1) : 0;
    return Number(value).toFixed(decimals);
  }

  function pushControl(name, payload, module = current) {
    if (!live) return;
    const epoch = moduleEpoch;
    return dsp('setControl', { module, name, ...payload })
      .then(result => { refreshModuleDots(); return result; })
      .catch(error => {
        shell.notify(error.message || '写入失败。');
        // 失败 → 重绘回内核的真值（用户看到的滑块位置不会停在一个没生效的值上），
        // 再闪一下提示"这一下被拒了"。重绘不放动画：值是回滚，不是内容换新。
        if (epoch === moduleEpoch && !page.hidden) { renderModule(false); flashReject(); }
        return false;
      });
  }

  function controlRow(control) {
    const module = current;
    const copy = CONTROL_COPY[control.name] || { label: control.name };
    const row = node('div', undefined, 'dsp-row');
    row.dataset.control = control.name;

    if (control.kind === 'combo') {
      row.append(node('span', copy.label, 'dsp-row-label'));
      const select = document.createElement('select');
      select.className = 'dsp-select';
      (control.items || []).forEach((item, index) => {
        const option = document.createElement('option');
        option.value = String(index);
        option.textContent = item;
        if (index === control.index) option.selected = true;
        select.append(option);
      });
      select.disabled = !control.enabled;
      select.setAttribute('aria-label', copy.label);
      select.addEventListener('change', () => { moduleVisual?.update(control.name, Number(select.value)); pushControl(control.name, { index: Number(select.value) }); });
      row.append(select);
      return [row];
    }

    if (control.kind === 'switch') {
      const toggle = node('button', undefined, 'dsp-switch');
      toggle.setAttribute('aria-pressed', String(!!control.value));
      toggle.disabled = !control.enabled;
      toggle.append(node('span', undefined, 'dsp-switch-knob'), node('span', copy.label));
      toggle.addEventListener('click', () => {
        const next = toggle.getAttribute('aria-pressed') !== 'true';
        toggle.setAttribute('aria-pressed', String(next));
        moduleVisual?.update(control.name, next);
        Promise.resolve(pushControl(control.name, { value: next })).then(ok => { if (row.isConnected) renderModule(false); });
      });
      row.append(toggle);
      if (copy.hint) row.append(node('span', copy.hint, 'dsp-row-hint'));
      return [row];
    }

    // Slider: the page updates instantly, the write is debounced. Every write makes the native
    // side save and push to the kernel, so firing per pixel would flood that path.
    row.append(node('span', copy.label, 'dsp-row-label'));
    const input = document.createElement('input');
    input.type = 'range';
    input.min = control.min; input.max = control.max;
    input.step = control.step > 0 ? control.step : 0.1;
    input.value = control.value;
    input.setAttribute('aria-label', copy.label);
    input.disabled = !control.enabled;
    const out = node('output', formatValue(control.value, input.step) + (copy.unit || ''), 'dsp-row-value');
    let controlTimer = 0;
    input.addEventListener('input', () => {
      out.textContent = formatValue(input.value, input.step) + (copy.unit || '');
      moduleVisual?.update(control.name, Number(input.value));
      clearTimeout(controlTimer);
      controlTimer = setTimeout(() => pushControl(control.name, { value: Number(input.value) }, module), 200);
    });
    input.addEventListener('change', () => { clearTimeout(controlTimer); pushControl(control.name, { value: Number(input.value) }, module); });
    row.append(input, out);
    return [row];
  }

  function moduleHead(id, extra) {
    const copy = MODULE_COPY[id] || {};
    const entry = MODULES.find(m => m.id === id);
    const head = node('div', undefined, 'dsp-module-head');
    const title = node('div');
    title.append(node('h2', copy.title || (entry ? entry.name : id)));
    if (copy.desc) title.append(node('p', copy.desc));
    head.append(title);
    if (extra) head.append(extra);
    return head;
  }

  function renderControlModule(id) {
    const epoch = moduleEpoch;
    body.append(moduleHead(id));

    const group = node('div', undefined, 'dsp-module-body');
    body.append(group);
    group.append(node('p', '读取中…', 'dsp-row-hint'));

    if (!live) {
      group.replaceChildren(node('p', '请在桌面播放器中使用这个页面：浏览器里读不到音频内核的参数。', 'dsp-row-hint'));
      return;
    }

    dsp('module', { module: id }).then(result => {
      if (current !== id || epoch !== moduleEpoch || page.hidden) return;
      // The page already knows whether this module is processing, so keep its dot honest even if
      // the bulk refresh failed for some reason.
      const dot = nav.querySelector(`[data-module="${id}"]`);
      if (dot) dot.dataset.active = String(!!result.active);
      group.replaceChildren();
      const visualHost = node('div'); group.before(visualHost);
      moduleVisual = window.CelesteDspVisuals.mount(visualHost, id, result.controls || [], result.info || {}, dsp, renderModule);
      const powerName = (result.controls || []).find(c => /^DspPower/.test(c.name));
      (result.controls || []).forEach(control => {
        if (id==='src' && ['SrcRateCombo','SrcQualityCombo','SrcDitherCombo'].includes(control.name)) return;
        if ((VISUAL_OWNED[id] || []).includes(control.name)) return;
        if (powerName && control === powerName) return;   // rendered in the header instead
        controlRow(control).forEach(row => group.append(row));
      });
      if (powerName) {
        const toggle = node('button', undefined, 'dsp-power');
        toggle.setAttribute('aria-pressed', String(!!powerName.value));
        toggle.textContent = powerName.value ? '已启用' : '已停用';
        toggle.addEventListener('click', () => {
          const next = toggle.getAttribute('aria-pressed') !== 'true';
          toggle.setAttribute('aria-pressed', String(next));
          toggle.textContent = next ? '已启用' : '已停用';
          pushControl(powerName.name, { value: next });
          // Native handlers enable/disable the page's own controls, so re-read once it settles.
          setTimeout(() => { if (current === id && !page.hidden) renderModule(false); }, 260);
        });
        body.querySelector('.dsp-module-head').append(toggle);
      }
      revealIn(group);
    }).catch(error => {
      if (current !== id || epoch !== moduleEpoch || page.hidden) return;
      failIn(group, error, () => renderModule());
    });
  }

  // Pipeline map + focus strip write-through helpers. `moduleMap` and `lastLevels` are the two
  // snapshots the bridge already hands out, so these two never issue their own requests.
  let moduleMap = {}, lastLevels = null;
  function paintPipeline() {
    pipelineCells.forEach(({ cell, value }, id) => {
      cell.dataset.selected = String(id === current);
      if (id === 'input' || id === 'output') return;
      const on = !!moduleMap[id];
      cell.dataset.active = String(on);
      value.textContent = on ? '参与' : '旁路';
    });
    const input = pipelineCells.get('input'), output = pipelineCells.get('output');
    input.cell.dataset.active = 'true';
    if (!lastLevels) return;
    const chain = String(lastLevels.chain || '');
    input.value.textContent = chain ? (chain.split('→')[0] || '').trim() || '—' : '—';
    const peak = lastLevels.session ? Number(lastLevels.peakDb) : NaN;
    output.value.textContent = Number.isFinite(peak) ? peak.toFixed(1) + ' dB' : (lastLevels.session ? '—' : '未播放');
    output.cell.dataset.risk = String(lastLevels.overload === 'clip');
    output.cell.dataset.active = String(!!lastLevels.session);
  }
  function paintFocus() {
    const entry = MODULES.find(m => m.id === current);
    focusModule.textContent = entry ? entry.name : current;
    focusState.textContent = current === 'rack' ? '可编排' : current === 'profiles' ? '可保存' : (moduleMap[current] ? '已启用' : '已旁路');
    focusBit.textContent = lastLevels ? (lastLevels.bypass ? '总旁路中' : (lastLevels.bitPerfect ? '直通' : 'DSP 链路')) : '—';
    // ECHO output card in the rail: only what the levels poll really supplies.
    outputValue.textContent = lastLevels ? (lastLevels.bypass ? '总旁路' : (lastLevels.bitPerfect ? '直通' : 'DSP 处理中')) : '—';
    outputChain.textContent = lastLevels ? (lastLevels.chain || '—') : '未播放';
  }

  // Nav dots show which modules are actually processing, same meaning as the native dots.
  async function refreshModuleDots() {
    if (!live) return;
    try {
      const result = await dsp('modules');
      const map = result.modules || {};
      moduleMap = map;
      MODULES.forEach(m => {
        const b = nav.querySelector(`[data-module="${m.id}"]`);
        if (b) b.dataset.active = String(m.id === 'rack' || !!map[m.id]);
      });
      paintPipeline(); paintFocus();
    } catch { /* dots are decoration; never block the page on them */ }
  }

  /**
   * 重画当前模块。
   * animate=false 用于"参数写回后的刷新"：内容没换，只是状态回读，不该再放一次入场。
   */
  function renderModule(animate = true) {
    animateModule = animate;
    moduleEpoch++;
    disposeEq?.(); disposeEq = null;
    moduleVisual?.dispose(); moduleVisual = null;
    body.replaceChildren();
    ensureLevelPoll(true);
    MODULES.forEach(m => { const b = nav.querySelector(`[data-module="${m.id}"]`); if (b) b.setAttribute('aria-current', String(m.id === current)); });
    paintPipeline(); paintFocus();
    placeIndicator();
    if (current === 'profiles') { renderProfiles(); return; }
    if (current === 'rack') { renderRack(); return; }
    if (current === 'safety') { renderSafety(); revealIn(body); return; }
    if (current === 'eq') { renderEq(); revealIn(body); return; }
    renderControlModule(current);
  }

  function renderEq() {
    disposeEq = window.CelesteDspEq.mount(body, { request: dsp, notify: text => shell.notify(text), bandGainAt, live });
  }

  // ---- 听音方案 ---------------------------------------------------------------------------
  // Reads and writes through DspProfileStore, the same file the native page uses, so a profile
  // saved here is the same profile the native page lists.
  function renderProfiles() {
    if (!live) { body.append(moduleHead('profiles'), node('p', '请在桌面播放器中使用这个页面。', 'dsp-row-hint')); return; }

    const saveRow = node('div', undefined, 'dsp-profile-save');
    const nameInput = document.createElement('input');
    nameInput.type = 'text';
    nameInput.className = 'dsp-input';
    nameInput.placeholder = '方案名字，例如「夜间 · 平板耳机」';
    nameInput.maxLength = 40;
    const saveBtn = node('button', '保存当前设置为方案', 'primary');
    saveBtn.addEventListener('click', async () => {
      saveBtn.disabled = true;
      try {
        await dsp('profileSave', { name: nameInput.value.trim() });
        nameInput.value = '';
        shell.notify('已保存当前设置。');
        renderModule();
      } catch (error) { shell.notify(error.message || '保存失败。'); }
      finally { saveBtn.disabled = false; }
    });
    saveRow.append(nameInput, saveBtn);
    body.append(moduleHead('profiles', null), saveRow);

    const listBox = node('div', undefined, 'dsp-profile-list');
    const note = node('p', '读取中…', 'dsp-row-hint');
    listBox.append(note);
    body.append(listBox);

    body.append(node('p', '一份方案包含：EQ 曲线、机架顺序、压缩器、立体声场、声道矩阵、声道平衡、输入余量、ReplayGain、房间校正与升频设置。', 'eq-hint'));

    dsp('profiles').then(result => {
      if (current !== 'profiles') return;
      listBox.replaceChildren();
      const profiles = result.profiles || [];
      if (!profiles.length) {
        listBox.append(node('p', '还没有保存过方案。调好声音后点上面的按钮存一份。', 'dsp-row-hint'));
        revealIn(listBox);
        return;
      }
      profiles.forEach(profile => {
        const row = node('div', undefined, 'dsp-profile-row');
        if (profile.active) row.dataset.active = 'true';
        const info = node('div', undefined, 'dsp-profile-info');
        const title = node('div', undefined, 'dsp-profile-name');
        title.append(node('span', profile.name));
        if (profile.active) title.append(node('span', '当前', 'dsp-profile-badge'));
        info.append(title);
        if (profile.stamp) info.append(node('p', '最后修改 ' + profile.stamp, 'dsp-profile-stamp'));
        row.append(info);

        const actions = node('div', undefined, 'dsp-profile-actions');
        const apply = node('button', profile.active ? '已应用' : '应用', 'eq-pill');
        apply.disabled = !!profile.active;
        apply.addEventListener('click', async () => {
          apply.disabled = true;
          try { await dsp('profileApply', { name: profile.name }); shell.notify(`已切到「${profile.name}」。`); renderModule(); }
          catch (error) { shell.notify(error.message || '应用失败。'); apply.disabled = false; }
        });
        const update = node('button', '更新为当前', 'eq-pill');
        update.title = '用现在的设置覆盖这份方案';
        update.addEventListener('click', async () => {
          update.disabled = true;
          try { await dsp('profileUpdate', { name: profile.name }); shell.notify(`「${profile.name}」已更新为当前设置。`); renderModule(); }
          catch (error) { shell.notify(error.message || '更新失败。'); update.disabled = false; }
        });
        const remove = node('button', '删除', 'eq-pill danger');
        remove.addEventListener('click', async () => {
          remove.disabled = true;
          try { await dsp('profileDelete', { name: profile.name }); shell.notify(`已删除「${profile.name}」。`); renderModule(); }
          catch (error) { shell.notify(error.message || '删除失败。'); remove.disabled = false; }
        });
        actions.append(apply, update, remove);
        row.append(actions);
        listBox.append(row);
      });
      revealIn(listBox);
    }).catch(error => {
      failIn(listBox, error, () => renderModule());
    });
  }

  // ---- 机架编排 ---------------------------------------------------------------------------
  // ECHO DspRackPanel: a panel with a header row, then an ordered list where each entry carries
  // a grip, a zero-padded position, the module copy and its two move buttons.
  // 机架重排用：动作前记下每一行在屏幕上的位置，重排后让行从旧位置滑到新位置。
  // 重建列表会换掉 DOM 节点，所以不能用位置无关的通用 FLIP —— 这里按模块名对上旧位置。
  let rackSnapshot = null;

  function renderRack() {
    if (!live) { body.append(moduleHead('rack'), node('p', '请在桌面播放器中使用这个页面。', 'dsp-row-hint')); return; }

    const reset = node('button', '恢复默认顺序', 'eq-pill');
    reset.addEventListener('click', async () => {
      reset.disabled = true;
      try { await dsp('rackReset'); renderModule(); }
      catch (error) { shell.notify(error.message || '恢复失败。'); reset.disabled = false; }
    });

    const panel = node('section', undefined, 'dsp-module-panel dsp-module-panel--rack');
    const header = node('header', undefined, 'dsp-rack-header');
    const headerCopy = node('div');
    headerCopy.append(node('span', 'Native DSP Rack', 'dsp-kicker'), node('h2', '编排实际处理顺序'), node('p', '越靠上的模块越先处理。改一次顺序会立刻重排内核链路，声音变化马上能听到。'));
    const headerActions = node('div', undefined, 'dsp-rack-actions');
    headerActions.append(reset);
    header.append(headerCopy, headerActions);
    panel.append(header);

    const list = node('ol', undefined, 'dsp-rack-list');
    list.append(node('p', '读取中…', 'dsp-row-hint'));
    panel.append(list);
    const foot = node('footer', undefined, 'dsp-rack-fixed');
    panel.append(foot);
    body.append(panel);

    dsp('rack').then(result => {
      if (current !== 'rack') return;
      list.replaceChildren();
      const modules = result.modules || [];
      modules.forEach(module => {
        const row = node('li');
        row.dataset.rack = module.name;
        const grip = node('span', undefined, 'dsp-rack-grip');
        grip.append(glyph('grip', 15));
        row.append(grip, node('span', String(module.index + 1).padStart(2, '0'), 'dsp-rack-index'));
        const info = node('div');
        info.append(node('strong', module.name));
        if (module.hint) info.append(node('small', module.hint));
        row.append(info);

        const move = (delta, label) => {
          const button = node('button', undefined, 'dsp-rack-move');
          button.append(icon('#i-chevron'));
          button.title = label;
          button.setAttribute('aria-label', label);
          button.disabled = module.index + delta < 0 || module.index + delta >= modules.length;
          button.addEventListener('click', async () => {
            button.disabled = true;
            // 先量位置再让宿主重排：失败时不跳，成功时才滑。
            rackSnapshot = new Map([...list.children].map(row => [row.dataset.rack, row.getBoundingClientRect().top]));
            try { await dsp('rackMove', { index: module.index, delta }); renderModule(false); }
            catch (error) { rackSnapshot = null; shell.notify(error.message || '调整失败。'); button.disabled = false; }
          });
          return button;
        };
        const buttons = node('div', undefined, 'dsp-rack-buttons');
        // Chevron is a single glyph pointing right in the icon set; rotate it for up/down instead
        // of inventing a second asset.
        const up = move(-1, '上移');
        up.classList.add('up');
        const down = move(1, '下移');
        down.classList.add('down');
        buttons.append(up, down);
        row.append(buttons);
        list.append(row);
      });

      foot.replaceChildren();
      const fixed = node('div');
      fixed.append(node('span', result.isDefault ? '默认顺序' : '自定义顺序'), node('small', result.isDefault ? 'EQ → 卷积 → ReplayGain → 压缩 → Crossfeed → 声场 → 矩阵 → 平衡' : '这份顺序会跟着「听音方案」一起保存。'));
      foot.append(fixed);

      // 重排后的位移动画：只动被换位的行，没动的行不产生动画对象。
      const m = motion();
      if (rackSnapshot && m) {
        const before = rackSnapshot;
        rackSnapshot = null;
        if (m.canMove() && !m.state.off.has('list')) {
          [...list.children].forEach(row => {
            const was = before.get(row.dataset.rack);
            if (was == null) return;
            const dy = was - row.getBoundingClientRect().top;
            if (!dy) return;
            m.animate(row, [{ transform: `translateY(${dy}px)` }, { transform: 'translateY(0)' }],
              { duration: m.duration(180, 'list'), easing: m.easings.enter });
          });
        }
      } else {
        revealIn(list);
      }
    }).catch(error => {
      rackSnapshot = null;
      failIn(list, error, () => renderModule());
    });
  }

  // ---- 输出安全（实时） -------------------------------------------------------------------
  let levelTimer = 0;
  let levelPeakHold = -140, levelClipSeen = 0;

  function ensureLevelPoll(on) {
    if (on && live && !levelTimer) {
      levelTimer = setInterval(pollLevels, 500);
      pollLevels();
    } else if (!on && levelTimer) {
      clearInterval(levelTimer);
      levelTimer = 0;
    }
  }

  const METER_MIN = -60;
  const meterPct = db => Math.max(0, Math.min(100, (db - METER_MIN) / -METER_MIN * 100));

  function meterRow(label) {
    const wrap = node('div', undefined, 'dsp-meter');
    const head = node('div', undefined, 'dsp-meter-head');
    const nameEl = node('span', label, 'dsp-meter-label');
    const value = node('span', '—', 'dsp-meter-value');
    head.append(nameEl, value);
    const track = node('div', undefined, 'dsp-meter-track');
    const bar = node('div', undefined, 'dsp-meter-bar');
    track.append(bar);
    wrap.append(head, track);
    return { wrap, track, bar, value };
  }

  function renderSafety() {
    if (!live) { body.append(moduleHead('safety'), node('p', '请在桌面播放器中使用这个页面：实时电平来自本机音频内核。', 'dsp-row-hint')); return; }

    const reset = node('button', '重置统计', 'eq-pill');
    reset.addEventListener('click', async () => {
      try { await dsp('levelsReset'); levelPeakHold = -140; levelClipSeen = 0; pollLevels(); }
      catch (error) { shell.notify(error.message || '重置失败。'); }
    });
    body.append(moduleHead('safety', reset));

    // ECHO OutputSafetyPanel: hero → route → checks + next action, all fed by the levels poll.
    const panel = node('section', undefined, 'dsp-module-panel dsp-module-panel--safety');
    const hero = node('div', undefined, 'dsp-safety-hero');
    const emblem = node('div', undefined, 'dsp-safety-emblem');
    emblem.append(glyph('shield', 26));
    const heroText = node('div');
    const heroTitle = node('h2', '正在读取输出状态');
    const heroDetail = node('p', '播放后显示当前电平与保护状态。');
    heroText.append(heroTitle, heroDetail);
    hero.append(emblem, heroText);
    panel.append(hero);

    const route = node('div', undefined, 'dsp-safety-route');
    const routeValues = [['DSP 状态', 'activity'], ['输出峰值', 'wave'], ['限制器', 'shield'], ['削波次数', 'zap']].map(([label, ico]) => {
      const cell = node('span'), value = node('strong', '—');
      cell.append(glyph(ico, 16), node('em', label), value);
      route.append(cell);
      return value;
    });
    panel.append(route);

    // The limiter switch really lives in the headroom module; expose it here as ECHO's
    // enable/disable pair and keep the route cell honest through the same state.
    let limiterEnabled = null, limiterUsable = true;
    const limiterActions = node('div', undefined, 'dsp-module-actions');
    const mkLimiter = (label, target, ico) => {
      const b = node('button');
      b.type = 'button';
      b.append(glyph(ico, 14), node('span', label));
      b.addEventListener('click', async () => {
        b.disabled = true;
        try {
          const result = await pushControl('AudioFxSafetyLimiterToggle', { value: target }, 'headroom');
          if (result === false) return;
          limiterEnabled = target;
          syncLimiter();
        } finally { if (b.isConnected) b.disabled = !limiterUsable; }
      });
      return b;
    };
    const limiterOn = mkLimiter('开启限制器', true, 'shield');
    const limiterOff = mkLimiter('停用限制器', false, 'zap');
    limiterActions.append(limiterOn, limiterOff);
    const limiterNote = node('p', '限制器关闭时，超过满刻度的信号会直接削波。', 'dsp-safety-limiter-note');
    const syncLimiter = () => {
      limiterOn.dataset.active = String(limiterEnabled === true);
      limiterOff.dataset.active = String(limiterEnabled === false);
      limiterOn.disabled = limiterOff.disabled = !limiterUsable;
      limiterNote.hidden = limiterEnabled !== false;
      routeValues[2].textContent = limiterEnabled === null ? '—' : limiterEnabled ? '开启' : '关闭';
    };
    dsp('module', { module: 'headroom' }).then(result => {
      if (current !== 'safety' || !limiterOn.isConnected) return;
      const control = (result.controls || []).find(c => c.name === 'AudioFxSafetyLimiterToggle');
      limiterUsable = !control || control.enabled !== false;
      limiterEnabled = control ? !!control.value : null;
      syncLimiter();
    }).catch(error => { if (limiterOn.isConnected) shell.notify(error.message); });

    const meters = node('div', undefined, 'dsp-meters');
    const left = meterRow('左声道');
    const right = meterRow('右声道');
    meters.append(left.wrap, right.wrap);

    const stats = node('div', undefined, 'dsp-stats');
    const stat = (label) => {
      const cell = node('div', undefined, 'dsp-stat');
      const title = node('span', label, 'dsp-stat-label');
      const value = node('strong', '—', 'dsp-stat-value');
      cell.append(title, value);
      stats.append(cell);
      return value;
    };
    const peakValue = stat('峰值（保持）');
    const clipValue = stat('削波次数');
    const stateValue = stat('输出状态');
    const grValue = stat('压缩器衰减');

    const chainBox = node('div', undefined, 'dsp-chain');
    const chainHead = node('p', undefined, 'dsp-chain-label');
    chainHead.append(node('span', '采样率链路'));
    const chainText = node('span', '—', 'dsp-chain-value');
    chainHead.append(chainText);
    const activeLabel = node('p', undefined, 'dsp-chain-label');
    activeLabel.append(node('span', '正在处理的模块'));
    const activeText = node('span', '—', 'dsp-chain-value');
    activeLabel.append(activeText);
    chainBox.append(chainHead, activeLabel);

    const safetyBody = node('div', undefined, 'dsp-safety-body');
    const checks = node('div', undefined, 'dsp-safety-checks');
    const sectionHead = node('div', undefined, 'dsp-safety-section-head');
    const headSpan = node('span');
    headSpan.append(glyph('waves', 15), node('em', '输出检查'));
    sectionHead.append(headSpan);
    const checkMetrics = node('div', undefined, 'dsp-module-metrics dsp-safety-metrics');
    const check = (label) => {
      const card = node('span', undefined, 'dsp-module-metric');
      const value = node('strong', '—');
      card.append(node('em', label), value);
      checkMetrics.append(card);
      return { card, value };
    };
    const eqCheck = check('EQ'), srcCheck = check('SRC'), firCheck = check('卷积'), chCheck = check('声道工具'), reasonCheck = check('直通原因');
    checks.append(sectionHead, checkMetrics, meters, stats, chainBox);

    const next = node('aside', undefined, 'dsp-safety-next');
    const nextHead = node('span');
    nextHead.append(glyph('info', 15), node('em', '建议下一步'));
    const refreshBtn = node('button');
    refreshBtn.type = 'button';
    refreshBtn.append(glyph('activity', 14), node('span', '刷新'));
    refreshBtn.addEventListener('click', () => { refreshModuleDots(); pollLevels(); });
    next.append(nextHead, node('strong', '保留输出余量'), node('p', '开启限制器，在处理链末端防止过载；有削波记录时降低前级增益。'), limiterActions, limiterNote, refreshBtn);
    safetyBody.append(checks, next);
    panel.append(safetyBody);
    panel.append(node('p', '电平来自音频内核。完整输出格式与直通状态以播放器的音频链路信息为准。', 'dsp-module-note'));
    body.append(panel);

    const setMeter = (meter, peakDb, rmsDb) => {
      meter.bar.style.width = meterPct(peakDb) + '%';
      meter.bar.dataset.level = peakDb > -1 ? 'hot' : peakDb > -12 ? 'warm' : 'cool';
      meter.value.textContent = peakDb <= METER_MIN ? '—' : peakDb.toFixed(1) + ' dB';
      meter.track.dataset.rms = rmsDb <= METER_MIN ? '' : rmsDb.toFixed(1) + ' dB RMS';
    };

    const paint = data => {
      const tone=!data.session?'idle':data.overload==='clip'?'risk':data.overload==='near'?'warn':'safe';panel.dataset.tone=tone;heroTitle.textContent=!data.session?'等待播放':tone==='risk'?'输出已削波':tone==='warn'?'输出接近满刻度':'输出电平正常';heroDetail.textContent=data.bypass?'当前总旁路，请留意原始信号电平。':'输出峰值与削波统计来自实际音频内核。';
      routeValues[0].textContent = data.bypass ? '总旁路' : (data.activeDsp?.length ? '正在处理' : '无音效处理');
      routeValues[1].textContent = data.session ? Number(data.peakDb).toFixed(1)+' dBFS' : '未播放';
      routeValues[3].textContent = String(data.clipCount || 0);
      const on = data.bypass ? [] : (data.activeDsp || []);
      [[eqCheck,'EQ'],[srcCheck,'SRC'],[firCheck,'FIR'],[chCheck,'声道工具']].forEach(([item,label])=>{
        const active=on.includes(label);
        item.value.textContent=active?'参与':'旁路';
        item.card.dataset.tone=active?'good':undefined;
      });
      reasonCheck.value.textContent = data.bypass ? '总旁路开启' : data.bitPerfect ? '无模块参与，bit-perfect' : 'DSP 正在处理信号';
      setMeter(left, Number(data.left?.peak ?? METER_MIN), Number(data.left?.rms ?? METER_MIN));
      setMeter(right, Number(data.right?.peak ?? METER_MIN), Number(data.right?.rms ?? METER_MIN));

      const peak = Number(data.peakDb);
      if (!Number.isNaN(peak)) levelPeakHold = Math.max(levelPeakHold, peak);
      peakValue.textContent = levelPeakHold <= -140 ? '—' : levelPeakHold.toFixed(1) + ' dBFS';
      clipValue.textContent = data.clipCount > 0 ? data.clipCount + ' 次' : '0 次';
      stateValue.textContent = data.overload === 'clip' ? '已削波' : data.overload === 'near' ? '接近满刻度' : '正常';
      stateValue.dataset.state = data.overload === 'ok' ? 'ok' : 'warn';
      grValue.textContent = data.compressorGr > 0.05 ? '-' + Number(data.compressorGr).toFixed(1) + ' dB' : '0 dB';
      chainText.textContent = data.chain || '—';
      const list = (data.activeDsp || []);
      activeText.textContent = data.bypass ? '总旁路中' : (list.length ? list.join(' → ') : '无（直通）');
      activeText.dataset.state = data.bitPerfect ? 'ok' : 'warn';
      if (!data.session) {
        left.value.textContent = '—';
        right.value.textContent = '—';
        left.bar.style.width = '0%';
        right.bar.style.width = '0%';
      }
    };

    renderSafety.paint = paint;
    pollLevels();
  }


  async function pollLevels() {
    if (page.hidden || document.hidden || !live || telemetryBusy) return;
    const epoch = moduleEpoch; telemetryBusy = true;
    try {
      const data = await dsp('levels');
      if (epoch !== moduleEpoch || page.hidden) return;
      lastLevels = data;
      paintPipeline(); paintFocus();
      if (current === 'safety') renderSafety.paint?.(data);
      moduleVisual?.telemetry(data);
    } catch { /* a failed poll just leaves the last frame on screen */ }
    finally { telemetryBusy = false; }
  }

  // Group heading is emitted lazily: only when the group name changes, so the two top entries
  // (empty group) get none, exactly like the native nav. Items use ECHO's chain-item geometry:
  // handle + icon + copy + state, with the state check lit only for modules actually processing.
  const glyph = window.CelesteDspGlyphs || (() => document.createElement('span'));
  const outputCard = node('div', undefined, 'dsp-output-card');
  const outputIcon = node('span', undefined, 'dsp-output-icon');
  outputIcon.append(glyph('waves', 16));
  const outputCopy = node('div');
  const outputValue = node('strong', '—'), outputChain = node('small', '未播放');
  outputCopy.append(node('span', '输出链路'), outputValue, outputChain);
  outputCard.append(outputIcon, outputCopy);
  let lastGroup = null;
  MODULES.forEach(m => {
    if (m.group && m.group !== lastGroup) {
      nav.append(node('div', m.group, 'dsp-nav-label dsp-chain-stage'));
      lastGroup = m.group;
    }
    const b = node('button', undefined, 'dsp-chain-item');
    b.dataset.module = m.id;
    b.dataset.accent = m.accent || 'blue';
    const ico = node('span', undefined, 'dsp-chain-icon');
    ico.append(glyph(m.icon, 17));
    const copy = node('span', undefined, 'dsp-chain-copy');
    copy.append(node('strong', m.name));
    const state = node('span', undefined, 'dsp-chain-state');
    state.append(glyph('check', 14));
    if (m.id === 'rack') state.classList.add('always');
    b.append(node('span', undefined, 'dsp-chain-handle'), ico, copy, state);
    b.addEventListener('click', () => { current = m.id; renderModule(); });
    nav.append(b);
  });
  nav.prepend(outputCard);

  layout.append(nav, body);
  page.append(layout);
  document.querySelector('.main').append(page);

  document.addEventListener('celeste:navigate', event => {
    const active = event.detail === 'dsp';
    page.hidden = !active;
    if (!active) { moduleEpoch++; disposeEq?.(); disposeEq = null; moduleVisual?.dispose(); moduleVisual = null; ensureLevelPoll(false); return; }
    // shell.js leaves the placeholder category-page visible for any page it does not know about,
    // so both would render at once. Hide it like folders.js does.
    const placeholder = document.getElementById('category-page');
    if (placeholder) placeholder.hidden = true;
    shell.setPath([{ label: 'DSP', action: () => { } }]);
    renderModule();
    refreshModuleDots();
    if (live) {
      dsp('bypass').then(result => {
        rackToggle.setAttribute('aria-pressed', String(!!result.bypass));
        rackToggle.textContent = result.bypass ? '总旁路已开启' : '总旁路关闭';
      }).catch(() => { /* the label falls back to its default */ });
    }
  });
})();
