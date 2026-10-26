/* 外观工作区：预设库 + 明暗 + 元素参数 + 实时预览。
   数据全在宿主的 theme 通道里（预设、当前值、参数边界）；这里只负责画和预览。
   规则：
   - 选预设 / 切明暗 / 改强调色 = 立即保存的偏好（这些是"我现在要用哪套外观"）。
   - 拖颜色、拖滑杆 = 草稿预览，先只改当前页面；点保存才落盘，点取消就回到进页面时的样子。
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
  const request = (operation, payload) =>
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

  let data = null;             // 宿主快照
  let editing = 'light';       // 正在编辑哪一套明暗
  let dirty = false;

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

  // 预览：把当前草稿的两套变量直接刷进根元素（不落盘）
  function paint() {
    if (!data) return;
    const theme = window.CelesteTheme;
    if (theme) {
      theme.preview(data.light.css);
      theme.preview(data.dark.css);
      theme.setMode(data.resolved === 'dark' ? 'dark' : 'light');
    }
  }

  async function reload() {
    data = await request('get');
    dirty = data.draft;
    paint();
  }

  // 草稿一定要交给宿主算：改强调色会连带 on-accent / 选中行这些派生值，
  // 本地自己拼 CSS 会漏掉它们，也会让「保存」时和预览不一致。
  let queue = Promise.resolve();
  function pushChange(changes, note) {
    queue = queue.then(async () => {
      const result = await request('preview', { changes });
      data.light = result.light;
      data.dark = result.dark;
      dirty = true;
      paint();
      status('预览中（还没保存）：' + note, 'warn');
    }).catch(error => status('预览失败：' + error.message, 'err'));
    return queue;
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
      dirty = false;
      paint();
      render();
      status('已套用「' + preset.name + '」。音频、DSP、账号和曲库都没动。', 'ok');
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
    picker.value = (data[editing].values[item.id] || '#888888');
    const hex = node('span', picker.value, 'set-color-hex');
    const push = value => pushChange({ [editing + ':' + item.id]: value }, item.label + ' ' + value);
    picker.addEventListener('input', () => { hex.textContent = picker.value; });
    picker.addEventListener('change', () => { hex.textContent = picker.value; push(picker.value); });
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
    const push = value => {
      const text = value + (item.unit === 'px' ? 'px' : '');
      pushChange({ ['light:' + item.id]: text, ['dark:' + item.id]: text }, item.label + ' ' + text);
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

  // —— 主渲染 ——
  function render(host) {
    host = host || $('settings-scroll');
    if (!host) return;
    host.replaceChildren();

    const head = section({ title: '当前外观', tag: currentPresetName() + (dirty ? ' · 有未保存的改动' : '') });
    head.append(modePicker());
    if (dirty) head.append(node('p', '下面这些改动还只是预览，点「保存」才会写进设置文件。', 'set-note'));
    const headActions = node('div', undefined, 'settings-actions');
    headActions.append(button('保存改动', async () => {
      data = await request('commit');
      dirty = false;
      paint();
      render();
      status('已保存。', 'ok');
    }));
    headActions.append(button('取消预览', async () => {
      data = await request('cancel');
      dirty = false;
      paint();
      render();
      status('已回到上次保存的外观。', 'ok');
    }));
    headActions.append(button('恢复当前预设', async () => {
      data = await request('reset', { scope: 'all' });
      dirty = false;
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
      status('已保存强调色设置。', 'ok');
    });
    colorInput.addEventListener('change', async () => {
      data = await request('accent', { source: 'custom', color: colorInput.value });
      paint(); render();
      status('已保存自定义强调色。', 'ok');
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
    (data.numbers || []).forEach(item => editor.append(numberRow(item)));
    host.append(editor);

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
  }

  async function mount(host) {
    await reload();
    render(host);
    status('外观已加载，当前是「' + currentPresetName() + '」。改动只会影响界面，不会碰音频和曲库。', 'ok');
  }

  window.CelesteAppearance = { mount, render };
})();
