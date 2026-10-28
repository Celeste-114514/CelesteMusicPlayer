// 恢复卡：宿主推 celeste.recovery 时画出来。两种情况，一套壳子两种说法：
//   mode = 'crash'   用户正在用的时候网页崩了 —— 宿主已自动重载，这里说明现场并给重载/安全模式；
//   mode = 'unclean' 上次没正常退出 —— 等启动加载走完、进了主界面之后才推，只告知不拦人。
// 判断一律在宿主（WebShellWindow / MainWindow.WebRecovery），这里只负责画。
// 外观与程序一致：用外观设置那套变量（--menu-bg / --line / --accent / --text-scale …），见 recovery.css。
(function () {
  // 等 DOM 与依赖（CelesteShell / CelesteTransport）就绪。defer 脚本已保证 DOM 解析完，
  // 但这两个接口分别由 shell.js / transport-panels.js 在各自 IIFE 末尾挂上，
  // 这里用轮询兜底，避免在脚本执行顺序上踩坑。
  function ready(callback) {
    if (window.CelesteShell && window.CelesteTransport && document.getElementById('recovery-overlay')) callback();
    else setTimeout(() => ready(callback), 30);
  }

  // 两种模式共用一套动作实现，差别只在标签与主次。
  const ACTIONS = {
    dismiss: { label: '知道了' },
    reload: { label: '重新载入界面' },
    safe: { label: '安全模式再试' }
  };

  ready(() => {
    const overlay = document.getElementById('recovery-overlay');
    const glyphUse = document.getElementById('recovery-glyph-use');
    const title = document.getElementById('recovery-title');
    const detail = document.getElementById('recovery-detail');
    const facts = document.getElementById('recovery-facts');
    const hint = document.getElementById('recovery-hint');
    const status = document.getElementById('recovery-status');
    const links = document.getElementById('recovery-links');
    const actions = document.getElementById('recovery-actions');
    const shell = window.CelesteShell;
    // 当前卡片是哪一种。宿主靠它区分"关掉的是一条告知"还是"关掉的是一次崩溃救援"。
    let currentMode = '';

    // 安全模式是"这一次"的界面降级：不画背景层、基本不动效。
    // 只挂一个 html 属性，具体压哪些变量交给 recovery.css —— 这样主题推送
    // 后面再写行内变量也盖不掉它。
    function applySafeMode() {
      document.documentElement.dataset.safeMode = 'true';
      // 立即生效：视频背景已经在播的，得让背景层重新判定一次类型才会停
      // （--bg-mode 被压成 solid，apply() 会据此把 video 停掉）。
      try { window.CelesteBackground?.apply?.(); } catch (_) { /* 背景层不在也不影响恢复卡 */ }
    }

    function dismiss() { overlay.hidden = true; }

    async function runAction(action) {
      try {
        if (action === 'export') {
          // 导出要拿回文件名，走带回复的 request；其余动作单向 send 即可。
          const result = await window.CelesteTransport.request('recovery', { action: 'export' });
          const name = result && result.fileName ? result.fileName : '诊断报告';
          status.textContent = '已导出诊断：' + name;
          status.removeAttribute('data-tone');
          shell.notify('诊断已导出：' + name);
          return;
        }
        shell.send('recovery', { action, mode: currentMode });
        if (action === 'safe') applySafeMode();
        if (action === 'dismiss' || action === 'reload' || action === 'safe') dismiss();
      } catch (error) {
        status.textContent = (error && error.message) ? error.message : '操作失败，请重试。';
        status.setAttribute('data-tone', 'error');
      }
    }

    function buildActions(list) {
      actions.replaceChildren();
      list.forEach(item => {
        const spec = ACTIONS[item.action];
        if (!spec) return;
        const button = document.createElement('button');
        button.className = item.cls || 'secondary';
        button.dataset.recovery = item.action;
        button.textContent = item.label || spec.label;
        button.addEventListener('click', () => runAction(item.action));
        actions.append(button);
      });
    }

    function buildLinks() {
      links.replaceChildren();
      [['export', '导出诊断'], ['openFolder', '打开日志文件夹']].forEach(([action, label]) => {
        const button = document.createElement('button');
        button.className = 'text-button';
        button.dataset.recovery = action;
        button.textContent = label;
        button.addEventListener('click', () => runAction(action));
        links.append(button);
      });
    }

    function fillFacts(rows) {
      facts.replaceChildren();
      rows.forEach(([label, value]) => {
        const li = document.createElement('li');
        const span = document.createElement('span'); span.textContent = label;
        const strong = document.createElement('strong'); strong.textContent = value;
        li.append(span, strong);
        facts.append(li);
      });
    }

    function showCrash(data) {
      const version = typeof data.appVersion === 'string' ? data.appVersion : '';
      const count = Number.isFinite(data.sessionCrashes) ? data.sessionCrashes : 1;
      glyphUse.setAttribute('href', '#i-refresh');
      title.textContent = '界面刚刚崩溃了';
      detail.textContent = '这次浏览时网页进程崩了（可能是内存、显卡驱动或网页本身出错）。' +
        '程序已经替你重新载入过一次，正在放的音乐不会因此停下。' +
        (count > 1 ? `这是本次运行的第 ${count} 次崩溃，建议点「安全模式再试」把背景视频和动效关掉看看。` : '');
      fillFacts([
        ...(count > 1 ? [['本次运行崩溃', count + ' 次']] : []),
        ...(version ? [['当前版本', version]] : [])
      ]);
      hint.textContent = '重载只是把网页重新载入，不动播放队列，也不会改你的设置。';
      buildLinks();
      // 崩过之后用户最想要的是"让界面恢复正常"，所以重载放主位。
      buildActions([{ action: 'reload', cls: 'primary' }, { action: 'safe' }, { action: 'dismiss', label: '继续用这个界面' }]);
    }

    function showUnclean(data) {
      const version = typeof data.appVersion === 'string' ? data.appVersion : '';
      const lastStart = typeof data.lastStartText === 'string' ? data.lastStartText : '';
      const crashes = Number.isFinite(data.crashes) ? data.crashes : 0;
      glyphUse.setAttribute('href', '#i-shield');
      title.textContent = '上次没有正常退出';
      const bits = [];
      if (lastStart) bits.push('上次启动于 ' + lastStart);
      if (data.settingsRecovered) bits.push('另外，本次启动时设置文件损坏，已用默认值恢复');
      detail.textContent = (bits.length ? bits.join('；') + '。' : '') +
        '程序上次没有正常收尾（可能崩溃、强杀或断电）。这次已经正常进到主界面，不影响使用；如果反复出现，可以导出诊断看看日志。';
      fillFacts([
        ...(crashes > 0 ? [['未正常退出累计', crashes + ' 次']] : []),
        ...(version ? [['当前版本', version]] : [])
      ]);
      hint.textContent = '诊断报告只保存在本机，程序不会自动上传。';
      buildLinks();
      buildActions([{ action: 'dismiss', cls: 'primary' }]);
    }

    function show(data) {
      if (!data || !data.show) return;
      if (data.safeMode) applySafeMode();
      status.textContent = '';
      status.removeAttribute('data-tone');
      currentMode = data.mode === 'crash' ? 'crash' : 'unclean';
      if (currentMode === 'crash') showCrash(data); else showUnclean(data);
      overlay.hidden = false;
      // 键盘用户开箱即用：焦点落在第一颗按钮上，回车就走。
      // 恢复卡不响应 Esc —— 必须明确关掉，不然它一直盖在上面。
      try { actions.querySelector('button')?.focus(); } catch (_) { /* 拿不到焦点不影响使用 */ }
    }

    // 宿主推送走 celeste:message，这里只认恢复卡那一条。
    document.addEventListener('celeste:message', event => {
      const message = event.detail;
      if (message && message.kind === 'celeste.recovery') show(message.data);
    });
  });
})();
