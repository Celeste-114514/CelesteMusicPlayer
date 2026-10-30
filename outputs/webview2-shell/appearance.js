/* 外观工作区：预设库 + 明暗 + 元素参数 + 实时预览。
   数据全在宿主的 theme 通道里（预设、当前值、参数边界）；这里只负责画和预览。
   规则：
   - 选预设 / 切明暗 / 改强调色 = 即时预览的草稿（这些是"我现在要用哪套外观"）。
   - 拖颜色、拖滑杆 = 草稿预览，先只改草稿；点保存才落盘，点取消就回到进页面时的样子。
   - 换外观绝不碰音频、DSP、账号、曲库目录。 */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const node = (tag, text, cls) => {
    const el = document.createElement(tag);
    if (text !== undefined) el.textContent = text;
    if (cls) el.className = cls;
    return el;
  };
  const send = (operation, payload) =>
    window.CelesteTransport.request('theme', Object.assign({ operation }, payload));

  // 开放细调的语义色（只列真实有消费者的，避免摆一排改了没反应的输入框）
  const COLORS = [
    { id: 'accent', label: '强调色' },
    { id: 'bg', label: '窗口底色' },
    { id: 'shell', label: '工作区外壳' },
    { id: 'surface', label: '页面与卡片' },
    { id: 'panel', label: '次级面板' },
    { id: 'line', label: '分隔线' },
    { id: 'ink', label: '正文文字' },
    { id: 'muted', label: '次要文字' },
    { id: 'row-active', label: '选中行' }
  ];

  const actions=node('div',undefined,'settings-apply-bar');
  const applyAppearance=node('button','应用','primary'),revertAppearance=node('button','撤销','secondary'),applyNote=node('span','外观已应用');
  applyAppearance.id='settings-apply';revertAppearance.id='settings-revert';applyAppearance.disabled=true;revertAppearance.disabled=true;
  const resetSettings=node('button','恢复默认设置','secondary');
  actions.append(applyNote,resetSettings,revertAppearance,applyAppearance);$('settings-page').append(actions);
  function refreshActions(){const changed=dirty||!!window.CelesteSettingsDraft?.dirty;applyAppearance.disabled=!changed;revertAppearance.disabled=!changed;applyNote.textContent=changed?'设置有未应用的改动':'设置已应用';}
  document.addEventListener('celeste:settings-draft',refreshActions);
  applyAppearance.onclick=async()=>{try{await window.CelesteSettingsDraft?.commit();if(dirty)await request('commit');render();refreshActions();}catch(e){status(e.message,'err')}};
  revertAppearance.onclick=async()=>{try{await window.CelesteSettingsDraft?.cancel();if(dirty)await request('cancel');render();refreshActions();}catch(e){status(e.message,'err')}};
  resetSettings.onclick=async()=>{try{await window.CelesteSettingsDraft?.reset();await request('reset',{scope:'all'});render();refreshActions();}catch(e){status(e.message,'err')}};

  let data = null;             // 宿主快照
  let editing = 'light';       // 正在编辑哪一套明暗
  let dirty = false;
  let mountedHost = null;
  let gestureId = 0;

  function status(text, kind) {
    const bar = $('settings-status');
    if (!bar) return;
    bar.textContent = text || '';
    bar.className = 'settings-status' + (kind ? ' ' + kind : '');
  }

  function section(opts) {
    const card = node('section', undefined, 'settings-card');
    const head = node('div', undefined, 'settings-card-title');
    head.append(node('h2', opts.title));
    if (opts.tag) head.append(node('span', opts.tag, 'tag'));
    card.append(head);
    if (opts.note) card.append(node('p', opts.note, 'settings-card-note'));
    return card;
  }

  function button(label, onClick, cls) {
    const el = node('button', label, cls || 'set-mini');
    el.type = 'button';
    el.addEventListener('click', () => {
      Promise.resolve(onClick(el)).catch(error => status('操作失败：' + error.message, 'err'));
    });
    return el;
  }

  // 应用：只刷当前明暗这一套，并且用 data 里刚刚拿到的 bundle。
  // （之前先写 light 再写 dark、最后 setMode 用旧 state 覆盖，等于什么都没变 —— 就是它导致的"切换预设没反应"。）
  function paint() {
    if (!data) return;
    const theme = window.CelesteTheme;
    if (!theme) return;
    const mode = data.resolved === 'dark' ? 'dark' : 'light';
    const bundle = data[mode] || data.light;
    if (!bundle) return;
    theme.applyBundle(mode, bundle);
  }

  async function reload() {
    data = await request('get');
    dirty = data.draft;
    paint();
  }

  // 草稿一定要交给宿主算：改强调色会连带 on-accent / 选中行这些派生值，
  // 本地自己拼 CSS 会漏掉它们，也会让「保存」时和预览不一致。
  // 所有命令共用一条队列：快速拖动后立即撤销/保存，也按操作顺序执行。
  let queue = Promise.resolve();
  const history = [], future = [];
  const HISTORY_LIMIT = 80;
  const EDITS = new Set(['preview', 'select', 'mode', 'accent', 'background', 'pickBackground',
    'clearBackground', 'typography', 'navItems', 'playerButtons', 'reset', 'motion']);
  const NOTES = { select: '切换预设', mode: '明暗模式', accent: '强调色', background: '背景设置',
    pickBackground: '背景资源', clearBackground: '去掉背景', typography: '字体', navItems: '导航项目',
    playerButtons: '底栏按钮', reset: '恢复当前预设', motion: '动效' };
  const clone = value => JSON.parse(JSON.stringify(value));
  let lastChangeAt = 0;
  function enqueue(action) {
    const pending = queue.then(action);
    queue = pending.catch(() => {}); // 一次失败不能堵死后续操作
    return pending;
  }
  function accept(result) {
    data = result;
    dirty = !!result.draft;
    refreshActions();
    paint();
    window.CelestePlayerLayout?.apply?.(result.playerButtons);
    window.CelesteNavLayout?.apply?.(result.navItems);
    refreshHistoryButtons();
  }
  function refreshHistoryButtons() {
    if ($('history-undo')) $('history-undo').disabled = history.length === 0;
    if ($('history-redo')) $('history-redo').disabled = future.length === 0;
    const tag = mountedHost?.querySelector('.settings-card .tag');
    if (tag && data) tag.textContent = currentPresetName() + (dirty ? ' · 有未保存的改动' : '');
  }
  function resetHistory() {
    history.length = future.length = 0;
    lastChangeAt = 0;
    refreshHistoryButtons();
  }
  function recordHistory(before, after, note, mergeKey) {
    if (!before || !after || JSON.stringify(before) === JSON.stringify(after)) return;
    const now = Date.now(), previous = history[history.length - 1];
    if (mergeKey && previous?.mergeKey === mergeKey
        && (mergeKey.startsWith('gesture:') || now - lastChangeAt < 700)) {
      previous.after = clone(after);
      previous.note = note;
    } else {
      history.push({ before: clone(before), after: clone(after), note, mergeKey });
      if (history.length > HISTORY_LIMIT) history.shift();
    }
    future.length = 0;
    lastChangeAt = now;
  }
  function request(operation, payload, note, mergeKey) {
    return enqueue(async () => {
      // 必须在请求执行时取 before，不能在入队时读过期快照。
      const before = data?.editState && clone(data.editState);
      const result = await send(operation, payload);
      accept(result);
      if (EDITS.has(operation)) recordHistory(before, result.editState, note || NOTES[operation] || operation, mergeKey);
      else if (['commit', 'cancel', 'savePreset', 'deletePreset'].includes(operation)) resetHistory();
      refreshHistoryButtons();
      return result;
    });
  }
  function replay(from, to, label) {
    return enqueue(async () => {
      const entry = from[from.length - 1];
      if (!entry) return;
      // 宿主确认成功之后才移栈；失败保留原来的历史，允许重试。
      const result = await send('restoreDraft', { state: label === '撤销' ? entry.before : entry.after });
      from.pop(); to.push(entry);
      lastChangeAt = 0;
      entry.mergeKey = null;
      accept(result);
      render();
      status('已' + label + '：' + entry.note, 'ok');
    }).catch(error => status(label + '失败：' + error.message, 'err'));
  }
  const undo = () => replay(history, future, '撤销');
  const redo = () => replay(future, history, '重做');
  document.addEventListener('keydown', event => {
    if (!(event.ctrlKey || event.metaKey) || event.altKey || event.defaultPrevented) return;
    if (!mountedHost?.isConnected || $('settings-page')?.hidden || !mountedHost.querySelector('#history-undo')) return;
    const target = event.target;
    if (target?.isContentEditable || target?.tagName === 'TEXTAREA'
        || (target?.tagName === 'INPUT' && !['range', 'color', 'checkbox', 'button'].includes(target.type))) return;
    const key = (event.key || '').toLowerCase();
    if (key === 'z') { event.preventDefault(); event.shiftKey ? redo() : undo(); }
    else if (key === 'y') { event.preventDefault(); redo(); }
  });
  function pushChange(changes, note, mergeKey) {
    return request('preview', { changes }, note, mergeKey || Object.keys(changes).sort().join('|'))
      .then(() => status('预览中（还没保存）：' + note, 'warn'))
      .catch(error => status('预览失败：' + error.message, 'err'));
  }

  function currentPresetName() {
    const preset = (data.presets || []).find(item => item.id === data.presetId);
    return preset ? preset.name : '未选择';
  }

  // —— 预设卡片：缩略图用真实语义色拼一个小页面，不是一块色样 ——
  function presetCard(preset) {
    const card = node('button', undefined, 'preset-card' + (preset.id === data.presetId ? ' selected' : ''));
    card.type = 'button';
    const scheme = data.resolved === 'dark' ? preset.dark : preset.light;
    const art = node('span', undefined, 'preset-art');
    art.style.setProperty('--p-bg', scheme.bg);
    art.style.setProperty('--p-shell', scheme.shell);
    art.style.setProperty('--p-surface', scheme.surface);
    art.style.setProperty('--p-accent', scheme.accent);
    art.style.setProperty('--p-ink', scheme.ink);
    art.style.setProperty('--p-muted', scheme.muted || scheme.ink);
    art.style.setProperty('--p-line', scheme.line);
    art.style.setProperty('--p-radius', Math.max(4, Math.min(14, preset.radius / 3)) + 'px');
    art.append(node('i', undefined, 'preset-art-bar'));
    art.append(node('i', undefined, 'preset-art-row one'));
    art.append(node('i', undefined, 'preset-art-row two'));
    art.append(node('i', undefined, 'preset-art-card'));
    card.append(art);
    card.append(node('strong', preset.name));
    card.append(node('small', preset.group + (preset.custom ? ' · 我的' : '')));
    card.addEventListener('click', async () => {
      data = await request('select', { presetId: preset.id });
      paint();
      render();
      status('正在预览「' + preset.name + '」，点保存后保留。', 'warn');
    });
    return card;
  }

  function modePicker() {
    const wrap = node('div', undefined, 'mode-picker');
    [['light', '浅色'], ['dark', '深色'], ['system', '跟随系统']].forEach(([value, label]) => {
      const item = node('button', label, 'mode-option' + (data.mode === value ? ' selected' : ''));
      item.type = 'button';
      item.addEventListener('click', async () => {
        data = await request('mode', { mode: value });
        editing = data.resolved === 'dark' ? 'dark' : 'light';
        paint();
        render();
        status(value === 'system' ? '已改为跟随系统明暗。' : '已切到' + label + '。', 'ok');
      });
      wrap.append(item);
    });
    return wrap;
  }

  // —— 颜色细调：明暗各一套，改哪套就预览哪套 ——
  function colorRow(item) {
    const row = node('div', undefined, 'set-row');
    const main = node('div', undefined, 'set-row-main');
    main.append(node('div', item.label, 'set-label'));
    row.append(main);
    const holder = node('div', undefined, 'set-control');
    const wrap = node('div', undefined, 'set-slider');
    const picker = document.createElement('input');
    picker.type = 'color';
    // 遮罩/投影这类值带透明度（#RRGGBBAA），不能整串塞给 type=color ——
    // 8 位值是非法的，浏览器会把它变成黑色，用户一碰就把透明度弄丢。
    // 所以只把 RGB 交给取色器，写回时再把原来的 alpha 接回去。
    const stored = String((data[editing].values[item.id] || '') || '#888888');
    const alpha = stored.length === 9 ? stored.slice(7) : '';
    const rgb = stored.length === 9 ? stored.slice(0, 7) : stored;
    picker.value = /^#[0-9a-fA-F]{6}$/.test(rgb) ? rgb : '#888888';
    const hex = node('span', picker.value + alpha, 'set-color-hex');
    const push = value => pushChange({ [editing + ':' + item.id]: value + alpha }, item.label + ' ' + value + alpha);
    picker.addEventListener('input', () => { hex.textContent = picker.value + alpha; });
    picker.addEventListener('change', () => { hex.textContent = picker.value + alpha; push(picker.value); });
    wrap.append(picker, hex);
    holder.append(wrap);
    row.append(holder);
    return row;
  }

  function rebuildCss(values) {
    return Object.entries(values).map(([key, value]) => '--' + key + ':' + value).join(';');
  }

  // —— 数值参数 ——
  function numberRow(item) {
    const row = node('div', undefined, 'set-row');
    const main = node('div', undefined, 'set-row-main');
    main.append(node('div', item.label, 'set-label'));
    row.append(main);
    const holder = node('div', undefined, 'set-control');
    const wrap = node('div', undefined, 'set-slider');
    const range = document.createElement('input');
    range.type = 'range';
    range.min = item.min; range.max = item.max; range.step = item.step;
    range.value = item.value;
    const readout = node('span', format(item), 'set-readout');
    const fill = () => range.style.setProperty('--fill',
      ((Number(range.value) - item.min) / Math.max(0.0001, item.max - item.min) * 100) + '%');
    fill();
    // 数值参数浅深色同步改，避免只改一半导致两套外观不配套
    let gesture = '';
    range.addEventListener('pointerdown', () => { gesture = 'gesture:' + (++gestureId); });
    ['pointerup', 'pointercancel', 'blur'].forEach(type => range.addEventListener(type, () => { gesture = ''; }));
    const push = value => {
      const text = value + (item.unit === 'px' ? 'px' : '');
      pushChange({ ['light:' + item.id]: text, ['dark:' + item.id]: text }, item.label + ' ' + text, gesture);
    };
    range.addEventListener('input', () => {
      readout.textContent = Number(range.value).toFixed(item.step < 0.1 ? 2 : 0) + (item.unit === '×' ? '×' : ' ' + item.unit);
      fill();
      push(Number(range.value));
    });
    wrap.append(range, readout);
    holder.append(wrap);
    row.append(holder);
    return row;
  }

  function format(item) {
    return Number(item.value).toFixed(item.step < 0.1 ? 2 : 0) + (item.unit === '×' ? '×' : ' ' + item.unit);
  }

  // —— 按钮与输入框 / 弹窗与浮层 ——
  // 这两类元素在页面里到处都是，以前全挤在一个 --accent-soft 上：
  // 想"悬停淡一点"，选中态会跟着一起淡。现在拆开，各调各的。
  function stateCards() {
    const input = section({
      title: '按钮与输入框',
      note: '悬停/按下只管鼠标反馈，不影响「已选中」。输入框那三支是搜索框、下拉、文本框共用的。'
    });
    [
      { id: 'hover', label: '悬停底色' },
      { id: 'pressed', label: '按下底色' },
      { id: 'focus', label: '焦点环' },
      { id: 'input-bg', label: '输入框底色' },
      { id: 'input-line', label: '输入框边框' }
    ].forEach(item => input.append(colorRow(item)));
    (data.numbers || []).filter(item => item.id === 'disabled-opacity').forEach(item => input.append(numberRow(item)));

    const layer = section({
      title: '弹窗与浮层',
      note: '菜单、右键菜单、音频/队列面板、确认框共用这几支：底色、投影、以及后面那层遮罩。投影和遮罩本来就带透明度，改色不会把透明度改掉。'
    });
    [
      { id: 'menu-bg', label: '浮层底色' },
      { id: 'shadow', label: '浮层投影' },
      { id: 'overlay', label: '遮罩' }
    ].forEach(item => layer.append(colorRow(item)));
    return [input, layer];
  }

  // —— 动效：模式 / 速度 / 幅度 / 回弹风格 ——
  // 这几项是个人偏好，宿主单独存，不随外观预设切换；
  // 换配色不会把用户调过的动效重置（这是方案里写死的规矩）。
  function motionSlider(item) {
    const row = node('div', undefined, 'set-row');
    const main = node('div', undefined, 'set-row-main');
    main.append(node('div', item.label, 'set-label'));
    if (item.hint) main.append(node('p', item.hint, 'set-note'));
    row.append(main);
    const holder = node('div', undefined, 'set-control');
    const wrap = node('div', undefined, 'set-slider');
    const range = document.createElement('input');
    range.type = 'range';
    range.min = item.min; range.max = item.max; range.step = item.step;
    range.value = item.value;
    const show = value => Number(value).toFixed(2) + '×';
    const readout = node('span', show(item.value), 'set-readout');
    const fill = () => range.style.setProperty('--fill',
      ((Number(range.value) - item.min) / Math.max(0.0001, item.max - item.min) * 100) + '%');
    fill();
    let gesture = '';
    range.addEventListener('pointerdown', () => { gesture = 'gesture:motion-' + item.id + ':' + (++gestureId); });
    ['pointerup', 'pointercancel', 'blur'].forEach(type => range.addEventListener(type, () => { gesture = ''; }));
    range.addEventListener('input', () => {
      readout.textContent = show(range.value);
      fill();
      request('motion', { [item.id]: Number(range.value) }, item.label + ' ' + show(range.value), gesture)
        .catch(error => status('设置失败：' + error.message, 'err'));
    });
    wrap.append(range, readout);
    holder.append(wrap);
    row.append(holder);
    return row;
  }

  function motionCard() {
    const info = data.motion || {};
    const card = section({
      title: '动效',
      note: '这一页的设置跟着你走，不随外观预设切换：换配色不会把动效重置。系统开了「减少动画」时，「跟随系统」会自动压掉位移和弹跳，但状态变化仍然看得见。'
    });

    const modeRow = node('div', undefined, 'set-row');
    const modeMain = node('div', undefined, 'set-row-main');
    modeMain.append(node('div', '动画模式', 'set-label'));
    modeMain.append(node('p', '关闭 = 什么都不动；轻量 = 只留颜色与状态变化；完整 = 位移和回弹一起。', 'set-note'));
    modeRow.append(modeMain);
    const modeHolder = node('div', undefined, 'set-control');
    const modeGroup = node('div', undefined, 'mode-group');
    const currentMode = info.mode || 'system';
    [['system', '跟随系统'], ['full', '完整'], ['lite', '轻量'], ['off', '关闭']].forEach(([value, label]) => {
      const item = node('button', label, 'mode-option' + (currentMode === value ? ' selected' : ''));
      item.type = 'button';
      item.addEventListener('click', () => {
        request('motion', { mode: value }, '动效模式：' + label)
          .catch(error => status('设置失败：' + error.message, 'err'));
      });
      modeGroup.append(item);
    });
    modeHolder.append(modeGroup);
    modeRow.append(modeHolder);
    card.append(modeRow);

    card.append(motionSlider({
      id: 'speed', label: '动画速度',
      min: info.speedMin ?? 0.6, max: info.speedMax ?? 1.6, step: info.speedStep ?? 0.05,
      value: info.speed ?? 1, hint: '越大越快。1 是基准；0.6 更拖沓，1.6 更干脆。'
    }));
    card.append(motionSlider({
      id: 'strength', label: '位移幅度',
      min: info.strengthMin ?? 0, max: info.strengthMax ?? 1.25, step: info.strengthStep ?? 0.05,
      value: info.strength ?? 1, hint: '0 = 只换颜色，不做位移和回弹；不影响浮层贴在哪里、也不影响点击范围。'
    }));

    const bounceRow = node('div', undefined, 'set-row');
    const bounceMain = node('div', undefined, 'set-row-main');
    bounceMain.append(node('div', '回弹风格', 'set-label'));
    bounceMain.append(node('p', '按压和展开时的回弹手感。克制最稳，灵动最明显。', 'set-note'));
    bounceRow.append(bounceMain);
    const bounceHolder = node('div', undefined, 'set-control');
    const bounceGroup = node('div', undefined, 'mode-group');
    const currentBounce = info.bounce || 'soft';
    [['calm', '克制'], ['soft', '柔和'], ['lively', '灵动']].forEach(([value, label]) => {
      const item = node('button', label, 'mode-option' + (currentBounce === value ? ' selected' : ''));
      item.type = 'button';
      item.addEventListener('click', () => {
        request('motion', { bounce: value }, '回弹风格：' + label)
          .catch(error => status('设置失败：' + error.message, 'err'));
      });
      bounceGroup.append(item);
    });
    bounceHolder.append(bounceGroup);
    bounceRow.append(bounceHolder);
    card.append(bounceRow);

    // 分类开关：关掉哪一类，哪一类就只剩"状态变化"。分类是为了让用户能"少动一点"，
    // 不是给几十个难懂的参数。
    const categories = Array.isArray(info.categories) && info.categories.length
      ? info.categories
      : [{ id: 'nav', label: '导航与页面转场' }, { id: 'popup', label: '弹窗与浮层' }, { id: 'list', label: '列表重排' },
         { id: 'control', label: '控件反馈' }, { id: 'theme', label: '主题与背景' }, { id: 'cover', label: '封面转场' }];
    const off = new Set(Array.isArray(info.off) ? info.off : []);
    const catRow = node('div', undefined, 'set-row');
    const catMain = node('div', undefined, 'set-row-main');
    catMain.append(node('div', '分类开关', 'set-label'));
    catMain.append(node('p', '单独关掉某一类动效。关掉的类别不会消失，只是不再移动 —— 状态变化照样看得见。', 'set-note'));
    catRow.append(catMain);
    const catHolder = node('div', undefined, 'set-control');
    const catList = node('div', undefined, 'motion-category-list');
    categories.forEach(item => {
      const disabled = off.has(item.id);
      const chip = node('button', item.label + (disabled ? ' · 已关' : ''), 'motion-category' + (disabled ? ' off' : ''));
      chip.type = 'button';
      chip.setAttribute('aria-pressed', String(!disabled));
      chip.addEventListener('click', () => {
        const next = new Set(off);
        if (next.has(item.id)) next.delete(item.id); else next.add(item.id);
        request('motion', { off: [...next] }, '动效分类：' + item.label)
          .catch(error => status('设置失败：' + error.message, 'err'));
      });
      catList.append(chip);
    });
    catHolder.append(catList);
    catRow.append(catHolder);
    card.append(catRow);

    // 演示：把当前这套参数直接跑一遍，改完立刻能看见，不用自己到处找地方试
    const demoRow = node('div', undefined, 'set-row');
    const demoMain = node('div', undefined, 'set-row-main');
    demoMain.append(node('div', '演示', 'set-label'));
    demoMain.append(node('p', '按当前参数跑一遍：浮层进出 + 页面转场 + 按压回弹。', 'set-note'));
    demoRow.append(demoMain);
    const demoHolder = node('div', undefined, 'set-control');
    demoHolder.append(button('演示一下', () => runMotionDemo(card)));
    demoRow.append(demoHolder);
    card.append(demoRow);
    return card;
  }

  /** 演示用的浮层：按当前参数进出一次，再做一次页面式转场。 */
  function runMotionDemo(host) {
    const motion = window.CelesteMotion;
    if (!motion) { status('动效控制器还没就绪。', 'err'); return; }
    let layer = host.querySelector('.motion-demo-layer');
    if (!layer) {
      layer = node('div', undefined, 'motion-demo-layer');
      layer.append(node('p', '这是一个浮层：进场、退场都走统一控制器。', 'motion-demo-text'));
      host.append(layer);
    }
    const popup = window.CelestePopup;
    popup.open({
      id: 'motion-demo', element: layer, anchor: host, placement: 'bottom-start',
      maxHeight: 200, dismissable: true
    });
    setTimeout(() => {
      popup.close('motion-demo');
      const sample = host.querySelector('.motion-demo-sample');
      if (sample) motion.page(sample, 'forward');
    }, 700);
  }

  // —— 半圆导航：几何 + 项目构成 ——
  const NAV_GROUPS = ['ring1', 'ring2'];
  const NAV_GROUP_LABELS = { ring1: '内圈（主要分类）', ring2: '外圈（收藏与历史）' };

  function navCards() {
    const geometry = section({
      title: '半圆导航',
      note: '轮盘的整体尺寸、图标大小和两圈半径属于同一套几何：改半径时轨道线会跟着重画，不会出现图标跑出轨道。'
    });
    [{ id: 'nav-track', label: '轨道与分隔线' }, { id: 'nav-ink', label: '普通项目颜色' }]
      .forEach(item => geometry.append(colorRow(item)));
    (data.numbers || []).filter(item => /^nav-/.test(item.id || '')).forEach(item => geometry.append(numberRow(item)));

    const items = section({
      title: '导航项目',
      tag: '预览 · 可撤销',
      note: '收起后同一圈剩下的项目会重新均分这段弧，不会留空位；每圈至少保留一个 —— 整圈都收起来的话轮盘算不出角度，会全叠在一起。'
    });
    const list = node('div', undefined, 'player-button-list');
    items.append(list);
    const catalog = new Map((data.navItemCatalog || []).map(item => [item.id, item]));

    function push(map) {
      return request('navItems', { value: serializeComposition(map, NAV_GROUPS) }).then(result => {
        data = result;
        // 宿主已经收敛过（每圈至少一个可见），以它回的为准
        if (window.CelesteNavLayout) window.CelesteNavLayout.apply(result.navItems);
        fill();
        status('导航项目已预览，保存后保留。', 'ok');
      }).catch(error => status('调整导航项目失败：' + error.message, 'err'));
    }

    function fill() {
      const map = parseComposition(data.navItems, NAV_GROUPS);
      list.replaceChildren();
      NAV_GROUPS.forEach(group => {
        const entries = map[group] || [];
        if (entries.length === 0) return;
        list.append(node('p', NAV_GROUP_LABELS[group], 'set-note'));
        entries.forEach((item, index) => {
          const def = catalog.get(item.id) || { label: item.id };
          const row = node('div', undefined, 'set-row');
          const main = node('div', undefined, 'set-row-main');
          main.append(node('div', (index + 1) + '. ' + def.label, 'set-label'));
          row.append(main);
          const holder = node('div', undefined, 'set-control');
          const actions = node('div', undefined, 'player-button-actions');
          actions.append(button(item.hidden ? '显示' : '收起', () => {
            item.hidden = !item.hidden;
            return push(map);
          }));
          const up = button('前移', () => { swap(entries, index, index - 1); return push(map); });
          up.disabled = index === 0;
          const down = button('后移', () => { swap(entries, index, index + 1); return push(map); });
          down.disabled = index === entries.length - 1;
          actions.append(up, down);
          holder.append(actions);
          row.append(holder);
          list.append(row);
        });
      });
    }

    fill();
    return [geometry, items];
  }

  // —— 背景 ——
  const BG_MODES = [
    ['solid', '纯色（跟随外壳色）'],
    ['gradient', '三色渐变'],
    ['cover', '当前专辑封面'],
    ['image', '我选的图片'],
    ['video', '我选的视频（静音循环）']
  ];

  function backgroundCard() {
    const card = section({ title: '图片与视频背景', tag: '预览 · 可撤销',
      note: '图片和视频保存在播放器的资源目录，原文件移动不影响使用。视频静音循环，窗口最小化或页面隐藏时暂停。改动需保存；取消选文件不会改变背景。' });
    const values = data[data.resolved === 'dark' ? 'dark' : 'light'].values;
    const currentMode = values['bg-mode'] || 'solid';
    const row = node('div', undefined, 'set-row');
    row.append(node('div', '背景类型', 'set-label'));
    const modeSelect = node('select');
    modeSelect.id = 'background-mode';
    BG_MODES.forEach(([value, label]) => modeSelect.append(new Option(label, value)));
    modeSelect.value = currentMode;
    modeSelect.addEventListener('change', async () => {
      try {
        const mode = modeSelect.value;
        const hasAsset = mode === 'video' ? data.editState?.backgroundVideo : data.editState?.backgroundImage;
        if ((mode === 'image' || mode === 'video') && !hasAsset) await request('pickBackground', { media: mode });
        else await request('background', { mode });
        render();
      } catch (error) { render(); status('改背景失败：' + error.message, 'err'); }
    });
    row.append(modeSelect); card.append(row);
    const actions = node('div', undefined, 'settings-actions');
    async function pick(media) {
      await request('pickBackground', { media });
      render();
    }
    actions.append(button('选择图片…', () => pick('image')), button('选择视频…', () => pick('video')),
      button('去掉背景', async () => { await request('clearBackground'); render(); }));
    if (currentMode === 'video') actions.append(button('播放视频背景', () => window.CelesteBackground?.resume()));
    card.append(actions);
    const feedback = node('p', window.CelesteBackground?.status || '', 'set-note');
    feedback.id = 'background-status'; feedback.setAttribute('role', 'status');
    card.append(feedback);
    card.append(node('p', '图片：JPG / PNG / BMP / WebP，最大 40 MB。视频：MP4 / WebM / M4V，最大 512 MB；编码兼容性以当前 WebView2 为准。', 'set-note'));
    [['bg-fit', '适配方式', [['cover', '铺满（裁边）'], ['contain', '完整显示（留边）']]],
     ['bg-pos', '对齐位置', [['center', '居中'], ['top', '顶部对齐'], ['bottom', '底部对齐']]]]
      .forEach(([id, label, options]) => {
        const row = node('div', undefined, 'set-row');
        row.append(node('div', label, 'set-label'));
        const select = node('select');
        select.id = id;
        options.forEach(([value, text]) => select.append(new Option(text, value)));
        select.value = values[id] || options[0][0];
        select.addEventListener('change', () => request('background', { [id === 'bg-fit' ? 'fit' : 'pos']: select.value })
          .catch(error => status('改背景失败：' + error.message, 'err')));
        row.append(select); card.append(row);
      });
    ['bg-grad-1', 'bg-grad-2', 'bg-grad-3'].forEach((id, index) => card.append(colorRow({ id, label: '渐变第 ' + (index + 1) + ' 色' })));
    (data.numbers || []).filter(item => /^bg-/.test(item.id || '')).forEach(item => card.append(numberRow(item)));
    return card;
  }
  document.addEventListener('celeste:background-status', event => {
    if ($('background-status')) $('background-status').textContent = event.detail.message;
  });

  // —— 字体与排版 ——
  // 字体作为个人偏好保留，但修改也参与草稿、撤销和保存；
  // 字号缩放与行高属于当前这套外观，仍走「预览 → 保存」那套。
  const FONT_FIELDS = [
    { key: 'fontUi', label: '西文与界面字体', note: '拉丁字母、数字、控件文字先用它' },
    { key: 'fontCjk', label: '中文字体', note: '上面那套字体里没有汉字时用这套' },
    { key: 'fontMono', label: '等宽字体', note: '播放进度、时长这类要对齐的数字' }
  ];

  function typographyCard() {
    const card = section({
      title: '字体与排版',
      tag: '预览 · 可撤销',
      note: '字体清单来自这台机器已安装的字体；改名或卸载后会被标出来。字号缩放和行高是当前这套外观的一部分，跟着上面的预览/保存走。'
    });

    // 实时样本：它没有任何自己的 font-family/size，全靠根元素变量，
    // 所以拖动下面两个滑杆时它跟真实页面一起变 —— 不会出现"样本对、页面不对"。
    const sample = node('div', undefined, 'type-sample');
    sample.append(node('strong', 'Celeste · 音乐播放器'));
    sample.append(node('span', ' 永不失联的爱 · 5:03 · FLAC 24bit/96kHz'));
    card.append(sample);

    FONT_FIELDS.forEach(field => {
      const row = node('div', undefined, 'set-row');
      const main = node('div', undefined, 'set-row-main');
      main.append(node('div', field.label, 'set-label'), node('div', field.note, 'set-note'));
      row.append(main);
      const holder = node('div', undefined, 'set-control');
      const select = node('select');
      const current = (data.typography || {})[field.key] || '';
      const names = data.fonts || [];
      // 存着的字体可能已经被卸载：仍然列出来并明确标注，否则下拉框会静默跳到第一项，
      // 用户什么都没动却"字体变了"
      if (current && !names.includes(current)) select.append(new Option(current + '（系统里没找到）', current));
      names.forEach(name => select.append(new Option(name, name)));
      select.value = current;
      select.addEventListener('change', async () => {
        try {
          data = await request('typography', { [field.key]: select.value });
          paint();
          status('字体预览：' + select.value + '。', 'ok');
        } catch (error) { status('改字体失败：' + error.message, 'err'); }
      });
      holder.append(select);
      row.append(holder);
      card.append(row);
    });

    (data.numbers || []).filter(item => /^(text-scale|line-height)$/.test(item.id || ''))
      .forEach(item => card.append(numberRow(item)));
    return card;
  }

  // —— 底栏按钮：显隐与顺序 ——
  // 宿主存的是 "left:a,!b|right:…|tools:…" 这种串（! = 收起来），
  // 这里只解析和拼回，合法性校验全在宿主（NormalizePlayerButtons）做。
  const BUTTON_GROUPS = ['left', 'right', 'tools'];
  const BUTTON_GROUP_LABELS = {
    left: '播放键左侧',
    right: '播放键右侧',
    tools: '最右工具区（歌词 / 音量 / 音频设置）'
  };

  // 构成串的通用读写：底栏按钮和半圆导航用的是同一套格式（组:项,项|组:…，`!` = 收起）。
  // 合法性校验都在宿主做，这里只负责拆和拼，免得两端各有一套规则互相打架。
  function parseComposition(text, groups) {
    const map = {};
    groups.forEach(group => { map[group] = []; });
    String(text || '').split('|').forEach(chunk => {
      const split = chunk.indexOf(':');
      if (split <= 0) return;
      const group = chunk.slice(0, split).trim();
      if (!groups.includes(group)) return;
      chunk.slice(split + 1).split(',').forEach(token => {
        const value = token.trim();
        if (!value) return;
        map[group].push(value.charAt(0) === '!'
          ? { id: value.slice(1), hidden: true }
          : { id: value, hidden: false });
      });
    });
    return map;
  }

  function serializeComposition(map, groups) {
    return groups
      .map(group => group + ':' + (map[group] || []).map(item => (item.hidden ? '!' : '') + item.id).join(','))
      .join('|');
  }

  function swap(list, from, to) {
    if (to < 0 || to >= list.length) return;
    const hold = list[from];
    list[from] = list[to];
    list[to] = hold;
  }

  /**
   * 底栏按钮卡片。改动实时预览并进入统一草稿，
   * 改完只重画这张卡片自己的行，
   * 不整页 render()，否则用户滚到页面底部一动就会跳回顶部。
   */
  function playerButtonCard() {
    const card = section({
      title: '底栏按钮',
      tag: '预览 · 可撤销',
      note: '收起来 = 从底栏拿掉；「上一首 / 下一首」是核心，不能收。播放键永远在正中，不在这里。顺序就是它们在底栏里从左到右的位置。'
    });
    const list = node('div', undefined, 'player-button-list');
    card.append(list);
    const catalog = new Map((data.playerButtonCatalog || []).map(item => [item.id, item]));

    function push(map) {
      return request('playerButtons', { value: serializeComposition(map, BUTTON_GROUPS) }).then(result => {
        data = result;
        // 宿主已经收敛过（核心强制显示、不认识的丢掉），以它回的为准
        if (window.CelestePlayerLayout) window.CelestePlayerLayout.apply(result.playerButtons);
        fill();
        status('底栏按钮已预览，保存后保留。', 'ok');
      }).catch(error => status('调整底栏按钮失败：' + error.message, 'err'));
    }

    function fill() {
      const map = parseComposition(data.playerButtons, BUTTON_GROUPS);
      list.replaceChildren();
      BUTTON_GROUPS.forEach(group => {
        const items = map[group] || [];
        if (items.length === 0) return;
        list.append(node('p', BUTTON_GROUP_LABELS[group], 'set-note'));
        items.forEach((item, index) => {
          const def = catalog.get(item.id) || { label: item.id, core: false };
          const row = node('div', undefined, 'set-row');
          const main = node('div', undefined, 'set-row-main');
          main.append(node('div', (index + 1) + '. ' + def.label, 'set-label'));
          row.append(main);
          const holder = node('div', undefined, 'set-control');
          const actions = node('div', undefined, 'player-button-actions');
          if (def.core) actions.append(node('span', '核心，不能隐藏', 'set-note'));
          else actions.append(button(item.hidden ? '显示' : '收起', () => {
            item.hidden = !item.hidden;
            return push(map);
          }));
          const up = button('前移', () => { swap(items, index, index - 1); return push(map); });
          up.disabled = index === 0;
          const down = button('后移', () => { swap(items, index, index + 1); return push(map); });
          down.disabled = index === items.length - 1;
          actions.append(up, down);
          holder.append(actions);
          row.append(holder);
          list.append(row);
        });
      });
    }

    fill();
    return card;
  }

  // —— 主渲染 ——
  function render(host) {
    host = host || mountedHost || $('settings-scroll');
    mountedHost = host;
    if (!host) return;
    const scrollTop = host.scrollTop;
    host.replaceChildren();

    const head = section({ title: '当前外观', tag: currentPresetName() + (dirty ? ' · 有未保存的改动' : '') });
    head.append(modePicker());
    if (dirty) head.append(node('p', '下面这些改动还只是预览，点「保存」才会写进设置文件。', 'set-note'));
    const headActions = node('div', undefined, 'settings-actions');
    // 撤销/重做排在保存前面：它们只影响还没保存的预览，不落盘
    const undoButton = button('撤销', () => undo(), 'set-mini');
    undoButton.id = 'history-undo';
    undoButton.disabled = history.length === 0;
    undoButton.title = '撤回上一步预览（Ctrl+Z）';
    const redoButton = button('重做', () => redo(), 'set-mini');
    redoButton.id = 'history-redo';
    redoButton.disabled = future.length === 0;
    redoButton.title = '重做刚撤销的那一步（Ctrl+Y / Ctrl+Shift+Z）';
    headActions.append(undoButton, redoButton);
    headActions.append(button('保存改动', async () => {
      data = await request('commit');
      dirty = false;
      resetHistory();
      paint();
      render();
      status('已保存。', 'ok');
    }));
    headActions.append(button('取消预览', async () => {
      data = await request('cancel');
      dirty = false;
      resetHistory();
      paint();
      render();
      status('已回到上次保存的外观。', 'ok');
    }));
    headActions.append(button('恢复当前预设', async () => {
      data = await request('reset', { scope: 'all' });
      paint();
      render();
      status('已把细调值全部清掉，回到预设本来的样子。', 'ok');
    }, 'set-mini danger'));
    head.append(headActions);
    host.append(head);

    // 强调色策略
    const accent = section({ title: '强调色来源', note: '「跟随预设」用当前主题自带的颜色；「自定义」只换强调这一支，其它语义色不跟着跳。' });
    const accentRow = node('div', undefined, 'set-row');
    const accentMain = node('div', undefined, 'set-row-main');
    accentMain.append(node('div', '来源', 'set-label'));
    accentRow.append(accentMain);
    const accentHolder = node('div', undefined, 'set-control');
    const select = node('select');
    [['theme', '跟随预设'], ['system', '跟随系统'], ['custom', '自定义']].forEach(([value, label]) => select.append(new Option(label, value)));
    select.value = data.accentSource;
    const colorInput = document.createElement('input');
    colorInput.type = 'color';
    colorInput.value = data.accentCustom || '#e84865';
    colorInput.hidden = data.accentSource !== 'custom';
    select.addEventListener('change', async () => {
      colorInput.hidden = select.value !== 'custom';
      data = await request('accent', { source: select.value, color: colorInput.value });
      paint(); render();
      status('强调色已预览，保存后保留。', 'ok');
    });
    colorInput.addEventListener('change', async () => {
      data = await request('accent', { source: 'custom', color: colorInput.value });
      paint(); render();
      status('自定义强调色已预览，保存后保留。', 'ok');
    });
    accentHolder.append(select, colorInput);
    accentRow.append(accentHolder);
    accent.append(accentRow);
    host.append(accent);

    // 预设库：按组分组
    const groups = new Map();
    (data.presets || []).forEach(preset => {
      if (!groups.has(preset.group)) groups.set(preset.group, []);
      groups.get(preset.group).push(preset);
    });
    groups.forEach((list, name) => {
      const card = section({ title: name, tag: list.length + ' 套' });
      const grid = node('div', undefined, 'preset-grid');
      list.forEach(preset => grid.append(presetCard(preset)));
      card.append(grid);
      host.append(card);
    });

    // 元素细调
    const editor = section({
      title: '元素细调',
      tag: editing === 'dark' ? '正在改深色' : '正在改浅色',
      note: '改的是当前这套外观的' + (editing === 'dark' ? '深色' : '浅色') + '；另一侧保持预设原值。这些都是预览，点上面的「保存」才生效。'
    });
    const editSwitch = node('div', undefined, 'settings-actions');
    [['light', '改浅色'], ['dark', '改深色']].forEach(([value, label]) => {
      const item = node('button', label, 'mode-option' + (editing === value ? ' selected' : ''));
      item.type = 'button';
      item.addEventListener('click', () => { editing = value; render(); });
      editSwitch.append(item);
    });
    editor.append(editSwitch);
    COLORS.forEach(item => editor.append(colorRow(item)));
    // 底栏那三个参数单独一组：它们是"某个部件的形状"，跟整站的比例尺不是一回事，
    // 混在元素细调里用户找不到「播放状态栏到底在哪调」。
    const isPlayerParam = item => /^player-|^cover-/.test(item.id || '');
    // 排版的两个数值（字号缩放/行高）单独一张卡：它们不是"某个部件的形状"，是全站文字的比例
    const isTypeParam = item => /^(text-scale|line-height)$/.test(item.id || '');
    // 禁用态透明度归「按钮与输入框」那张卡
    const isStateParam = item => /^disabled-opacity$/.test(item.id || '');
    // 导航几何归「半圆导航」那张卡
    const isNavParam = item => /^nav-/.test(item.id || '');
    // 背景的模糊/亮度/遮罩归「背景」那张卡
    const isBgParam = item => /^bg-/.test(item.id || '');
    (data.numbers || []).filter(item => !isPlayerParam(item) && !isTypeParam(item) && !isStateParam(item) && !isNavParam(item) && !isBgParam(item) && !/^material-/.test(item.id)).forEach(item => editor.append(numberRow(item)));
    host.append(editor);

    const materialCard = section({title:'材质与毛玻璃',note:'Windows 使用亚克力背板，Apple 使用银白磨砂与柔和控件。关闭透明效果可获得更清晰的实色界面。'});
    const materialToggle = node('label', undefined, 'set-row');
    materialToggle.append(node('span', '透明与磨砂效果', 'set-label'));
    const materialInput = document.createElement('input'); materialInput.type = 'checkbox';
    materialInput.checked = String(data.light?.values?.['material-transparency'] ?? '1') !== '0';
    materialInput.addEventListener('change', () => pushChange({'light:material-transparency':materialInput.checked?'1':'0','dark:material-transparency':materialInput.checked?'1':'0'}, '透明与磨砂效果'));
    materialToggle.append(materialInput); materialCard.append(materialToggle);
    (data.numbers || []).filter(item=>/^material-/.test(item.id)).forEach(item=>materialCard.append(numberRow(item)));
    host.append(materialCard);
    host.append(backgroundCard());
    navCards().forEach(card => host.append(card));
    stateCards().forEach(card => host.append(card));
    host.append(motionCard());

    // 字体与排版：字体是个人偏好（立刻存），两个数值跟着预设走（预览→保存）
    host.append(typographyCard());

    const player = section({
      title: '底栏（播放状态栏）',
      note: '底栏底色和上下边框永远跟「工作区外壳」同色（改外壳色它就跟着变）。这里调的是它的文字图标色和形状：字多重、多高、多宽松、封面多圆。'
    });
    // 底栏的字/图标单独一支颜色，不和全站正文色绑在一起
    [{ id: 'player-ink', label: '底栏文字与图标' }, { id: 'player-muted', label: '底栏次要文字' }]
      .forEach(item => player.append(colorRow(item)));
    (data.numbers || []).filter(isPlayerParam).forEach(item => player.append(numberRow(item)));
    host.append(player);

    // 按钮显隐/顺序（立即存，不走预览）
    host.append(playerButtonCard());

    // 我的预设
    const mine = section({ title: '我的预设', note: '调好的外观可以存成自己的一套，随时切回来；内置的 24 套不能被覆盖。' });
    const saveRow = node('div', undefined, 'set-row');
    const saveMain = node('div', undefined, 'set-row-main');
    saveMain.append(node('div', '名称', 'set-label'));
    saveRow.append(saveMain);
    const saveHolder = node('div', undefined, 'set-control');
    const nameInput = document.createElement('input');
    nameInput.type = 'text';
    nameInput.placeholder = '例如：深夜听爵士';
    nameInput.maxLength = 40;
    saveHolder.append(nameInput);
    saveHolder.append(button('保存为我的预设', async () => {
      data = await request('savePreset', { name: nameInput.value });
      dirty = false;
      paint(); render();
      status('已存成「' + nameInput.value.trim() + '」，并切到这套。', 'ok');
    }));
    saveRow.append(saveHolder);
    mine.append(saveRow);
    const mineList = (data.presets || []).filter(preset => preset.custom);
    if (mineList.length === 0) mine.append(node('p', '还没有自己的预设。调好一套之后在上面起个名字就行。', 'set-note'));
    mineList.forEach(preset => {
      const row = node('div', undefined, 'set-row');
      const main = node('div', undefined, 'set-row-main');
      main.append(node('div', preset.name, 'set-label'));
      row.append(main);
      const holder = node('div', undefined, 'set-control');
      holder.append(button('删除', async () => {
        data = await request('deletePreset', { presetId: preset.id });
        paint(); render();
        status('已删除「' + preset.name + '」。', 'ok');
      }, 'set-mini danger'));
      row.append(holder);
      mine.append(row);
    });
    host.append(mine);
    host.scrollTop = scrollTop;
  }

  async function mount(host) {
    await reload();
    render(host);
    status('外观已加载，当前是「' + currentPresetName() + '」。改动只会影响界面，不会碰音频和曲库。', 'ok');
  }

  window.CelesteAppearance = { mount, render };
})();
