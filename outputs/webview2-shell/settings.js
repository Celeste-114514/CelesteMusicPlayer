/* 设置页。数据全部来自宿主的 appSettings 通道；本文件不保存任何默认表，
   字段叫什么、能取什么值、改了什么时候生效，都由后端 schema 说了算。
   规则：
   - 开关/下拉/滑杆/文本/颜色：改动后立即提交（滑杆和文本框做防抖），并把后端回的值再盖回来。
   - 密码/密钥：不自动提交，必须点保存按钮；空输入保留原值。
   - 失败要说清楚，不说"已保存"。 */
(() => {
  'use strict';
  const $ = id => document.getElementById(id);
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const page = $('settings-page');
  const navTarget = $('settings-nav');
  const scrollTarget = $('settings-scroll');
  const statusBar = $('settings-status');
  const searchInput = $('settings-search-input');
  const node = (tag, text, cls) => {
    const el = document.createElement(tag);
    if (text !== undefined) el.textContent = text;
    if (cls) el.className = cls;
    return el;
  };
  const icon = name => {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    const use = document.createElementNS(svg.namespaceURI, 'use');
    use.setAttribute('href', '#i-' + name);
    svg.append(use);
    return svg;
  };

  let snapshot = null;          // 后端整份快照（含 defs / values / accounts / hotkeys / about）
  let revision = 0;
  let group = 'general';
  let query = '';
  let busy = false;
  const rows = new Map();       // 字段 id -> 行元素
  const defsById = new Map();

  const request = (operation, payload) =>
    window.CelesteTransport.request('appSettings', Object.assign({ operation }, payload));

  function status(text, kind) {
    if (!statusBar) return;
    statusBar.textContent = text || '';
    statusBar.className = 'settings-status' + (kind ? ' ' + kind : '');
  }

  // —— 通用控件 ——
  function section(opts) {
    const card = node('section', undefined, 'settings-card');
    const head = node('div', undefined, 'settings-card-title');
    head.append(node('h2', opts.title));
    if (opts.tag) head.append(node('span', opts.tag, 'tag'));
    if (opts.after) head.append(opts.after);
    card.append(head);
    if (opts.note) card.append(node('p', opts.note, 'settings-card-note'));
    return card;
  }

  function button(label, onClick, cls) {
    const el = node('button', label, cls || 'set-mini');
    el.type = 'button';
    el.addEventListener('click', () => { Promise.resolve(onClick(el)).catch(error => status(error.message, 'err')); });
    return el;
  }

  function debounce(fn, wait) {
    let timer = 0;
    return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), wait); };
  }

  // —— 提交 ——
  // 多个改动可能连着来（拖动滑杆、快速点开关），这里串行排队，避免后一次覆盖前一次。
  let queue = Promise.resolve();
  function patch(changes) {
    const task = queue.then(() => submit(changes), () => submit(changes));
    queue = task.catch(() => {});
    return task;
  }

  async function submit(changes) {
    busy = true;
    try {
      const result = await request('patch', { revision, changes });
      if (result.conflict) {
        await reload();
        status(result.message, 'warn');
        return false;
      }
      revision = result.revision;
      applyValues(result.values || {});
      status(result.note || '已保存', 'ok');
      return true;
    } catch (error) {
      status('保存失败：' + error.message, 'err');
      throw error;
    } finally { busy = false; }
  }

  const commitDebounced = debounce((id, value) => {
    patch({ [id]: value }).catch(() => {});
  }, 320);

  function applyValues(values) {
    Object.entries(values).forEach(([id, value]) => {
      if (snapshot && snapshot.values) snapshot.values[id] = value;
      const row = rows.get(id);
      if (row) paintRow(row, id, value);
    });
  }

  // —— 行 ——
  // 标记为高级的项默认折叠进「高级」区块：它们真实可用，只是不该占着首屏。
  function addFields(card, ids, options) {
    const fold = !options || options.foldAdvanced !== false;
    const advanced = [];
    const place = def => {
      const row = buildRow(def);
      card.append(row);
      rows.set(def.id, row);
    };
    ids.forEach(id => {
      const def = defsById.get(id);
      if (!def) return;
      if (fold && def.advanced) advanced.push(def);
      else place(def);
    });
    if (advanced.length) {
      const details = node('details', undefined, 'settings-details');
      details.append(node('summary', `高级（${advanced.length} 项）`));
      card.append(details);
      advanced.forEach(def => {
        const row = buildRow(def);
        details.append(row);
        rows.set(def.id, row);
      });
    }
  }

  function buildRow(def) {
    const row = node('div', undefined, 'set-row');
    row.dataset.field = def.id;
    const main = node('div', undefined, 'set-row-main');
    const label = node('div', undefined, 'set-label');
    label.append(document.createTextNode(def.label));
    if (def.scope === 'native') label.append(node('span', '仅旧界面', 'set-badge native'));
    if (def.scope === 'web') label.append(node('span', '仅网页窗口', 'set-badge web'));
    if (def.readOnly) label.append(node('span', '只读', 'set-badge locked'));
    main.append(label);
    if (def.note) main.append(node('p', def.note, 'set-note'));
    row.append(main);
    row.append(buildControl(def));
    return row;
  }

  function buildControl(def) {
    const holder = node('div', undefined, 'set-control');
    const value = snapshot.values[def.id];
    if (def.readOnly) holder.append(buildReadOnly(def, value));
    else if (def.kind === 'bool') holder.append(buildSwitch(def, value));
    else if (def.kind === 'slider') holder.append(buildSlider(def, value));
    else if (def.kind === 'select') holder.append(buildSelect(def, value));
    else if (def.kind === 'color') holder.append(buildColor(def, value));
    else if (def.kind === 'folder' || def.kind === 'file') holder.append(buildPath(def, value));
    else holder.append(buildText(def, value));
    return holder;
  }

  function paintRow(row, id, value) {
    const def = defsById.get(id);
    if (!def) return;
    writeValue(row.querySelector('.set-control'), def, value);
  }

  // 控件值回填：只改值，不重建 DOM，避免拖动中的焦点被抢走
  function writeValue(holder, def, value) {
    const switchEl = holder.querySelector('.set-switch');
    if (switchEl) { switchEl.setAttribute('aria-checked', String(!!value)); return; }
    const range = holder.querySelector('input[type=range]');
    const number = holder.querySelector('input[type=number]');
    if (range) {
      range.value = Number(value);
      range.style.setProperty('--fill', ((Number(range.value) - Number(def.min)) / Math.max(1, Number(def.max) - Number(def.min)) * 100) + '%');
      const readout = holder.querySelector('.set-readout');
      if (readout) readout.textContent = formatNumber(range.value, def.unit);
      if (number) number.value = Number(range.value);
      return;
    }
    const select = holder.querySelector('select');
    if (select) { select.value = String(value); return; }
    const text = holder.querySelector('input[type=text]');
    if (text) { text.value = String(value ?? ''); return; }
    const color = holder.querySelector('input[type=color]');
    if (color) {
      color.value = String(value);
      const hex = holder.querySelector('.set-color-hex');
      if (hex) hex.textContent = String(value);
    }
  }

  function buildReadOnly(def, value) {
    const select = node('select');
    (def.options || []).forEach(option => {
      const def2 = parseOption(option);
      select.append(new Option(def2.label, def2.value));
    });
    select.value = String(value);
    select.disabled = true;
    return select;
  }

  function parseOption(raw) {
    const index = String(raw).indexOf('|');
    return index < 0 ? { value: String(raw), label: String(raw) } : { value: String(raw).slice(0, index), label: String(raw).slice(index + 1) };
  }

  function buildSwitch(def, value) {
    const sw = node('button', undefined, 'set-switch');
    sw.type = 'button';
    sw.setAttribute('role', 'switch');
    sw.setAttribute('aria-checked', String(!!value));
    sw.setAttribute('aria-label', def.label);
    sw.addEventListener('click', async () => {
      const next = sw.getAttribute('aria-checked') !== 'true';
      sw.setAttribute('aria-checked', String(next));
      sw.setAttribute('aria-busy', 'true');
      try { await patch({ [def.id]: next }); }
      catch { sw.setAttribute('aria-checked', String(!next)); }
      finally { sw.setAttribute('aria-busy', 'false'); }
    });
    return sw;
  }

  function formatNumber(value, unit) {
    const numeric = Number(value);
    const text = Number.isInteger(numeric) ? String(numeric) : numeric.toFixed(2);
    return unit ? text + ' ' + unit : text;
  }

  function buildSlider(def, value) {
    const wrap = node('div', undefined, 'set-slider');
    const range = document.createElement('input');
    range.type = 'range';
    range.min = def.min; range.max = def.max; range.step = def.step || 1;
    range.value = Number(value ?? def.min);
    if (def.unit === '×') range.step = def.step || 0.05;
    const readout = node('span', formatNumber(range.value, def.unit), 'set-readout');
    const box = document.createElement('input');
    box.type = 'number';
    box.min = def.min; box.max = def.max; box.step = range.step;
    box.value = Number(range.value);
    const fill = () => range.style.setProperty('--fill', ((Number(range.value) - Number(def.min)) / Math.max(1, Number(def.max) - Number(def.min)) * 100) + '%');
    fill();
    const sync = next => {
      range.value = next; box.value = next;
      readout.textContent = formatNumber(next, def.unit);
      fill();
    };
    range.addEventListener('input', () => {
      sync(Number(range.value));
      commitDebounced(def.id, Number(range.value));
    });
    box.addEventListener('change', () => {
      const clamped = Math.min(Number(def.max), Math.max(Number(def.min), Number(box.value)));
      sync(clamped);
      patch({ [def.id]: clamped }).catch(() => {});
    });
    wrap.append(range, box, readout);
    return wrap;
  }

  function buildSelect(def, value) {
    const select = node('select');
    select.setAttribute('aria-label', def.label);
    (def.options || []).forEach(raw => {
      const item = parseOption(raw);
      select.append(new Option(item.label, item.value));
    });
    select.value = String(value ?? '');
    select.addEventListener('change', () => {
      patch({ [def.id]: select.value }).catch(() => {});
    });
    return select;
  }

  function buildText(def, value) {
    const input = document.createElement('input');
    input.type = 'text';
    input.value = String(value ?? '');
    input.setAttribute('aria-label', def.label);
    input.addEventListener('input', () => commitDebounced(def.id, input.value));
    input.addEventListener('change', () => patch({ [def.id]: input.value }).catch(() => {}));
    return input;
  }

  function buildColor(def, value) {
    const wrap = node('div', undefined, 'set-slider');
    const picker = document.createElement('input');
    picker.type = 'color';
    picker.value = String(value ?? '#0078D4');
    picker.setAttribute('aria-label', def.label);
    const hex = node('span', picker.value, 'set-color-hex');
    picker.addEventListener('input', () => { hex.textContent = picker.value; });
    picker.addEventListener('change', () => {
      hex.textContent = picker.value;
      patch({ [def.id]: picker.value }).catch(() => {});
    });
    wrap.append(picker, hex);
    return wrap;
  }

  function buildPath(def, value) {
    const wrap = node('div', undefined, 'set-path');
    const input = document.createElement('input');
    input.type = 'text';
    input.readOnly = true;
    input.value = String(value ?? '');
    input.placeholder = '留空表示用默认位置';
    input.setAttribute('aria-label', def.label);
    wrap.append(input);
    wrap.append(button('浏览…', async () => {
      const result = await request(def.kind === 'file' ? 'pickFile' : 'pickFolder');
      if (!result || !result.path) return;
      input.value = result.path;
      await patch({ [def.id]: result.path });
    }));
    if (String(value ?? '').length > 0) {
      wrap.append(button('清除', async () => {
        input.value = '';
        await patch({ [def.id]: '' });
      }, 'set-mini danger'));
    }
    return wrap;
  }

  // —— 凭据（显式保存）——
  function credentialRow(labelText, opts) {
    const wrap = node('div', undefined, 'set-row');
    const main = node('div', undefined, 'set-row-main');
    const label = node('div', undefined, 'set-label');
    label.append(document.createTextNode(labelText));
    main.append(label);
    if (opts.note) main.append(node('p', opts.note, 'set-note'));
    const holder = node('div', undefined, 'set-control');
    const box = node('div', undefined, 'set-secret');
    const input = document.createElement('input');
    input.type = 'password';
    input.placeholder = opts.configured ? '已保存（留空则不改）' : '在这里粘贴';
    input.autocomplete = 'off';
    const state = node('span', opts.configured ? '已保存' : '未填写', 'set-secret-state' + (opts.configured ? ' on' : ''));
    box.append(input, state);
    box.append(button('保存', async () => {
      const fields = { [opts.key]: input.value };
      const result = await request('credentials', { service: opts.service, fields });
      input.value = '';
      state.textContent = result.message || '已保存';
      state.className = 'set-secret-state on';
      status(result.message || '已保存', 'ok');
    }));
    if (opts.clear !== false) {
      box.append(button('清除', async () => {
        const result = await request('credentials', { service: opts.service, fields: { [opts.key]: '__clear__' } });
        input.value = '';
        state.textContent = '已清除';
        state.className = 'set-secret-state';
        status(result.message || '已清除', 'ok');
      }, 'set-mini danger'));
    }
    holder.append(box);
    wrap.append(main, holder);
    return wrap;
  }

  // —— 各分组内容 ——
  const HOTKEY_LABELS = {
    PlayPause: '播放 / 暂停', Stop: '停止', Next: '下一首', Previous: '上一首',
    VolumeUp: '音量 +', VolumeDown: '音量 -', SeekForward: '快进 5 秒', SeekBack: '快退 5 秒',
    ToggleDesktopLyrics: '显示 / 隐藏桌面歌词', ToggleFavorite: '喜欢当前曲目', ShowHideMain: '显示 / 隐藏主窗口'
  };

  function renderGeneral(host) {
    const start = section({ title: '启动' });
    addFields(start, ['AutoRun', 'StartupCategory']);
    host.append(start);

    const session = section({ title: '启动后恢复什么', note: '这三项是各自独立的事：恢复曲库、恢复到上次那首歌、以及在恢复出来之后要不要自动起播。' });
    addFields(session, ['RestoreLibrary', 'RestorePlayback', 'AutoPlayWhenStart']);
    host.append(session);

    const exit = section({ title: '关闭行为' });
    addFields(exit, ['CloseAction', 'AlwaysOnTop']);
    host.append(exit);
  }

  function renderAudio(host) {
    const output = section({
      title: '输出',
      note: '设备切换要走底栏的「音频设置」面板 —— 那里会做设备存活校验和忙碌保护。为了避免两处同时写同一件事，这个下拉在这里只读。'
    });
    const open = button('打开音频设置面板', () => {
      window.CelesteTransport.open('audio', $('audio-settings'));
    }, 'set-mini');
    output.querySelector('.settings-card-title').append(open);
    addFields(output, ['OutputMode']);
    output.append(node('p', 'SRC 升频、EQ、ReplayGain、卷积这些具体处理都在「音效处理」页；「按设备记忆 DSP」在音频设置面板里，这里不重复一个开关。', 'set-note'));
    host.append(output);

    const volume = section({ title: '软件音量与缓冲' });
    addFields(volume, ['HiFiSoftwareVolume', 'OutputBufferMs']);
    host.append(volume);

    const dsd = section({ title: 'DSD 高级兼容' });
    addFields(dsd, ['DsdOutputMode', 'DopContainerMode', 'DsdPreloadEnabled', 'AsioFeederEnabled', 'DsdCachePath', 'TranscodeCacheLimitMb']);
    host.append(dsd);
  }

  function renderPlayback(host) {
    const behaviour = section({ title: '换曲与队列' });
    addFields(behaviour, ['CrossfadeMs', 'StopWhenError', 'ContinueWhenSwitchPlaylist', 'InsertPlaylistAtBegin']);
    host.append(behaviour);

    const speed = section({ title: '速度', note: '和底栏音频设置里的速度是同一个值，改哪边都一样。' });
    addFields(speed, ['PlaybackRate', 'PlaybackRatePreservePitch']);
    host.append(speed);
  }

  function renderAppearance(host) {
    // 网页这一层的外观走「主题通道」：预设库 + 明暗 + 元素细调 + 实时预览。
    // 下面这些旧字段只影响旧主窗口，所以单独摆在后面一块，不混进工作区。
    // 工作区自己占一个容器：它内部会整块重画，不能和下面的旧界面卡片共用一个父节点
    const workspace = node('div', undefined, 'appearance-workspace');
    host.append(workspace);
    if (window.CelesteAppearance) {
      window.CelesteAppearance.mount(workspace).catch(error => status('外观工作区加载失败：' + error.message, 'err'));
    }

    const style = section({ title: '旧界面的外观（不影响网页这一层）', note: '这些只作用于旧主窗口和独立原生窗。网页的外观请用上面的预设库与元素细调。' });
    addFields(style, ['UiStyleMode', 'CelesteThemeName', 'EnableFrostedGlass', 'AccentSource', 'CustomAccentColor']);
    host.append(style);

    const background = section({ title: '背景（旧界面）' });
    addFields(background, ['EnableBackground', 'AlbumCoverAsBackground', 'BackgroundGaussBlur', 'GaussBlurRadius', 'BackgroundPreset', 'BackgroundPresetMotion', 'CustomBackgroundPath']);
    host.append(background);

    const list = section({ title: '列表' });
    addFields(list, ['PlaylistDensity', 'ShowPlaylistTitle', 'ShowPlaylistArtist', 'ShowPlaylistAlbum', 'ShowPlaylistYear', 'ShowPlaylistDuration']);
    host.append(list);

    const nav = section({ title: '导航' });
    addFields(nav, ['ShowNavFavorites', 'ShowNavRecent', 'ShowNavGenre', 'ShowNavYear', 'NavEmojiIcons']);
    host.append(nav);

    const pages = section({ title: '播放页与独立窗口' });
    addFields(pages, ['NowPlayingLayout', 'NowPlayingVisual', 'MiniPlayerBackground', 'MiniPlayerAlwaysOnTop', 'OpenMiniPlayerOnStartup', 'ScatteredPlayBackground']);
    host.append(pages);
  }

  function renderLyrics(host) {
    const source = section({ title: '歌词来源与排版' });
    addFields(source, ['LyricSourcePriority', 'LyricFuzzyMatch', 'HideBlankLyricLines', 'LyricAlign', 'LyricLineSpacing', 'LyricOffsetMs', 'MainLyricFontSize', 'MainLyricFontFamily', 'ShowLyricTranslate', 'LyricSavePolicy']);
    host.append(source);

    const desktop = section({ title: '桌面歌词', note: '桌面歌词是独立的一条原生歌词窗，和网页里的歌词页不是同一套渲染。' });
    addFields(desktop, ['OpenDesktopLyricsOnStartup', 'DesktopLyricHideWithoutLyric', 'DesktopLyricHideWhenPaused', 'DesktopLyricLockOnStart', 'DesktopLyricShowUnlockWhenLocked', 'DesktopLyricHideTranslation', 'DesktopLyricShowBackgroundBar', 'DesktopLyricVisibleLines', 'DesktopLyricLineSpacing', 'DesktopLyricOpacity', 'DesktopLyricFontSize', 'DesktopLyricFontFamily', 'DesktopLyricPlayedColor', 'DesktopLyricUnplayedColor', 'DesktopLyricOutlineColor', 'DesktopLyricOutlineWidth', 'DesktopLyricShadowStrength', 'DesktopLyricAlign', 'DesktopLyricBackgroundColor', 'DesktopLyricClickThrough']);
    host.append(desktop);

    const fetchGradual = section({ title: '自动获取与保存' });
    addFields(fetchGradual, ['AutoDownloadLyrics', 'AutoDownloadCover', 'AutoDownloadOnlyWhenTagFull', 'SaveLyricToSongFolder', 'SaveCoverToSongFolder', 'LyricFolder', 'CoverFolder', 'UseInnerCoverFirst']);
    host.append(fetchGradual);

    const tools = section({ title: '标签工具默认值', note: '这些是「标签」页里下载歌词时的默认选择，不是全局歌词处理规则。' });
    addFields(tools, ['LyricPageSource', 'LyricBatchPageSource', 'LyricPageWriteTarget', 'LyricPageIncludeTranslation', 'WriteId3v23']);
    host.append(tools);
  }

  async function renderLibrary(host) {
    const rules = section({ title: '扫描规则' });
    addFields(rules, ['AutoUpdateLibrary', 'AutoApplyCueOnScan', 'RemoveMissingOnUpdate', 'IgnoreTooShortOnUpdate', 'FileTooShortSec', 'RecentPlayedRangeDays', 'DisableDeleteFromDisk']);
    host.append(rules);

    const folders = section({ title: '监视的目录', note: '这里加的目录会被目录监视服务接管；双击打开的散装文件另外单独记，不会被目录表清空。' });
    const list = node('div', undefined, 'set-list');
    folders.append(list);
    const actions = node('div', undefined, 'settings-actions');
    actions.append(button('添加目录…', async () => {
      await request('folders', { action: 'add' });
      await paintFolders(list);
      status('已添加目录并重启了监视服务。', 'ok');
    }));
    folders.append(actions);
    host.append(folders);
    paintFolders(list);

    const history = section({ title: '历史与记录' });
    const historyActions = node('div', undefined, 'settings-actions');
    historyActions.append(button('清除最近播放', async () => {
      const result = await request('clearRecent');
      status(result.message, 'ok');
    }));
    history.append(historyActions);
    host.append(history);
  }

  async function paintFolders(list) {
    if (!live) return;
    try {
      const result = await request('folders', { action: 'list' });
      list.replaceChildren();
      if (!result.folders.length) list.append(node('p', '还没有添加任何目录，音乐库里只有手动加入的文件。', 'set-note'));
      result.folders.forEach(item => {
        const row = node('div', undefined, 'set-folder' + (item.exists ? '' : ' missing'));
        row.append(node('span', item.path));
        row.append(node('small', item.exists ? (item.tracks >= 0 ? `当前 ${item.tracks} 首` : '已加入监视') : '目录不存在'));
        row.append(button('移除', async () => {
          await request('folders', { action: 'remove', path: item.path });
          await paintFolders(list);
          status('已从监视列表移除。', 'ok');
        }, 'set-mini danger'));
        list.append(row);
      });
      if (result.manualCount > 0) list.append(node('p', `另外还有 ${result.manualCount} 个手动加入的散装文件。`, 'set-note'));
    } catch (error) {
      list.replaceChildren(node('p', '读取失败：' + error.message, 'set-note'));
    }
  }

  function renderNetwork(host) {
    window.CelesteSources.mount(host);

    const lastfm = section({ title: 'Last.fm', note: '元数据查询和收听提交用的是两套凭据：这里的 API key / secret 两边都会写，保持一致。授权会话需要在旧界面的 Last.fm 面板里完成一次。' });
    addFields(lastfm, ['EnableLastFm', 'LastFmNowPlaying', 'LastFmLeastSeconds', 'LastFmLeastPercent']);
    lastfm.append(credentialRow('API key', { service: 'lastfm', key: 'apiKey', configured: !!snapshot.accounts.lastfmApiKey }));
    lastfm.append(credentialRow('Shared secret', { service: 'lastfm', key: 'sharedSecret', configured: !!snapshot.accounts.lastfm.configured }));
    const lastfmState = node('dl', undefined, 'set-kv');
    lastfmState.append(node('dt', '授权会话'), node('dd', snapshot.accounts.lastfm.hasSession ? `已授权（${snapshot.accounts.lastfm.username || '已登录'}）` : '还没有'));
    lastfm.append(lastfmState);
    host.append(lastfm);

    const accounts = section({ title: 'Discogs 与 fanart.tv' });
    addFields(accounts, ['OnlineSearchDefaultSource']);
    accounts.append(credentialRow('Discogs personal token', { service: 'discogs', key: 'token', configured: !!snapshot.accounts.discogsToken, note: '在线封面 / 搜索读这个。' }));
    accounts.append(credentialRow('Discogs consumer key', { service: 'discogs', key: 'consumerKey', configured: !!snapshot.accounts.discogsConsumerKey, note: '标签补全读这一对。' }));
    accounts.append(credentialRow('Discogs consumer secret', { service: 'discogs', key: 'consumerSecret', configured: !!snapshot.accounts.discogsConsumerSecret }));
    accounts.append(credentialRow('fanart.tv API key', { service: 'fanart', key: 'apiKey', configured: !!snapshot.accounts.fanartTvApiKey }));
    host.append(accounts);
  }

  function renderSystem(host) {
    const hotkeys = section({ title: '全局热键', note: snapshot.hotkeys.hint });
    addFields(hotkeys, ['EnableGlobalHotkeys']);
    const table = node('div', undefined, 'set-hotkeys');
    Object.entries(snapshot.hotkeys.defaults).forEach(([action, fallback]) => {
      const row = node('div', undefined, 'set-hotkey');
      row.append(node('b', HOTKEY_LABELS[action] || action));
      const input = document.createElement('input');
      input.type = 'text';
      input.value = snapshot.hotkeys.custom[action] || '';
      input.placeholder = fallback;
      row.append(input);
      row.append(node('small', '留空 = 用默认 ' + fallback));
      row.dataset.action = action;
      table.append(row);
    });
    hotkeys.append(table);
    const hotkeyActions = node('div', undefined, 'settings-actions');
    hotkeyActions.append(button('保存热键', async () => {
      const bindings = {};
      table.querySelectorAll('.set-hotkey').forEach(row => { bindings[row.dataset.action] = row.querySelector('input').value.trim(); });
      await request('hotkeys', { action: 'set', bindings });
      status('已保存热键并重启了注册。能不能真正抢到组合键，要看当时有没有别的软件占着。', 'ok');
    }));
    hotkeyActions.append(button('全部恢复默认', async () => {
      await request('hotkeys', { action: 'reset' });
      await reload();
      status('已恢复默认热键。', 'ok');
    }, 'set-mini danger'));
    hotkeys.append(hotkeyActions);
    host.append(hotkeys);

    const system = section({ title: '系统集成' });
    addFields(system, ['EnableSmtc', 'ShowTaskbarProgress', 'GlobalMouseWheelVolume']);
    host.append(system);

    const shellCard = section({ title: '这个网页窗口', note: '和上面「主窗口置顶」是两回事：那条管旧主窗口，这条管你现在看的这个窗口。' });
    const shellRow = node('div', undefined, 'set-row');
    const shellMain = node('div', undefined, 'set-row-main');
    shellMain.append(node('div', '网页窗口置顶', 'set-label'));
    shellRow.append(shellMain);
    const shellHolder = node('div', undefined, 'set-control');
    const shellSwitch = node('button', undefined, 'set-switch');
    shellSwitch.type = 'button';
    shellSwitch.setAttribute('role', 'switch');
    shellHolder.append(shellSwitch);
    shellRow.append(shellHolder);
    shellCard.append(shellRow);
    host.append(shellCard);
    readShellTop(shellSwitch);
    shellSwitch.addEventListener('click', async () => {
      const next = shellSwitch.getAttribute('aria-checked') !== 'true';
      shellSwitch.setAttribute('aria-checked', String(next));
      try {
        await window.CelesteTransport.request('window', { action: 'top', value: next });
        status(next ? '网页窗口已置顶。' : '已取消置顶。', 'ok');
      } catch (error) {
        shellSwitch.setAttribute('aria-checked', String(!next));
        status('设置失败：' + error.message, 'err');
      }
    });

    const assoc = section({ title: '文件关联', note: '显示的是系统里真实的关联状态，不是我们记住的一个开关。' });
    const assocList = node('dl', undefined, 'set-kv');
    assoc.append(assocList);
    const assocActions = node('div', undefined, 'settings-actions');
    assocActions.append(button('刷新状态', () => paintAssoc(assocList)));
    assocActions.append(button('关联推荐格式', async () => {
      await request('fileAssoc', { action: 'register' });
      await paintAssoc(assocList);
      status('已把推荐格式关联到当前程序。', 'ok');
    }));
    assocActions.append(button('取消全部关联', async () => {
      await request('fileAssoc', { action: 'unregister' });
      await paintAssoc(assocList);
      status('已取消全部文件关联。', 'ok');
    }, 'set-mini danger'));
    assoc.append(assocActions);
    host.append(assoc);
    paintAssoc(assocList);
  }

  async function readShellTop(sw) {
    try {
      const result = await window.CelesteTransport.request('window', { action: 'state' });
      sw.setAttribute('aria-checked', String(!!result.top));
    } catch { sw.setAttribute('aria-checked', 'false'); }
  }

  async function paintAssoc(target) {
    try {
      const result = await request('fileAssoc', { action: 'get' });
      target.replaceChildren();
      target.append(node('dt', '当前程序'), node('dd', result.currentExe || '(未知)'));
      target.append(node('dt', '已关联'), node('dd', result.registered.length ? result.registered.join(' ') : '(没有)'));
      target.append(node('dt', '推荐格式'), node('dd', result.recommended.join(' ')));
    } catch (error) {
      target.replaceChildren(node('dt', '读取失败'), node('dd', error.message));
    }
  }

  function renderStorage(host) {
    const cache = section({ title: '缓存', note: '三种缓存各管各的：ffmpeg 转码、网络音乐下载、DSD 预载。清理互不影响。' });
    const grid = node('div', undefined, 'set-grid2');
    const slots = {};
    ['transcode', 'webdav', 'dsd'].forEach(kind => {
      const box = node('div', undefined, 'set-stat');
      const top = node('div', undefined, 'set-stat-top');
      top.append(node('em', '—'));
      box.append(top, node('small', '正在读取…'));
      grid.append(box);
      slots[kind] = box;
    });
    cache.append(grid);
    const actions = node('div', undefined, 'settings-actions');
    actions.append(button('刷新占用', () => paintCache(slots)));
    actions.append(button('清理转码缓存', async () => {
      const result = await request('cache', { action: 'clear', kind: 'transcode' });
      await paintCache(slots);
      status(result.cleared, 'ok');
    }));
    actions.append(button('清理下载缓存', async () => {
      const result = await request('cache', { action: 'clear', kind: 'webdav' });
      await paintCache(slots);
      status(result.cleared, 'ok');
    }));
    actions.append(button('清理 DSD 预载', async () => {
      const result = await request('cache', { action: 'clear', kind: 'dsd' });
      await paintCache(slots);
      status(result.cleared, 'ok');
    }));
    cache.append(actions);
    host.append(cache);
    paintCache(slots);

    const backup = section({ title: '偏好备份', note: '导出的只是主设置文件，不含主题、DSP 听音方案、设备档案、账号凭据和曲库。' });
    const backupActions = node('div', undefined, 'settings-actions');
    backupActions.append(button('导出应用偏好…', async () => {
      const result = await request('export');
      if (!result.canceled) status(result.message, result.ok ? 'ok' : 'err');
    }));
    backupActions.append(button('导入应用偏好…', async () => {
      const result = await request('import');
      if (result.canceled) return;
      if (result.values) { applyValues(result.values); revision = result.revision; }
      status(result.message, result.ok ? 'ok' : 'err');
    }));
    backup.append(backupActions);
    host.append(backup);

    const reset = section({ title: '恢复默认', note: '按分组恢复，不会动其它分组，也不会删除你的曲库和播放历史。' });
    const pickerRow = node('div', undefined, 'set-row');
    const pickerMain = node('div', undefined, 'set-row-main');
    pickerMain.append(node('div', '要恢复的分组', 'set-label'));
    pickerRow.append(pickerMain);
    const selector = node('select');
    (snapshot.groups || []).forEach(item => selector.append(new Option(item.label, item.id)));
    const pickerHolder = node('div', undefined, 'set-control');
    pickerHolder.append(selector);
    pickerRow.append(pickerHolder);
    reset.append(pickerRow);
    const resetActions = node('div', undefined, 'settings-actions');
    resetActions.append(button('恢复这个分组的默认设置', async () => {
      const result = await request('reset', { group: selector.value });
      applyValues(result.values || {});
      revision = result.revision;
      status(result.message, 'ok');
    }, 'set-mini danger'));
    reset.append(resetActions);
    host.append(reset);
  }

  async function paintCache(slots) {
    if (!live) return;
    const titles = { transcode: '转码缓存', webdav: '网络音乐下载缓存', dsd: 'DSD 预载缓存' };
    try {
      const stats = await request('cache', { action: 'stats' });
      Object.entries(titles).forEach(([kind, title]) => {
        const data = stats[kind] || {};
        const box = slots[kind];
        box.replaceChildren();
        const top = node('div', undefined, 'set-stat-top');
        top.append(node('em', data.size || '0 B'));
        if (data.limitMb) top.append(node('span', `上限 ${data.limitMb} MB`, 'set-inline-note'));
        box.append(top);
        box.append(node('small', `${title} · ${data.count || 0} 个文件\n${data.dir || ''}`));
        box.querySelector('small').style.whiteSpace = 'pre-line';
      });
    } catch (error) {
      Object.values(slots).forEach(box => { box.replaceChildren(node('em', '—'), node('small', '读取失败：' + error.message)); });
    }
  }

  function renderAbout(host) {
    const info = section({ title: '版本信息' });
    const about = snapshot.about || {};
    const kv = node('dl', undefined, 'set-kv');
    kv.append(node('dt', '当前版本'), node('dd', about.version || ''));
    kv.append(node('dt', '安装包类型'), node('dd', about.variant === 'sc' ? '自包含版' : '框架依赖版'));
    kv.append(node('dt', '设置目录'), node('dd', about.configDirectory || ''));
    info.append(kv);
    if (about.recovered) info.append(node('p', '注意：上次启动时检测到设置文件损坏，已用默认设置恢复（原文件有备份）。', 'set-note'));

    const updateBox = node('p', '', 'settings-card-note');
    updateBox.id = 'about-update-note';
    info.append(updateBox);
    const actions = node('div', undefined, 'settings-actions');
    // 下载入口只建一次：反复点「检查更新」不该在按钮后面叠出一排链接。
    const downloadButton = button('打开下载页', async btn => {
      const url = btn.dataset.url;
      if (!url) return;
      btn.disabled = true;
      try {
        await window.CelesteTransport.request('openExternal', { url });
        status('已在浏览器中打开下载页。', 'ok');
      } finally { btn.disabled = false; }
    });
    downloadButton.hidden = true;
    downloadButton.id = 'about-download';
    const checkButton = button('检查更新', async btn => {
      btn.disabled = true;
      updateBox.textContent = '正在检查…';
      updateBox.style.color = '';
      downloadButton.hidden = true;
      try {
        const result = await request('update');
        const line = describeUpdate(result);
        updateBox.textContent = line.text;
        updateBox.style.color = line.color;
        // 只有「确实有新版本、且给了能下手的地址」才亮下载按钮；
        // 清单校验不了的时候优先给发布页，让用户自己核对，不假装可以放心自动更新。
        const target = line.manual ? (result.releaseUrl || result.lastKnownReleaseUrl) : (result.downloadUrl || result.releaseUrl);
        if (line.showDownload && target) {
          downloadButton.dataset.url = target;
          downloadButton.textContent = line.manual ? '打开发布页' : '打开下载页';
          downloadButton.hidden = false;
        }
      } catch (error) {
        updateBox.textContent = '检查失败：' + (error.message || error);
        updateBox.style.color = 'var(--danger)';
      } finally { btn.disabled = false; }
    });
    checkButton.id = 'about-check';
    actions.append(checkButton, downloadButton);
    info.append(actions);
    host.append(info);
  }

  // 把宿主返回的状态翻译成人话。四种状态分开写死，不靠「有没有版本号」猜。
  function describeUpdate(result) {
    const current = result.current || '';
    const checked = result.checkedAt ? `（${result.checkedAt} 检查）` : '';
    switch (result.status) {
      case 'available': {
        const verify = result.checksum === 'verified'
          ? '安装包哈希与发布清单一致。'
          : '这个版本的安装包没有可用的哈希清单，无法校验完整性，建议从发布页手动下载。';
        return {
          text: `有新版本 ${result.latest}（${result.packageKind}），当前 ${current}。${verify}${checked}`,
          color: 'var(--warning)',
          showDownload: true,
          manual: result.checksum !== 'verified'
        };
      }
      case 'unsupported':
        return {
          text: `有新版本 ${result.latest}，但这次发布没有适配当前安装方式（框架依赖/自包含）与架构的安装包，请到发布页手动下载。当前 ${current}${checked}`,
          color: 'var(--warning)',
          showDownload: true,
          manual: true
        };
      case 'failed': {
        const stale = result.stale && result.lastKnownTag
          ? `上次检查发现过 ${result.lastKnownTag}，这次没能确认。`
          : '';
        return {
          text: `检查失败：${result.error || '未知原因'}${stale}（当前 ${current}${checked}）`,
          color: 'var(--danger)',
          showDownload: !!result.lastKnownDownloadUrl,
          manual: true
        };
      }
      default: {
        const pre = result.prereleaseIgnored && result.latestTag
          ? `发现预览版 ${result.latestTag}，稳定通道不自动升级预览版。`
          : '';
        return {
          text: `已经是最新版（${current}）。${pre}${checked}`,
          color: 'var(--success)',
          showDownload: false,
          manual: false
        };
      }
    }
  }

  const renderers = {
    general: renderGeneral,
    audio: renderAudio,
    playback: renderPlayback,
    appearance: renderAppearance,
    lyrics: renderLyrics,
    library: renderLibrary,
    network: renderNetwork,
    system: renderSystem,
    storage: renderStorage,
    about: renderAbout
  };

  // —— 搜索 ——
  function renderSearch(host, text) {
    const needle = text.trim().toLowerCase();
    const hits = [];
    snapshot.defs.forEach(def => {
      if (matches(def, needle)) hits.push(def);
    });
    if (!hits.length) {
      const empty = node('div', undefined, 'settings-empty');
      empty.append(node('p', '没有找到和「' + text + '」相关的设置。'));
      empty.append(node('p', '只搜已经在设置字段表里登记过的项，已经下架的旧开关不会被搜出来。'));
      host.append(empty);
      return;
    }
    // 按分组聚合，方便看出这项属于哪个分类
    const byGroup = new Map();
    hits.forEach(def => {
      if (!byGroup.has(def.group)) byGroup.set(def.group, []);
      byGroup.get(def.group).push(def);
    });
    (snapshot.groups || []).filter(item => byGroup.has(item.id)).forEach(item => {
      const card = section({ title: item.label, tag: byGroup.get(item.id).length + ' 项命中' });
      addFields(card, byGroup.get(item.id).map(def => def.id));
      host.append(card);
    });
  }

  function matches(def, needle) {
    if (!needle) return true;
    const haystack = [def.label, def.id, def.note || '', (def.options || []).join(' ')].join(' ').toLowerCase();
    return haystack.indexOf(needle) >= 0;
  }

  // —— 主体渲染 ——
  function render() {
    if (!snapshot) return;
    rows.clear();
    defsById.clear();
    snapshot.defs.forEach(def => defsById.set(def.id, def));
    scrollTarget.replaceChildren();
    const heading = $('settings-heading');
    const description = $('settings-description');
    if (query.trim()) {
      heading.textContent = '搜索结果';
      description.textContent = '只列出当前能真正改动的项；已经下架的旧开关不在列表里。';
      renderSearch(scrollTarget, query);
      return;
    }
    const meta = (snapshot.groups || []).find(item => item.id === group);
    heading.textContent = meta ? meta.label : '设置';
    description.textContent = meta ? meta.description : '';
    const renderer = renderers[group] || renderGeneral;
    renderer(scrollTarget);
    scrollTarget.scrollTop = 0;
  }

  // 分类导航的共享指示条：只有这一块，跟着选中项走。
  // 之前是每项自己加 .selected 底色 —— 换分类时旧的一下消失、新的一下出现，没有连续感。
  let navIndicator = null;

  function placeNavIndicator(instant) {
    if (!navIndicator) return;
    const active = navTarget.querySelector('.settings-nav-item.selected');
    if (!active || !active.offsetHeight) { navIndicator.dataset.ready = 'false'; return; }
    if (instant) {
      navIndicator.style.transition = 'none';
      requestAnimationFrame(() => { navIndicator.style.transition = ''; });
    }
    navIndicator.dataset.ready = 'true';
    navIndicator.style.setProperty('--ind-y', active.offsetTop + 'px');
    navIndicator.style.setProperty('--ind-h', active.offsetHeight + 'px');
  }

  function selectGroup(id) {
    navTarget.querySelectorAll('.settings-nav-item').forEach(button => {
      const on = button.dataset.group === id;
      button.classList.toggle('selected', on);
      if (on) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    placeNavIndicator();
  }

  function renderNav() {
    navTarget.replaceChildren();
    navTarget.append(node('p', '设置分类', 'settings-nav-title'));
    (snapshot.groups || []).forEach(item => {
      const button = node('button', undefined, 'settings-nav-item' + (item.id === group ? ' selected' : ''));
      button.type = 'button';
      button.dataset.group = item.id;
      if (item.id === group) button.setAttribute('aria-current', 'page');
      button.append(icon(item.icon || 'settings'), node('span', item.label));
      const count = snapshot.defs.filter(def => def.group === item.id).length;
      if (count) button.append(node('span', String(count), 'settings-nav-count'));
      button.addEventListener('click', () => {
        if (group === item.id && !query) return;
        group = item.id;
        query = '';
        if (searchInput) searchInput.value = '';
        // 不重建导航栏：重建会把指示条一起换掉，看着就是"跳"而不是"滑"
        selectGroup(item.id);
        render();
      });
      navTarget.append(button);
    });
    navIndicator = node('span', undefined, 'settings-nav-indicator');
    navIndicator.setAttribute('aria-hidden', 'true');
    navTarget.append(navIndicator);
    placeNavIndicator(true);
  }

  async function reload() {
    snapshot = await request('get');
    revision = snapshot.revision || 0;
    rows.clear();
    renderNav();
    render();
  }

  let loaded = false;
  async function ensureLoaded() {
    if (loaded && snapshot) return;
    loaded = true;
    if (!live) {
      scrollTarget.replaceChildren(node('div', '这个页面需要从桌面宿主读取真实设置（测试预览版没有这条通道）。', 'settings-empty'));
      return;
    }
    status('正在读取设置…', '');
    try {
      await reload();
      status('设置已加载。改动会立刻写入并同步到底层服务。', 'ok');
    } catch (error) {
      loaded = false;
      status('读取设置失败：' + error.message, 'err');
    }
  }

  // 打开页面时才取数据，避免拖慢启动
  document.addEventListener('celeste:navigate', event => {
    if (event.detail !== 'settings') return;
    ensureLoaded();
  });

  if (searchInput) {
    searchInput.addEventListener('input', debounce(() => {
      query = searchInput.value || '';
      if (!snapshot) return;
      render();
    }, 220));
  }

  const refresh = $('settings-refresh');
  if (refresh) refresh.addEventListener('click', async btn => {
    btn.disabled = true;
    try { await reload(); status('已重新读取设置。', 'ok'); }
    catch (error) { status('刷新失败：' + error.message, 'err'); }
    finally { btn.disabled = false; }
  });
})();
