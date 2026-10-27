(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const node = (tag, text, className) => { const el = document.createElement(tag); if (text !== undefined) el.textContent = text; if (className) el.className = className; return el; };

  const drawer = $('chain-drawer'), scrim = $('chain-scrim');
  // 200ms 与原生面板的节拍一致（AudioChainPanelControl.StartLiveRefresh）；
  // 一秒太慢，电平条会明显一顿一顿。只在抽屉可见时跑，收起立刻停。
  const BEAT_MS = 200;
  let open = false, beat = 0, epoch = 0, busy = false;

  const setText = (id, text, hiddenWhenEmpty) => {
    const el = $(id); if (!el) return;
    el.textContent = text || '';
    if (hiddenWhenEmpty) el.hidden = !text;
  };
  const setSection = (id, hasContent) => { const el = $(id); if (el) el.hidden = !hasContent; };

  function fieldCell(field) {
    const cell = node('div');
    cell.append(node('span', field.label || '', 'chain-field-label'), node('strong', field.value || '', 'chain-field-value'));
    if (field.note) cell.append(node('small', field.note, 'chain-field-note'));
    return cell;
  }
  const fillFields = (id, fields) => {
    const target = $(id); target.replaceChildren(...(fields || []).map(fieldCell));
    return (fields || []).length > 0;
  };

  function renderLevels(levels) {
    const target = $('chain-levels'); target.replaceChildren();
    const channels = levels?.channels || 0, peak = levels?.peak || [], rms = levels?.rms || [];
    if (!channels) { target.append(node('p', '暂无信号可测。', 'chain-level-empty')); return; }
    const db = value => Math.max(-60, Math.min(0, value > 0 ? 20 * Math.log10(value) : -60));
    const percent = value => (db(value) + 60) / 60 * 100;
    for (let i = 0; i < channels; i++) {
      const row = node('div', undefined, 'chain-level-row');
      row.append(node('span', 'CH' + (i + 1)));
      const track = node('div', undefined, 'chain-level-track');
      const rmsBar = node('i', undefined, 'chain-level-rms'); rmsBar.style.width = percent(rms[i] || 0) + '%';
      const peakMark = node('i', undefined, 'chain-level-peak'); peakMark.style.left = 'calc(' + percent(peak[i] || 0) + '% - 1px)';
      track.append(rmsBar, peakMark);
      row.append(track);
      target.append(row);
    }
  }

  function renderNodes(nodes) {
    const target = $('chain-nodes'); target.replaceChildren();
    (nodes || []).forEach(item => {
      const row = node('li', undefined, 'chain-node');
      row.dataset.state = item.state || 'unknown';
      row.append(node('span', undefined, 'chain-node-dot'));
      const body = node('div');
      const head = node('div', undefined, 'chain-node-head');
      head.append(node('strong', item.title || '', 'chain-node-title'));
      if (item.stateText) head.append(node('span', item.stateText, 'chain-node-state'));
      body.append(head);
      if (item.detail) body.append(node('p', item.detail, 'chain-node-detail'));
      if (item.note) body.append(node('p', item.note, 'chain-node-note'));
      if (item.latencyMs !== null && item.latencyMs !== undefined) body.append(node('p', '延迟 ' + Number(item.latencyMs).toFixed(1) + ' ms', 'chain-node-latency'));
      body.className = 'chain-node-body';
      row.append(body);
      target.append(row);
    });
    $('chain-nodes-empty').hidden = (nodes || []).length > 0;
  }

  function renderEvents(events) {
    const target = $('chain-events'); target.replaceChildren();
    (events || []).forEach(item => {
      const row = node('li');
      row.append(node('time', item.time || ''), node('span', item.text || ''));
      target.append(row);
    });
    $('chain-events-empty').hidden = (events || []).length > 0;
  }

  function render(data) {
    setText('chain-stage-title', data.stageTitle);
    setText('chain-stage-note', data.stageNote);
    const badge = $('chain-layers');
    badge.hidden = !data.layerCount;
    if (data.layerCount) badge.textContent = data.layerCount + ' 层链路';
    setText('chain-summary', data.summaryLine);
    const verdict = $('chain-verdict');
    const reason = data.bitPerfect ? (data.bitPerfectConfirmed ? '' : '设备端尚未确认，判定基于当前链路状态。') : data.verdictReason;
    verdict.hidden = !reason;
    verdict.textContent = reason || '';
    verdict.dataset.perfect = String(!!data.bitPerfect);

    renderLevels(data.levels);
    setText('chain-source-value', data.sourceValue);
    setText('chain-source-note', data.sourceNote);
    setText('chain-process-value', data.processValue);
    setText('chain-process-note', data.processNote);
    fillFields('chain-fields', data.fields);

    const headroom = $('chain-headroom-section');
    headroom.hidden = !data.dspHeadroomValue;
    setText('chain-headroom', data.dspHeadroomValue);
    setText('chain-headroom-note', data.dspHeadroomNote);

    const tags = data.techTags || [];
    $('chain-tech').replaceChildren(...tags.map(tag => {
      const pill = node('span', tag.text || '', 'chain-tag'); pill.dataset.tone = tag.tone || 'neutral'; return pill;
    }));
    setSection('chain-tech-section', tags.length > 0);

    renderNodes(data.nodes);

    const diagnostics = data.diagnostics || [];
    $('chain-diag').replaceChildren(...diagnostics.map(item => {
      const card = node('div', undefined, 'chain-diag-item');
      card.append(node('span', item.category || ''), node('strong', item.title || ''));
      if (item.reason) card.append(node('p', item.reason, 'chain-diag-reason'));
      if (item.advice) card.append(node('p', item.advice, 'chain-diag-advice'));
      return card;
    }));
    setSection('chain-diag-section', diagnostics.length > 0);

    setText('chain-dac-device', data.dacDeviceName);
    const dacFields = fillFields('chain-dac-fields', data.dacFields);
    setSection('chain-dac-section', !!data.dacDeviceName || dacFields);

    renderEvents(data.events);

    setText('chain-stability-title', data.stabilityTitle);
    setText('chain-stability-note', data.stabilityNote);
    const stabilityFields = fillFields('chain-stability-fields', data.stabilityFields);
    setSection('chain-stability-section', !!data.stabilityTitle || stabilityFields);
  }

  async function refresh() {
    if (!open || !live) return;
    const token = epoch;
    if (busy) return;
    busy = true;
    try {
      const data = await window.CelesteTransport.request('audioChain', {});
      if (token === epoch) render(data);
    } catch (error) {
      if (token !== epoch) return;
      $('chain-summary').textContent = '读取链路失败：' + error.message;
    } finally { busy = false; }
  }

  function stop() {
    clearInterval(beat); beat = 0;
  }
  let hideTimer = 0;
  function show() {
    if (open) return;
    open = true; epoch++;
    clearTimeout(hideTimer);          // 上一轮还没走完的收起，立刻作废
    drawer.hidden = false; scrim.hidden = false;
    // 与既有面板同一条铁律：元素刚从隐藏变可见的同一帧开始动画会被吞掉，延一帧再加类。
    requestAnimationFrame(() => { drawer.classList.add('is-open'); scrim.classList.add('is-open'); });
    $('chain-button')?.setAttribute('aria-expanded', 'true');
    if (!live) { $('chain-summary').textContent = '浏览器预览没有本机音频引擎，链路数据需要在桌面播放器中查看。'; return; }
    refresh();
    stop();
    beat = setInterval(refresh, BEAT_MS);
  }
  function hide() {
    if (!open) return;
    open = false; const ticket = ++epoch; stop();
    drawer.classList.remove('is-open'); scrim.classList.remove('is-open');
    $('chain-button')?.removeAttribute('aria-expanded');
    clearTimeout(hideTimer);
    // 时长跟着同一套令牌走（含「弹窗」分类开关）：写死 240 的话，
    // 把速度调慢以后抽屉会先被藏起来、动画还没放完。
    hideTimer = setTimeout(() => {
      if (open || ticket !== epoch) return;   // 退场期间又被打开：这不许把新开的抽屉藏掉
      drawer.hidden = true; scrim.hidden = true;
    }, (window.CelesteMotion?.duration(220, 'popup') ?? 220) + 20);
  }

  $('chain-button')?.addEventListener('click', () => open ? hide() : show());
  $('chain-close').addEventListener('click', hide);
  scrim.addEventListener('click', hide);
  $('chain-open-settings').addEventListener('click', () => {
    hide();
    window.CelesteTransport?.open('audio', $('audio-settings'));
  });
  document.addEventListener('keydown', event => { if (event.key === 'Escape' && open) { event.preventDefault(); hide(); } });
  window.CelesteAudioChain = Object.freeze({ open: show, close: hide, isOpen: () => open });
})();
