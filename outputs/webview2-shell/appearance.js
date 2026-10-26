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
      tag: '立即生效',
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
        status('导航项目已保存。', 'ok');
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

  // —— 字体与排版 ——
  // 字体是「这台机器装了什么」的问题，选完立刻存，不跟预设走（换配色不该把你挑的字体换掉）；
  // 字号缩放与行高属于当前这套外观，仍走「预览 → 保存」那套。
  const FONT_FIELDS = [
    { key: 'fontUi', label: '西文与界面字体', note: '拉丁字母、数字、控件文字先用它' },
    { key: 'fontCjk', label: '中文字体', note: '上面那套字体里没有汉字时用这套' },
    { key: 'fontMono', label: '等宽字体', note: '播放进度、时长这类要对齐的数字' }
  ];

  function typographyCard() {
    const card = section({
      title: '字体与排版',
      tag: '字体立即生效',
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
          status('字体已保存：' + select.value + '。', 'ok');
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
   * 底栏按钮卡片。改一下立刻存（这是"我要用哪个布局"，不是"外观预览"），
   * 所以刻意不接「保存 / 取消预览」那套；改完只重画这张卡片自己的行，
   * 不整页 render()，否则用户滚到页面底部一动就会跳回顶部。
   */
  function playerButtonCard() {
    const card = section({
      title: '底栏按钮',
      tag: '立即生效',
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
        status('底栏按钮已保存。', 'ok');
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
    // 底栏那三个参数单独一组：它们是"某个部件的形状"，跟整站的比例尺不是一回事，
    // 混在元素细调里用户找不到「播放状态栏到底在哪调」。
    const isPlayerParam = item => /^player-|^cover-/.test(item.id || '');
    // 排版的两个数值（字号缩放/行高）单独一张卡：它们不是"某个部件的形状"，是全站文字的比例
    const isTypeParam = item => /^(text-scale|line-height)$/.test(item.id || '');
    // 禁用态透明度归「按钮与输入框」那张卡
    const isStateParam = item => /^disabled-opacity$/.test(item.id || '');
    // 导航几何归「半圆导航」那张卡
    const isNavParam = item => /^nav-/.test(item.id || '');
    (data.numbers || []).filter(item => !isPlayerParam(item) && !isTypeParam(item) && !isStateParam(item) && !isNavParam(item)).forEach(item => editor.append(numberRow(item)));
    host.append(editor);

    navCards().forEach(card => host.append(card));
    stateCards().forEach(card => host.append(card));

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
  }

  async function mount(host) {
    await reload();
    render(host);
    status('外观已加载，当前是「' + currentPresetName() + '」。改动只会影响界面，不会碰音频和曲库。', 'ok');
  }

  window.CelesteAppearance = { mount, render };
})();
