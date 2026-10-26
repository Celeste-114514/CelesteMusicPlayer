(() => {
  'use strict';
  const shell = window.CelesteShell;
  const live = typeof window.chrome?.webview?.postMessage === 'function';
  const node = (tag, text, cls) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n; };
  const SVGNS = 'http://www.w3.org/2000/svg';
  const icon = href => {
    // SVG, never a text glyph — see outputs/避坑清单-网页图标必须用SVG-2026-10-26.md
    const svg = document.createElementNS(SVGNS, 'svg');
    const use = document.createElementNS(SVGNS, 'use');
    use.setAttribute('href', href);
    svg.append(use);
    svg.setAttribute('aria-hidden', 'true');
    return svg;
  };

  // Column set mirrors the native DataGrid (TagPanelControl.xaml). Kept as a real table on purpose:
  // the user works in this table daily, so sort / multi-select / column width / cell edit all stay.
  //
  // `source` says where the value comes from:
  //   'library' — already in memory, so it can be sorted and filtered by the host
  //   'tag'     — only readable from the file, so it is fetched lazily for visible rows and
  //               sorting those columns only reorders what is currently loaded
  const COLUMNS = [
    { key: 'title', name: '标题', width: 200, source: 'library' },
    { key: 'artist', name: '艺术家', width: 150, source: 'library' },
    { key: 'albumArtist', name: '专辑艺术家', width: 130, source: 'library' },
    { key: 'album', name: '专辑', width: 160, source: 'library' },
    { key: 'year', name: '年份', width: 70, source: 'library', numeric: true },
    { key: 'track', name: '曲目', width: 72, source: 'library', numeric: true },
    { key: 'genre', name: '流派', width: 110, source: 'library' },
    { key: 'duration', name: '时长', width: 80, source: 'library', readonly: true },
    { key: 'file', name: '文件名', width: 190, source: 'library', readonly: true },
    { key: 'format', name: '格式', width: 70, source: 'library', readonly: true },
    { key: 'spec', name: '位深采样率', width: 160, source: 'tag', readonly: true },
    { key: 'bitrate', name: '码率', width: 100, source: 'tag', readonly: true },
    { key: 'fileSize', name: '大小', width: 90, source: 'tag', readonly: true, numeric: true },
    { key: 'composer', name: '作曲家', width: 120, source: 'tag' },
    { key: 'disc', name: '碟片号', width: 85, source: 'library', numeric: true },
    { key: 'trackCount', name: '曲目总数', width: 82, source: 'tag', numeric: true },
    { key: 'bpm', name: 'BPM', width: 92, source: 'tag', numeric: true },
    { key: 'comment', name: '注释', width: 150, source: 'tag' }
  ];
  const hiddenColumns = new Set();
  try { const keys=JSON.parse(localStorage.getItem('celeste.tagHiddenColumns')||'[]');if(Array.isArray(keys))keys.filter(k=>COLUMNS.some(c=>c.key===k)).forEach(k=>hiddenColumns.add(k)); } catch {}
  const displayCell = (row,key) => key==='spec' ? [row.bitDepth,row.sampleRate].filter(Boolean).join(' / ') : row[key]??'';
  try { const widths=JSON.parse(localStorage.getItem('celeste.tagColumns')||'{}');COLUMNS.forEach(c=>{const width=Number(widths[c.key]);if(width>=56&&width<=800)c.width=width;}); } catch { }
  const COLUMN_BY_KEY = new Map(COLUMNS.map(c => [c.key, c]));

  const SUBPAGES = [
    { id: 'quick', name: '快速编辑' },
    { id: 'batch', name: '批量工具' },
    { id: 'numbering', name: '排序编号' },
    { id: 'online', name: '在线标签' },
    { id: 'lyrics', name: '在线歌词' },
    { id: 'lyricsBatch', name: '批量下载歌词' }
  ];

  // Fields the quick-edit form shows, in the native panel's order.
  const FIELDS = [
    ['title', '标题'], ['artist', '艺术家'], ['album', '专辑'], ['albumArtist', '专辑艺术家'],
    ['year', '年份'], ['track', '曲目号'], ['trackCount', '曲目总数'], ['disc', '碟片号'], ['discCount', '碟片总数'], ['genre', '流派'],
    ['bpm', 'BPM'], ['composer', '作曲家'], ['comment', '注释']
  ];

  // ---- state -----------------------------------------------------------------------------
  const PAGE_SIZE = 200;
  const state = {
    rows: [],
    byId: new Map(),
    total: 0,
    offset: 0,
    hasMore: true,
    loading: false,
    query: '',
    sort: '',
    asc: true,
    scopeIds: [],
    sortRules: [],
    generation: 0
  };
  const selected = new Set();       // track ids
  const dirty = new Map();          // id -> { field: value }
  const enriched = new Set();       // ids whose tag fields have been read
  const readCache = new Map();      // id -> full field set (survives re-selecting the same row)
  const rowEls = new Map();         // id -> <tr>
  const cellEls = new Map();        // id -> { field: <div class=cell> }
  const selectionHooks = [];        // subpage counters that follow the selection
  let editor = null;                // { id, cover }
  let currentSub = 'quick';
  let lastClicked = -1;
  let disposeTool = null, multiDraft = {}, selectionKey = '', saving = false;

  const page = node('section', undefined, 'page tags-page'); page.id = 'tags-page'; page.hidden = true;

  const heading = node('div', undefined, 'tags-heading');
  const titleCopy = node('div');
  titleCopy.append(node('p', 'METADATA', 'eyebrow'), node('h1', '标签编辑'));
  const actions = node('div', undefined, 'tags-actions');
  const selectionLabel = node('span', '已选 0 个文件', 'tags-selection');
  const saveBtn = node('button', '保存改动', 'primary');
  const revertBtn = node('button', '还原', 'secondary');
  actions.append(selectionLabel, revertBtn, saveBtn);
  heading.append(titleCopy, actions);
  page.append(heading);

  const layout = node('div', undefined, 'tags-layout');
  const side = node('div', undefined, 'tags-side');
  const subnav = node('nav', undefined, 'tags-subnav');
  const panel = node('div', undefined, 'tags-panel');
  side.append(subnav, panel);

  const tableCol = node('div', undefined, 'tags-tablecol');
  const toolbar = node('div', undefined, 'tags-toolbar');
  const searchWrap = node('label', undefined, 'tags-search');
  const searchIcon = icon('#i-search');
  const searchInput = document.createElement('input');
  searchInput.type = 'search';
  searchInput.placeholder = '搜索标题 / 艺术家 / 专辑 / 文件名';
  searchInput.spellcheck = false;
  searchWrap.append(searchIcon, searchInput);
  const totalLabel = node('span', '正在读取曲库…', 'tags-total');
  toolbar.append(searchWrap, totalLabel);
  const fileActions = node('div',undefined,'tags-file-actions');
  const directoryLabel=node('span','尚未选择工作目录','tags-working-directory');
  [['addFiles','添加文件…'],['addFolder','添加文件夹…'],['changeDirectory','改变工作目录…']].forEach(([operation,label])=>{const button=node('button',label,'secondary');button.addEventListener('click',async()=>{button.disabled=true;try{if(operation==='changeDirectory'&&dirty.size&&!confirm('改变工作目录会放弃未保存的标签改动，是否继续？'))return;const result=await req(operation);if(result.canceled)return;if(operation==='changeDirectory'){state.scopeIds=[];selected.clear();dirty.clear();multiDraft={};}state.scopeIds=[...new Set([...state.scopeIds,...(result.ids||[])])];if(result.directory)directoryLabel.textContent=result.directory;await reload();shell.notify(`已添加 ${result.count} 个文件`);}catch(error){shell.notify(error.message);}finally{button.disabled=false;}});fileActions.append(button);});
  fileActions.append(directoryLabel);
  tableCol.prepend(fileActions);

  const tableWrap = node('div', undefined, 'tag-table-wrap');
  const table = document.createElement('table');
  table.className = 'tag-table';
  tableWrap.append(table);
  const footer = node('div', undefined, 'tags-footer');
  tableCol.append(toolbar, tableWrap, footer);
  layout.append(side, tableCol);
  page.append(layout);
  document.querySelector('.main').append(page);
  function fitWorkspace() {
    if (page.hidden) return;
    const bottom = document.querySelector('.player')?.getBoundingClientRect().top || innerHeight - 140;
    const height = Math.max(260, bottom - layout.getBoundingClientRect().top - 24);
    layout.style.setProperty('--tags-workspace-height', height + 'px');
  }
  window.addEventListener('resize', () => requestAnimationFrame(fitWorkspace));

  // ---- bridge ----------------------------------------------------------------------------
  async function req(operation, payload = {}) {
    if (!live) throw new Error('请在桌面播放器中使用这个功能：浏览器里读不到本机曲库。');
    return window.CelesteTransport.request('tags', { operation, ...payload });
  }

  async function loadRows(reset) {
    // A reset (new search / new sort) must never be dropped just because a page is in flight —
    // the user typed something and expects that to win. Only the append path waits.
    if (!reset && (state.loading || !state.hasMore)) return;
    const generation = ++state.generation;
    state.loading = true;
    if (reset) {
      state.rows = [];
      state.byId.clear();
      state.offset = 0;
      state.hasMore = true;
      enriched.clear();
      rowEls.clear();
      cellEls.clear();
      tableBody.replaceChildren();
      lastClicked = -1;
      renderHead();
    }
    if (!state.scopeIds.length) {
      state.total=0;state.hasMore=false;state.loading=false;renderFooter();return;
    }
    try {
      const result = await req('list', {
        offset: state.offset,
        limit: PAGE_SIZE,
        query: state.query,
        sort: state.sort,
        asc: state.asc,
        sortRules: state.sortRules,
        scopeIds: state.scopeIds
      });
      if (generation !== state.generation) return;
      state.total = Number(result.total) || 0;
      const rows = result.rows || [];
      state.offset += rows.length;
      state.hasMore = rows.length >= PAGE_SIZE && state.offset < state.total;
      rows.forEach(row => {
        const model = { ...row, trackCount: '', discCount: '', bpm: '', composer: '', comment: '', lyrics: '', syncedLyrics: '', ...(dirty.get(row.id) || {}) };
        state.rows.push(model);
        state.byId.set(model.id, model);
      });
      appendRows(rows.map(row => row.id));
      renderFooter();
    } catch (error) {
      shell.notify(error.message || '读取曲库失败。');
      totalLabel.textContent = '读取失败';
    } finally {
      // A newer request owns the loading flag now; clearing it here would let a second page
      // start while that one is still running.
      if (generation === state.generation) state.loading = false;
    }
  }

  // ---- table -----------------------------------------------------------------------------
  const colgroup = document.createElement('colgroup');
  const pickCol = document.createElement('col');
  pickCol.style.width = '38px';
  colgroup.append(pickCol);
  COLUMNS.forEach(c => { const col = document.createElement('col'); col.style.width = c.width + 'px'; colgroup.append(col); });
  table.append(colgroup);

  const thead = document.createElement('thead');
  const tableBody = document.createElement('tbody');
  table.append(thead, tableBody);

  const columnPicker=node('details',undefined,'tags-column-picker'),columnSummary=node('summary','显示列'),columnOptions=node('div',undefined,'tags-column-options');columnPicker.append(columnSummary,columnOptions);fileActions.append(columnPicker);
  COLUMNS.forEach(c=>{const label=node('label'),check=document.createElement('input');check.type='checkbox';check.checked=!hiddenColumns.has(c.key);check.setAttribute('aria-label','显示'+c.name);check.addEventListener('change',()=>{if(check.checked)hiddenColumns.delete(c.key);else hiddenColumns.add(c.key);try{localStorage.setItem('celeste.tagHiddenColumns',JSON.stringify([...hiddenColumns]));}catch{}applyColumnVisibility();});label.append(check,node('span',c.name));columnOptions.append(label);});
  function applyColumnVisibility(){COLUMNS.forEach((c,i)=>{const hidden=hiddenColumns.has(c.key);colgroup.children[i+1].style.width=hidden?'0px':c.width+'px';table.querySelectorAll(`[data-column="${c.key}"]`).forEach(n=>n.hidden=hidden);});}
  applyColumnVisibility();

  // Native TagPanelControl uses a toolbar panel with ordered rules, rather than header clicks.
  const sortPanel=node('dialog',undefined,'tags-sort-dialog'),sortTitle=node('h2','排序'),sortRules=node('div',undefined,'tags-sort-rules'),sortDescription=node('p','按规则从上到下依次排序；清空规则恢复添加顺序。','panel-note');
  const sortButton=node('button','排序…','secondary');fileActions.append(sortButton);page.append(sortPanel);let draftRules=[];
  const sortAction=(text,fn)=>{const b=node('button',text,'secondary');b.type='button';b.addEventListener('click',fn);return b;};
  function drawSortRules(){sortRules.replaceChildren();draftRules.forEach((rule,index)=>{const row=node('div',undefined,'tags-sort-rule'),key=document.createElement('select'),direction=document.createElement('select');key.setAttribute('aria-label',`第 ${index+1} 排序字段`);direction.setAttribute('aria-label',`第 ${index+1} 排序方向`);COLUMNS.forEach(c=>{const o=node('option',c.name);o.value=c.key;key.append(o);});key.value=rule.key;key.addEventListener('change',()=>rule.key=key.value);[['asc','升序'],['desc','降序']].forEach(([value,text])=>{const o=node('option',text);o.value=value;direction.append(o);});direction.value=rule.descending?'desc':'asc';direction.addEventListener('change',()=>rule.descending=direction.value==='desc');const up=sortAction('上移',()=>{[draftRules[index-1],draftRules[index]]=[draftRules[index],draftRules[index-1]];drawSortRules();});up.disabled=index===0;row.append(key,direction,up,sortAction('删除',()=>{draftRules.splice(index,1);drawSortRules();}));sortRules.append(row);});}
  sortButton.addEventListener('click',()=>{draftRules=state.sortRules.map(r=>({...r}));drawSortRules();sortPanel.showModal();});
  sortPanel.append(sortTitle,sortDescription,sortRules,sortAction('添加规则',()=>{const c=COLUMNS.find(c=>!draftRules.some(r=>r.key===c.key));if(c){draftRules.push({key:c.key,descending:false});drawSortRules();}}),sortAction('清空规则',()=>{draftRules=[];drawSortRules();}),sortAction('取消',()=>sortPanel.close()),sortAction('应用排序',async()=>{state.sortRules=draftRules.map(r=>({...r}));sortPanel.close();await loadRows(true);}));

  function renderHead() {
    thead.replaceChildren();
    const headRow = document.createElement('tr');
    const pickTh = document.createElement('th');
    pickTh.className = 'col-pick';
    const pickAll = document.createElement('input');
    pickAll.type = 'checkbox';
    pickAll.title = '全选已加载的行';
    pickAll.checked = state.rows.length > 0 && state.rows.every(row => selected.has(row.id));
    pickAll.addEventListener('change', () => {
      if (pickAll.checked) state.rows.forEach(row => selected.add(row.id));
      else selected.clear();
      afterSelectionChange();
    });
    pickTh.append(pickAll);
    headRow.append(pickTh);

    COLUMNS.forEach(c => {
      const th = document.createElement('th');
      th.dataset.column = c.key;
      th.hidden = hiddenColumns.has(c.key);
      th.style.width = c.width + 'px';
      if (state.sort === c.key) th.setAttribute('aria-sort', state.asc ? 'ascending' : 'descending');
      const inner = node('div', undefined, 'th-inner');
      const arrow = icon(state.sort !== c.key ? '#i-sort-none' : (state.asc ? '#i-sort-asc' : '#i-sort-desc'));
      arrow.classList.add('sort-arrow');
      inner.append(node('span', c.name), arrow);
      inner.title = '使用工具条「排序…」设置多级排序';
      const resizer = node('span', undefined, 'col-resizer');
      resizer.addEventListener('pointerdown', event => {
        event.preventDefault();
        const startX = event.clientX, startWidth = c.width;
        const move = e => { c.width = Math.min(800, Math.max(56, startWidth + (e.clientX - startX))); colgroup.children[COLUMNS.indexOf(c) + 1].style.width = c.width + 'px'; };
        const up = () => { window.removeEventListener('pointermove', move); window.removeEventListener('pointerup', up);try { localStorage.setItem('celeste.tagColumns',JSON.stringify(Object.fromEntries(COLUMNS.map(c=>[c.key,c.width])))); } catch {} };
        window.addEventListener('pointermove', move);
        window.addEventListener('pointerup', up);
      });
      th.append(inner, resizer);
      headRow.append(th);
    });
    thead.append(headRow);
  }

  function compareRows(a, b) {
    const source = COLUMN_BY_KEY.get(state.sort);
    const x = (a[state.sort] ?? '').toString(), y = (b[state.sort] ?? '').toString();
    const numeric = source?.numeric || (/^\d+$/.test(x) && /^\d+$/.test(y) && x !== '' && y !== '');
    const result = numeric ? (Number(x) || 0) - (Number(y) || 0) : x.localeCompare(y, 'zh');
    return state.asc ? result : -result;
  }

  function appendRows(ids) {
    ids.forEach(id => {
      const row = state.byId.get(id);
      if (!row || rowEls.has(id)) return;
      const tr = document.createElement('tr');
      tr.dataset.id = id;
      tr.setAttribute('aria-selected', String(selected.has(id)));

      const tdPick = document.createElement('td');
      tdPick.className = 'col-pick';
      const box = document.createElement('input');
      box.type = 'checkbox';
      box.checked = selected.has(id);
      box.addEventListener('click', event => {
        const index = state.rows.findIndex(item => item.id === id);
        if (event.shiftKey && lastClicked >= 0) {
          const from = Math.min(lastClicked, index), to = Math.max(lastClicked, index);
          for (let i = from; i <= to; i++) selected.add(state.rows[i].id);
        } else if (box.checked) selected.add(id); else selected.delete(id);
        lastClicked = index;
        afterSelectionChange();
      });
      tdPick.append(box);
      tr.append(tdPick);

      const cells = {};
      COLUMNS.forEach(c => {
        const td = document.createElement('td');
        td.dataset.column=c.key;td.hidden=hiddenColumns.has(c.key);
        const cell = node('div', displayCell(row,c.key), 'cell');
        cell.title = displayCell(row,c.key);
        cell.dataset.field=c.key;
        if (c.readonly) {
          cell.classList.add('readonly');
        } else {
          cell.tabIndex = 0;
          cell.addEventListener('click', event => { if(!selected.has(id)||selected.size!==1||event.ctrlKey||event.shiftKey||event.metaKey)return;event.stopPropagation();if(cell.contentEditable==='true')return;cell.contentEditable='true';cell.focus();document.getSelection()?.selectAllChildren(cell); });
          cell.addEventListener('blur', () => {
            if (cell.contentEditable !== 'true') return;
            cell.contentEditable = 'false';
            const previous = (row[c.key] ?? '').toString();
            const next = cell.textContent.trim();
            cell.textContent = next;
            cell.title = next;
            if (next !== previous) {
              row[c.key] = next;
              markDirty(id, c.key, next, previous);
            }
          });
          cell.addEventListener('keydown', event => {
            if (event.key === 'Enter') { event.preventDefault(); cell.blur(); }
            if (event.key === 'Escape') { cell.textContent = row[c.key] ?? ''; cell.blur(); }
          });
        }
        td.append(cell);
        cells[c.key] = cell;
        tr.append(td);
      });
      cellEls.set(id, cells);
      rowEls.set(id, tr);
      tableBody.append(tr);
      observer.observe(tr);
    });
  }

  function syncSelectionUi() {
    rowEls.forEach((tr, id) => tr.setAttribute('aria-selected', String(selected.has(id))));
    rowEls.forEach((tr, id) => {
      const box = tr.querySelector('.col-pick input');
      if (box) box.checked = selected.has(id);
    });
  }

  function afterSelectionChange() {
    const nextKey = [...selected].sort().join('|');
    if (nextKey !== selectionKey) {
      if (Object.keys(multiDraft).length) selectionKey.split('|').filter(Boolean).forEach(id => {
        Object.entries(multiDraft).forEach(([field,value]) => markDirty(id,field,value));
      });
      multiDraft = {}; selectionKey = nextKey;
    }
    syncSelectionUi();
    updateSelection();
    if (currentSub !== 'quick') return;
    if (selected.size !== 1) { editor = null; renderPanel(); return; }
    const id = [...selected][0];
    if (editor?.id === id) { renderPanel(); return; }
    refreshEditor().then(() => { if (currentSub === 'quick') renderPanel(); });
  }

  // ---- lazy tag read --------------------------------------------------------------------
  // Only rows that actually scroll into view ask for their file, so opening the page costs a
  // hundred in-memory rows instead of four thousand disk reads.
  const pending = new Set();
  let flushTimer = 0;
  const observer = new IntersectionObserver(entries => {
    let queued = false;
    entries.forEach(entry => {
      if (!entry.isIntersecting) return;
      const id = entry.target.dataset.id;
      if (!id || enriched.has(id)) return;
      pending.add(id);
      queued = true;
      observer.unobserve(entry.target);
    });
    if (queued) {
      clearTimeout(flushTimer);
      flushTimer = setTimeout(flushPending, 140);
    }
  }, { root: tableWrap, rootMargin: '240px' });

  async function flushPending() {
    const ids = [...pending].slice(0, 40);
    ids.forEach(id => pending.delete(id));
    if (!ids.length) return;
    ids.forEach(id => enriched.add(id));
    try {
      const result = await req('read', { ids });
      (result.tracks || []).forEach(fields => {
        if (!fields.ok) return;
        const row = state.byId.get(fields.id);
        if (!row) return;
        COLUMNS.filter(c=>c.source==='tag').map(c=>c.key).forEach(key => {
          if (dirty.get(fields.id)?.[key] !== undefined) return;
          if(key==='spec'){row.bitDepth=fields.bitDepth;row.sampleRate=fields.sampleRate;}
          row[key] = displayCell(fields,key);
          const cell = cellEls.get(fields.id)?.[key];
          if (cell && !cell.isContentEditable) { cell.textContent = row[key]; cell.title = row[key]; }
        });
      });
    } catch { /* a failed read only leaves the column blank */ }
    if (pending.size) { clearTimeout(flushTimer); flushTimer = setTimeout(flushPending, 60); }
  }

  // ---- editor panel ----------------------------------------------------------------------
  function renderSubnav() {
    subnav.replaceChildren();
    SUBPAGES.forEach(s => {
      const b = node('button', s.name);
      b.setAttribute('aria-current', String(s.id === currentSub));
      b.addEventListener('click', () => { currentSub = s.id; renderSubnav(); renderPanel(); });
      subnav.append(b);
    });
  }

  function renderPanel() {
    disposeTool?.(); disposeTool = null;
    panel.replaceChildren();
    selectionHooks.length = 0;
    const sub = SUBPAGES.find(s => s.id === currentSub);
    panel.append(node('h2', sub.name));

    if (currentSub === 'quick') { renderQuick(); return; }
    if (currentSub === 'batch') { renderBatch(); return; }
    if (currentSub === 'numbering') { renderNumbering(); return; }

    disposeTool = window.CelesteTagTools.mount(panel, currentSub, {
      request: req, targets: () => [...selected], current: () => state.byId.get([...selected][0]),
      writeRows, notify: text => shell.notify(text), reload
    });
  }

  function fieldRow(labelText, input) {
    const l = document.createElement('label');
    l.textContent = labelText;
    if (input.id) l.htmlFor = input.id;
    return [l, input];
  }

  function makeInput(id, placeholder) {
    const input = document.createElement('input');
    input.id = id;
    input.type = 'text';
    input.autocomplete = 'off';
    if (placeholder) input.placeholder = placeholder;
    return input;
  }

  function renderQuick() {
    const singleId = selected.size === 1 ? [...selected][0] : null;
    const single = singleId ? state.byId.get(singleId) : null;
    panel.append(node('p', selected.size === 0
      ? '在右侧表格里选一行，这里会显示它的全部标签。'
      : selected.size > 1
        ? `已选 ${selected.size} 个文件：只写入你修改过的字段；输入后再清空表示清除该字段。`
        : '双击表格里的单元格可以直接改；这里适合一次看全所有字段。', 'panel-note'));

    const cover = node('div', undefined, 'tags-cover');
    const art = node('div', undefined, 'tags-cover-art');
    art.append(icon('#i-note'));
    const coverCopy = node('div');
    const coverData = single && editor?.id === single.id ? editor.cover : '';
    if (coverData) {
      art.replaceChildren();
      const img = document.createElement('img');
      img.src = coverData;
      img.alt = '当前封面';
      art.append(img);
    }
    coverCopy.append(node('p', single ? (coverData ? '内嵌封面' : (editor?.id === single.id ? '这个文件没有内嵌封面' : '读取中…')) : '未选择', 'panel-note'));
    const coverActions = node('div', undefined, 'tags-cover-actions');
    const replaceCover = node('button', '替换封面…', 'secondary');
    const exportCover = node('button', '导出封面…', 'secondary'), removeCover = node('button', '移除封面', 'secondary'), copyCover=node('button','复制封面','secondary'),pasteCover=node('button','粘贴封面','secondary');
    [[replaceCover,'coverReplace'],[exportCover,'coverExport'],[removeCover,'coverRemove'],[copyCover,'coverCopy'],[pasteCover,'coverPaste']].forEach(([button,operation]) => {
      button.disabled = !selected.size;
      button.addEventListener('click', async () => {
        button.disabled = true;
        try { const result = await req(operation, { ids: [...selected] }); if (!result.canceled) { shell.notify('封面操作已完成'); editor = null; readCache.clear(); await refreshEditor(); renderPanel(); } }
        catch (error) { shell.notify(error.message); } finally { button.disabled = false; }
      });
    });
    coverActions.append(replaceCover, exportCover, copyCover, pasteCover, removeCover);
    coverCopy.append(coverActions);
    cover.append(art, coverCopy);
    panel.append(cover);

    const grid = node('div', undefined, 'tag-grid');
    FIELDS.forEach(([key, label]) => {
      const input = makeInput('tag-field-' + key, selected.size > 1 ? '不改就留空' : '');
      input.value = single ? (state.byId.get(single.id)?.[key] ?? '') : (multiDraft[key] ?? '');
      input.dataset.field = key;
      input.addEventListener('input', () => { if (single) markDirty(single.id, key, input.value); else if (selected.size) { multiDraft[key] = input.value; updateSelection(); } });
      grid.append(...fieldRow(label, input));
    });

    const lyricLabel = document.createElement('label');
    lyricLabel.textContent = '歌词（非同步）';
    lyricLabel.htmlFor = 'tag-field-lyrics';
    const lyricArea = document.createElement('textarea');
    lyricArea.id = 'tag-field-lyrics';
    lyricArea.dataset.field = 'lyrics';
    lyricArea.value = single ? (single.lyrics ?? '') : (multiDraft.lyrics ?? '');
    lyricArea.placeholder = selected.size > 1 ? '不改就留空' : '';
    lyricArea.addEventListener('input', () => { if (single) markDirty(single.id, 'lyrics', lyricArea.value); else if (selected.size) { multiDraft.lyrics = lyricArea.value; updateSelection(); } });
    const lyricWrap = node('div', undefined, 'wide');
    lyricWrap.append(lyricLabel, lyricArea);
    grid.append(lyricWrap);

    const syncedLabel = document.createElement('label');
    syncedLabel.textContent = '同步歌词';
    const synced = makeInput('tag-field-synced', '');
    synced.value = single ? (single.syncedLyrics ?? '') : '';
    synced.readOnly = true;
    const syncedWrap = node('div', undefined, 'wide');
    syncedWrap.append(syncedLabel, synced);
    grid.append(syncedWrap);
    panel.append(grid);
  }

  /// Loads the full tag set for one row (cache first) and stores it on the row model.
  async function ensureEditor(id, force) {
    const row = state.byId.get(id);
    if (!row) return;
    const cached = readCache.get(id);
    if (cached && !force) { applyTagFields(id, cached); editor = { id, cover: cached.cover || '' }; return; }

    try {
      const result = await req('read', { ids: [id], cover: true });
      const fields = (result.tracks || [])[0];
      if (!fields?.ok) { shell.notify(fields?.error || '读取标签失败。'); editor = { id, cover: '' }; return; }
      readCache.set(id, fields);
      applyTagFields(id, fields);
    if (selected.size === 1 && selected.has(id)) editor = { id, cover: fields.cover || '' };
    } catch (error) {
      shell.notify(error.message || '读取标签失败。');
      editor = { id, cover: '' };
    }
  }

  function applyTagFields(id, fields) {
    const row = state.byId.get(id);
    if (!row) return;
    ['title', 'artist', 'album', 'albumArtist', 'year', 'track', 'disc', 'genre', 'bpm',
     'composer', 'comment', 'lyrics', 'syncedLyrics', 'trackCount', 'discCount', 'bitDepth', 'sampleRate', 'bitrate', 'fileSize'].forEach(key => {
      if (dirty.get(id)?.[key] !== undefined) return;   // never clobber a pending edit
      row[key] = fields[key] ?? '';
    });
    COLUMNS.map(c=>c.key).forEach(key => {
      const cell = cellEls.get(id)?.[key];
      if (cell && cell.contentEditable!=='true') { cell.textContent = displayCell(row,key); cell.title = displayCell(row,key); }
    });
    enriched.add(id);
  }

  /// Single-row selection drives the edit panel; multi-selection only drives batch tools.
  async function refreshEditor() {
    if (selected.size !== 1) { editor = null; return; }
    const id = [...selected][0];
    if (editor?.id === id) return;
    await ensureEditor(id);
  }

  // ---- batch / numbering -----------------------------------------------------------------
  const TEXT_FIELDS = ['albumArtist', 'genre', 'composer', 'comment', 'year', 'track', 'disc', 'bpm'];

  function renderBatch() {
    panel.append(node('p', '把同一个值写进所有已选文件的同一个字段。留空 = 清空该字段。', 'panel-note'));
    const grid = node('div', undefined, 'tag-grid');
    const select = document.createElement('select');
    select.id = 'batch-field';
    TEXT_FIELDS.forEach(key => {
      const option = document.createElement('option');
      option.value = key;
      option.textContent = (FIELD_LABELS[key] || key) + (key === 'albumArtist' ? '（常用）' : '');
      select.append(option);
    });
    const value = makeInput('batch-value', '要写入的值');
    grid.append(...fieldRow('字段', select), ...fieldRow('值', value));
    panel.append(grid);

    const row = node('div', undefined, 'tags-panel-actions');
    const apply = node('button', '应用到已选（0）', 'primary');
    apply.addEventListener('click', async () => {
      if (!selected.size) { shell.notify('先在表格里选文件。'); return; }
      const key = select.value;
      const fields = { [key]: value.value.trim() };
      await writeGroup([...selected], fields, `已写入 ${FIELD_LABELS[key] || key}`);
    });
    const refresh = () => {
      apply.textContent = `应用到已选（${selected.size}）`;
      apply.disabled = !selected.size;
    };
    selectionHooks.push(refresh);
    refresh();
    row.append(apply);
    panel.append(row);

    panel.append(node('p', '小提示：专辑艺术家是最常批量补的字段。整张专辑选中后填一次，播放列表分组就会归到同一位演出者名下。', 'panel-note'));
    panel.append(node('h3', '从文件名填充 / 替换 / 大小写', 'tags-section-title'));
    const tools = node('div', undefined, 'tag-grid'), mode = document.createElement('select'), field = document.createElement('select');
    [['fromFilename','从文件名填充'],['replace','查找替换'],['upper','大写'],['lower','小写'],['titleCase','词首大写'],['clear','清空字段'],['clearAll','清空全部文字标签（保留封面）']].forEach(([id,text]) => { const o=node('option',text);o.value=id;mode.append(o); });
    Object.entries(FIELD_LABELS).filter(([id]) => !['syncedLyrics','discCount','trackCount'].includes(id)).forEach(([id,text]) => { const o=node('option',text);o.value=id;field.append(o); });
    mode.setAttribute('aria-label','批量工具');field.setAttribute('aria-label','处理字段');
    const find=makeInput('batch-find','查找文字'),replacement=makeInput('batch-replacement','替换为（留空表示删除）'),regex=document.createElement('input'),overwrite=document.createElement('input');regex.type=overwrite.type='checkbox';regex.setAttribute('aria-label','正则表达式');overwrite.setAttribute('aria-label','覆盖已有标题和艺术家');
    tools.append(...fieldRow('工具',mode),...fieldRow('字段',field),...fieldRow('查找',find),...fieldRow('替换为',replacement),...fieldRow('正则表达式',regex),...fieldRow('覆盖已有标签',overwrite));panel.append(tools);
    const preview=node('pre','','tags-transform-preview');let planned=[];
    const commit=node('button','应用预览改动','primary');commit.disabled=true;commit.addEventListener('click',()=>writeRows(planned,'批量处理完成'));
    const makePreview=node('button','预览所选文件','secondary');makePreview.addEventListener('click',async()=>{if(!selected.size)return shell.notify('请先选择文件。');makePreview.disabled=true;try{const result=await req('batchTransform',{ids:[...selected],mode:mode.value,field:field.value,find:find.value,replacement:replacement.value,regex:regex.checked,overwrite:overwrite.checked});planned=result.rows||[];preview.textContent=planned.slice(0,20).map(r=>(state.byId.get(r.id)?.file||r.id)+'\n'+Object.entries(r.fields).map(([k,v])=>`${FIELD_LABELS[k]||k} → ${v}`).join('\n')).join('\n\n')+(planned.length>20?`\n… 共 ${planned.length} 个文件`:'');commit.disabled=!planned.length;}catch(error){shell.notify(error.message);}finally{makePreview.disabled=false;}});
    tools.addEventListener('input',()=>{planned=[];commit.disabled=true;});const toolActions=node('div',undefined,'tags-panel-actions');toolActions.append(makePreview,commit);panel.append(toolActions,preview);
    panel.append(node('h3','按标签重命名文件','tags-section-title'));
    const pattern=makeInput('rename-pattern','%track% - %title%');pattern.value='%track% - %title%';const renamePreview=node('pre','','tags-transform-preview'),renameApply=node('button','应用重命名','primary');renameApply.disabled=true;let renameRows=[];
    const prepareRename=node('button','预览文件名','secondary');prepareRename.addEventListener('click',async()=>{if(!selected.size)return shell.notify('先选择文件。');try{renameRows=[...selected].map(id=>{const row=state.byId.get(id);const file=pattern.value.replace(/%(title|artist|album|track|disc)%/g,(_,key)=>String(row?.[key]??'').replace(/[<>:"/\\|?*]/g,'_')).trim();return{id,file};});renamePreview.textContent=renameRows.slice(0,20).map(r=>(state.byId.get(r.id)?.file||r.id)+' → '+r.file).join('\n');renameApply.disabled=!renameRows.length;}catch(error){shell.notify(error.message);}});
    renameApply.addEventListener('click',async()=>{renameApply.disabled=true;try{const result=await req('rename',{rows:renameRows});const remap=result.remap||{};state.scopeIds=state.scopeIds.map(id=>remap[id]||id);for(const [oldId,newId] of Object.entries(remap)){if(dirty.has(oldId)){dirty.set(newId,dirty.get(oldId));dirty.delete(oldId);}}selected.clear();await reload();document.dispatchEvent(new CustomEvent('celeste:tracksChanged'));shell.notify(`已重命名 ${result.count} 个文件`);}catch(error){shell.notify(error.message);renameApply.disabled=false;}});
    pattern.addEventListener('input',()=>renameApply.disabled=true);const renameActions=node('div',undefined,'tags-panel-actions');renameActions.append(prepareRename,renameApply);panel.append(pattern,node('p','可用：%title% / %artist% / %album% / %track% / %disc%；扩展名会保留。','panel-note'),renameActions,renamePreview);
  }

  const FIELD_LABELS = {
    title: '标题', artist: '艺术家', album: '专辑', albumArtist: '专辑艺术家', year: '年份',
    track: '曲目号', trackCount: '曲目总数', disc: '碟片号', discCount: '碟片总数',
    bpm: 'BPM', genre: '流派', composer: '作曲家', comment: '注释', lyrics: '歌词', syncedLyrics: '同步歌词'
  };

  function renderNumbering() {
    panel.append(node('p', '按当前选择的顺序给已选文件重新编曲目号，常见用法：整张专辑选中后统一编号。', 'panel-note'));
    const grid = node('div', undefined, 'tag-grid');
    const start = makeInput('number-start', '1');
    start.value = '1';
    const step = makeInput('number-step', '1');step.value='1';step.type='number';step.min='1';
    const disc = makeInput('number-disc', '1');
    disc.value = '1';
    const useDisc = document.createElement('input');
    useDisc.type = 'checkbox';
    useDisc.id = 'number-usedisc';
    useDisc.checked = true;
    const useDiscWrap = node('div', undefined, 'tags-check');
    const useDiscLabel = document.createElement('label');
    useDiscLabel.textContent = '同时写入碟片号';
    useDiscLabel.htmlFor = 'number-usedisc';
    useDiscWrap.append(useDisc, useDiscLabel);

    const order = document.createElement('select');
    order.id = 'number-order';
    [['table', '按表格当前顺序'], ['file', '按文件名'], ['title', '按标题']].forEach(([value, text]) => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = text;
      order.append(option);
    });

    grid.append(
      ...fieldRow('起始曲目号', start),
      ...fieldRow('步长', step),
      ...fieldRow('碟片号', disc),
      ...fieldRow('排序依据', order),
      ...fieldRow('', useDiscWrap)
    );
    panel.append(grid);

    const row = node('div', undefined, 'tags-panel-actions');
    const apply = node('button', '开始编号（0）', 'primary');
    const refresh = () => { apply.textContent = `开始编号（${selected.size}）`; apply.disabled = !selected.size; };
    selectionHooks.push(refresh);
    refresh();
    apply.addEventListener('click', async () => {
      const ids = [...selected];
      if (!ids.length) { shell.notify('先在表格里选文件。'); return; }
      const mode = order.value;
      const ordered = state.rows.filter(item => selected.has(item.id));
      if (mode === 'file') ordered.sort((a, b) => (a.file || '').localeCompare(b.file || '', 'zh'));
      else if (mode === 'title') ordered.sort((a, b) => (a.title || '').localeCompare(b.title || '', 'zh'));
      const first = Math.max(1, parseInt(start.value, 10) || 1);
      const discNo = Math.max(1, parseInt(disc.value, 10) || 1);
      const rows = ordered.map((item, index) => {
        const fields = { track: String(first + index * Math.max(1, parseInt(step.value,10)||1)), trackCount: String(ordered.length) };
        if (useDisc.checked) fields.disc = String(discNo);
        return { id: item.id, fields };
      });
      await writeRows(rows, `已给 ${rows.length} 个文件重新编号`);
    });
    row.append(apply);
    panel.append(row);
    panel.append(node('p', '曲目总数会一并写成所选文件的数量，播放器按「碟片号 → 曲目号」排序时顺序才对。', 'panel-note'));
  }

  // ---- writing ---------------------------------------------------------------------------
  // `baseline` remembers the value each field had before the first edit, so typing a value and
  // then typing the original back removes the row from the pending list instead of rewriting the
  // file for nothing.
  const baseline = new Map();

  function markDirty(id, field, value, originalHint) {
    const row = state.byId.get(id);
    if (!row) return;
    let base = baseline.get(id);
    if (!base) { base = {}; baseline.set(id, base); }
    if (!(field in base)) base[field] = originalHint !== undefined ? originalHint : (readCache.get(id)?.[field] ?? row[field] ?? '');
    row[field] = value;

    const fields = dirty.get(id) || {};
    if (String(value) === String(base[field])) delete fields[field];
    else fields[field] = value;
    if (Object.keys(fields).length) dirty.set(id, fields);
    else { dirty.delete(id); baseline.delete(id); }

    const pending = dirty.get(id);
    const cell = cellEls.get(id)?.[field];
    if (cell) cell.classList.toggle('edited', pending ? pending[field] !== undefined : false);
    const tr = rowEls.get(id);
    if (tr) tr.classList.toggle('dirty', dirty.has(id));
    updateSelection();
  }

  async function writeRows(rows, successText) {
    if (!rows.length || saving) return false;
    saving = true;
    saveBtn.disabled = true;
    revertBtn.disabled = true;
    layout.inert = true;
    page.setAttribute('aria-busy', 'true');
    try {
      const result = await req('write', { rows });
      const results = result.results || [];
      const failed = results.filter(item => !item.ok);
      const skipped = results.filter(item => item.ok && item.skipped).length;
      if (failed.length) {
        shell.notify(`${successText}：成功 ${results.length - failed.length}，失败 ${failed.length}（${failed[0].error || '未知原因'}）`);
        return false;
      } else {
        shell.notify(skipped === results.length ? '没有可写的字段。' : `${successText}${skipped ? `，跳过 ${skipped}` : ''}`);
      }
      rows.forEach(row => { dirty.delete(row.id); baseline.delete(row.id); });
      multiDraft = {};
      await reload();
      document.dispatchEvent(new CustomEvent('celeste:tracksChanged'));
      return true;
    } catch (error) {
      shell.notify(error.message || '写入失败。');
      return false;
    } finally {
      saving = false;
      revertBtn.disabled = false;
      layout.inert = false;
      page.removeAttribute('aria-busy');
      updateSelection();
    }
  }

  async function writeGroup(ids, fields, successText) {
    return writeRows(ids.map(id => ({ id, fields })), successText);
  }

  async function saveChanges() {
    const map = new Map([...dirty.entries()].map(([id, fields]) => [id, { ...fields }]));
    if (Object.keys(multiDraft).length) [...selected].forEach(id => map.set(id, { ...(map.get(id) || {}), ...multiDraft }));
    const rows = [...map].map(([id, fields]) => ({ id, fields }));
    if (!rows.length) { shell.notify('没有改动需要保存。'); return; }
    await writeRows(rows, `已保存 ${rows.length} 个文件`);
  }

  async function reload() {
    editor = null;
    readCache.clear();
    baseline.clear();
    renderHead();
    await loadRows(true);
    await refreshEditor();
    renderPanel();
    updateSelection();
  }

  // ---- footer + selection label -----------------------------------------------------------
  function renderFooter() {
    totalLabel.textContent = state.total === 0
      ? (state.query ? `没有匹配「${state.query}」的曲目` : '曲库是空的')
      : `共 ${state.total.toLocaleString('zh-CN')} 首 · 已载入 ${state.rows.length.toLocaleString('zh-CN')}`;
    footer.replaceChildren();
    if (state.hasMore) {
      const more = node('button', '载入更多', 'secondary');
      more.addEventListener('click', () => loadRows(false));
      footer.append(more);
    } else if (state.rows.length) {
      footer.append(node('span', '已到末尾 · 滚动到底会自动继续载入', 'tags-footer-note'));
    }
  }

  function updateSelection() {
    selectionLabel.textContent = `已选 ${selected.size} 个文件` + (dirty.size ? ` · ${dirty.size} 项待保存` : '');
    const count = new Set([...dirty.keys(), ...(Object.keys(multiDraft).length ? selected : [])]).size;
    saveBtn.disabled = saving || count === 0;
    saveBtn.textContent = count ? `保存改动（${count}）` : '保存改动';
    selectionHooks.forEach(hook => hook());
  }

  // ---- wiring ----------------------------------------------------------------------------
  searchInput.addEventListener('input', () => {
    clearTimeout(searchInput._timer);
    searchInput._timer = setTimeout(() => {
      state.query = searchInput.value.trim();
      loadRows(true);
    }, 260);
  });

  tableWrap.addEventListener('scroll', () => {
    if (state.loading || !state.hasMore) return;
    if (tableWrap.scrollTop + tableWrap.clientHeight >= tableWrap.scrollHeight - 400) loadRows(false);
  });

  saveBtn.addEventListener('click', saveChanges);
  revertBtn.addEventListener('click', async () => {
    if (!dirty.size && !Object.keys(multiDraft).length) { shell.notify('没有改动需要还原。'); return; }
    dirty.clear();
    multiDraft = {};
    await reload();
  });

  // Clicking a row (not the checkbox) selects just that row — the native grid behaves the same.
  tableBody.addEventListener('click', event => {
    if (event.target.closest('.col-pick')) return;
    const tr = event.target.closest('tr');
    if (!tr) return;
    const id = tr.dataset.id;
    if (!event.ctrlKey && !event.metaKey) {
      selected.clear();
    }
    selected.add(id);
    lastClicked = state.rows.findIndex(item => item.id === id);
    afterSelectionChange();
  });

  document.addEventListener('celeste:navigate', event => {
    const active = event.detail === 'tags';
    page.hidden = !active;
    if (!active) { disposeTool?.(); disposeTool = null; return; }
    const placeholder = document.getElementById('category-page');
    if (placeholder) placeholder.hidden = true;
    document.querySelector('.main').scrollTop = 0;
    requestAnimationFrame(fitWorkspace);
    shell.setPath([{ label: '标签编辑', action: () => { } }]);
    renderSubnav();
    if (!state.rows.length) {
      renderHead();
      loadRows(true).then(() => { renderPanel(); updateSelection(); });
    } else {
      renderHead();
      renderPanel();
      updateSelection();
    }
  });

  updateSelection();
  window.CelesteTags = Object.freeze({ open: async ids => {
    state.scopeIds = [...new Set(ids.filter(Boolean))]; state.query = ''; searchInput.value = '';
    selected.clear(); state.scopeIds.forEach(id => selected.add(id)); currentSub = 'quick';
    shell.navigate('tags'); await reload(); afterSelectionChange();
  } });
})();
