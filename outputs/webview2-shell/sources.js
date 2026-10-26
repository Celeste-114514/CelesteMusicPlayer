/* Source management stays in the host. The page never receives saved passwords or stream URLs. */
(() => {
  'use strict';
  const labels = { webdav: 'WebDAV', baidu: '百度网盘', jellyfin: 'Jellyfin', emby: 'Emby', subsonic: 'Subsonic / Navidrome', smb: 'NAS / SMB', sshfs: 'SSHFS（已挂载）' };
  const request = (operation, payload = {}) => window.CelesteTransport.request('sources', { operation, ...payload });
  const el = (tag, text, cls) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; if (cls) node.className = cls; return node; };
  let host, snapshot, selected = '', directory = '', trail = [], timer, epoch = 0;
  const say = text => { const bar = document.getElementById('settings-status'); if (bar) bar.textContent = text; };
  const action = (text, fn) => {
    const button = el('button', text, 'set-mini'); button.type = 'button';
    button.addEventListener('click', async () => { button.disabled = true; try { await fn(); } catch (e) { say(e.message); } finally { button.disabled = false; } }); return button;
  };
  const card = title => { const section = el('section', undefined, 'settings-card'); section.append(el('h2', title)); return section; };
  const statusText = s => s.job.running ? `${s.job.phase} · 已发现 ${s.job.discovered} 项` : s.job.phase === 'failed' ? s.job.error : s.job.phase === 'cancelled' ? '同步已取消，旧索引保留' : `${s.enabled ? '已启用' : '已停用'} · ${s.count} 首已入库`;
  async function reload() { snapshot = await request('list'); render(); }
  function render() {
    if (!host?.isConnected) return;
    host.replaceChildren();
    const list = card('网络音乐来源');
    list.append(el('p', '支持多个连接。默认只浏览；“加入曲库 / 同步”仅索引歌曲信息，播放时才下载原文件。SMB、SSHFS 使用系统已挂载或可直接访问的目录。', 'set-note'));
    list.append(action('添加来源', () => edit()), action('取消当前下载', async () => { await request('cancelDownload'); say('当前下载已取消。'); }), action('清理网络缓存', async () => { const result = await request('clearCache'); say(`已清理 ${result.count} 个缓存文件；收藏和歌单保留，播放中的文件受保护。`); }));
    for (const source of snapshot.sources) {
      const row = el('div', undefined, 'set-row'); const main = el('div', undefined, 'set-row-main');
      main.append(el('strong', source.name + ' · ' + labels[source.provider]));
      const status = el('p', statusText(source), 'set-note'); status.dataset.sourceStatus = source.id; main.append(status); row.append(main);
      const actions = el('div', undefined, 'settings-actions');
      actions.append(action('浏览', async () => { selected = source.id; trail = []; await browse(''); }),
        action('编辑', () => edit(source)), action('测试连接', async () => say((await request('test', { sourceId: source.id })).message)),
        action('加入曲库 / 同步', async () => { await request('sync', { sourceId: source.id }); say('同步已开始，可随时取消。'); }),
        action('取消同步', async () => { await request('cancel', { sourceId: source.id }); }),
        action(source.enabled ? '停用' : '启用', async () => { await request('toggle', { sourceId: source.id }); await reload(); }));
      row.append(actions); list.append(row);
    }
    host.append(list, el('div', undefined, 'source-workspace'));
  }
  function workspace() { return host.querySelector('.source-workspace'); }
  function edit(source) {
    const section = card(source ? '编辑来源：' + source.name : '添加网络音乐来源');
    const fields = {};
    function field(key, label, value = '', type = 'text') {
      const row = el('label', undefined, 'set-row'); row.append(el('span', label, 'set-label'));
      const input = el('input'); input.type = type; input.value = value; input.autocomplete = 'off'; fields[key] = input; row.append(input); section.append(row); return input;
    }
    const provider = el('select'); provider.id = 'source-provider'; provider.setAttribute('aria-label', '网络来源类型');
    Object.entries(labels).forEach(([key, name]) => provider.append(new Option(name, key)));
    provider.value = source?.provider || 'webdav'; provider.disabled = !!source; section.append(provider);
    field('name', '显示名称', source?.name);
    const endpoint = field('endpoint', '服务器地址 / 已挂载路径', source?.endpoint);
    endpoint.placeholder = 'https://server.example.com 或 \\NAS\Music';
    field('root', '子目录 / 媒体库 ID（可留空）', source?.root);
    field('username', '用户名', source?.username);
    const auth = el('select'); auth.append(new Option('用户名与密码', 'password'), new Option('API Key / Token', 'token')); auth.value = source?.auth || 'password'; section.append(auth);
    field('userId', '媒体服务器用户 ID（API Key 模式可选）', source?.userId);
    field('secret', source?.hasSecret ? '密码 / Token（留空保留）' : '密码 / Token', '', 'password');
    const baidu = el('div');
    section.append(baidu);
    function baiduField(key, label, value = '', type = 'text') { const input = field(key, label, value, type); baidu.append(input.closest('label')); }
    baiduField('clientId', '百度应用 App Key', source?.clientId);
    baiduField('clientSecret', '百度应用 Secret Key（留空保留）', '', 'password');
    baiduField('refreshToken', 'Refresh Token（可选，留空保留）', '', 'password');
    baidu.append(el('p', '百度使用你配置的应用身份，通过百度官方页面授权；播放器不索取百度账号密码。也可填入已有 Access Token。SSHFS 不提供 SSH 登录，请先在系统中挂载。', 'set-note'));
    function update() { baidu.hidden = provider.value !== 'baidu'; if (provider.value === 'baidu' && !endpoint.value) endpoint.value = 'https://pan.baidu.com'; }
    provider.addEventListener('change', update); update();
    const buttons = el('div', undefined, 'settings-actions');
    buttons.append(action('保存来源', async () => {
      const payload = Object.fromEntries(Object.entries(fields).map(([key, input]) => [key, input.value]));
      const result = await request('save', { ...payload, sourceId: source?.id || '', provider: provider.value, auth: auth.value, revision: source?.revision });
      selected = result.selectedId; snapshot = result; render(); say('来源已保存。可以测试、浏览或加入曲库。');
    }));
    if (source?.provider === 'baidu') {
      buttons.append(action('打开百度官方授权页', async () => { await request('authorize', { sourceId: source.id }); say('已在系统浏览器打开官方授权页，复制授权码后填入下方。'); }));
      const code = field('authorizationCode', '授权码（仅用于本次换取 Token）');
      buttons.append(action('换取授权 Token', async () => { await request('exchange', { sourceId: source.id, code: code.value }); code.value = ''; await reload(); say('百度授权已加密保存。'); }));
    }
    section.append(buttons); workspace().replaceChildren(section);
  }
  async function browse(path) {
    const currentEpoch = ++epoch; directory = path;
    say('正在读取网络目录…');
    const data = await request('browse', { sourceId: selected, path });
    if (epoch !== currentEpoch || !host?.isConnected) return;
    const source = snapshot.sources.find(s => s.id === selected);
    const section = card((source?.name || '网络来源') + ' / ' + (path || '根目录'));
    const actions = el('div', undefined, 'settings-actions');
    actions.append(action('回根目录', async () => { trail = []; await browse(''); }),
      action('上一级', async () => browse(trail.pop() || '')),
      action('播放当前目录', () => { if (data.tracks.length) window.CelesteShell.send('playAll', { trackIds: data.tracks.map(t => t.id) }); }));
    section.append(actions);
    for (const folder of data.folders) section.append(action(folder.name, async () => { trail.push(directory); await browse(folder.path); }));
    section.append(el('p', `${data.folders.length} 个目录 · ${data.tracks.length} 首歌曲`, 'set-note'));
    let limit = 0;
    const rows = el('div'), more = action('显示更多', fill); section.append(rows, more);
    function fill() {
      for (const track of data.tracks.slice(limit, limit + 100)) {
        const row = el('div', undefined, 'set-row');
        row.append(el('span', `${track.title} · ${track.artist}${track.cue ? ' · CUE 分轨' : ''}${track.cached ? ' · 已缓存' : ''}`, 'set-row-main'));
        row.append(action('播放', () => window.CelesteShell.send('playTrack', { trackId: track.id })),
          action('加入曲库', async () => { await request('pin', { sourceId: selected, trackId: track.id }); say('该曲目已加入曲库，可在歌曲页收藏或添加到混合歌单。'); })); rows.append(row);
      }
      limit += 100; more.hidden = limit >= data.tracks.length;
    }
    fill(); workspace().replaceChildren(section); say('目录已读取；双端数据均保留来源身份。');
  }
  async function poll() {
    if (!host?.isConnected) { clearInterval(timer); return; }
    if (document.hidden) return;
    try {
      snapshot = await request('list');
      for (const source of snapshot.sources) {
        const status = [...host.querySelectorAll('[data-source-status]')].find(node => node.dataset.sourceStatus === source.id);
        if (!status) continue;
        const text = statusText(source);
        // 只在内容真的变了的时候才动一下：轮询每 2 秒一次，不该每次都闪
        if (status.textContent === text) continue;
        status.textContent = text;
        status.classList.remove('just-changed');
        void status.offsetWidth;          // 强制重排，让动画能重播
        status.classList.add('just-changed');
        window.CelesteMotion?.fadeIn(status, 'list');
      }
    } catch { /* 主操作显示错误；轮询不打断编辑 */ }
  }
  async function mount(target) {
    clearInterval(timer); epoch++;
    host = el('div', undefined, 'network-sources'); target.append(host);
    try { await reload(); timer = setInterval(poll, 2000); }
    catch (e) { host.textContent = '读取网络来源失败：' + e.message; }
  }
  document.addEventListener('celeste:message', event => { if (event.detail?.kind === 'celeste.sourceStatus') { say(event.detail.message); window.CelesteShell?.notify?.(event.detail.message); } });
  window.CelesteSources = { mount };
})();
