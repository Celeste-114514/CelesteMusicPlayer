// 启动恢复页：宿主检测到上次未正常退出时，会在网页 ready 之后推送一条 celeste.recovery。
// 这里只负责把那一条画成卡片、接好按钮，不做任何判断（判断全在宿主）。
// 外观与程序一致：recovery.css 用的就是外观设置那套变量（--menu-bg / --line / --accent …），
// 形状照抄程序自带的 .ui-modal，但类名独立 —— 恢复页常驻 DOM，不能被「取当前弹窗」的查询抓到。
(function () {
  // 等 DOM 与依赖（CelesteShell / CelesteTransport）就绪。defer 脚本已保证 DOM 解析完，
  // 但这两个接口分别由 shell.js / transport-panels.js 在它们各自的 IIFE 末尾挂上，
  // 这里用轮询兜底，避免在脚本执行顺序上踩坑。
  function ready(callback) {
    if (window.CelesteShell && window.CelesteTransport && document.getElementById('recovery-overlay')) callback();
    else setTimeout(() => ready(callback), 30);
  }

  ready(() => {
    const overlay = document.getElementById('recovery-overlay');
    const detail = document.getElementById('recovery-detail');
    const facts = document.getElementById('recovery-facts');
    const status = document.getElementById('recovery-status');
    const shell = window.CelesteShell;

    // 安全模式是"这一次"的界面降级：不画背景层、基本不动效。
    // 只挂一个 html 属性，具体压哪些变量交给 recovery.css —— 这样主题推送
    // 后面再写行内变量也盖不掉它。
    function applySafeMode() {
      document.documentElement.dataset.safeMode = 'true';
      // 立即生效：视频背景已经在播的，得让背景层重新判定一次类型才会停。
      try { window.CelesteBackground?.apply?.(); } catch (_) { /* 背景层不在也不影响恢复页 */ }
    }

    function show(data) {
      if (!data || !data.show) return;
      const version = typeof data.appVersion === 'string' ? data.appVersion : '';
      const lastStart = typeof data.lastStartText === 'string' ? data.lastStartText : '';
      const crashes = Number.isFinite(data.crashes) ? data.crashes : 0;
      if (data.safeMode) applySafeMode();

      const bits = [];
      if (lastStart) bits.push('上次启动于 ' + lastStart);
      if (data.settingsRecovered) bits.push('本次启动时设置文件损坏，已用默认值恢复');
      detail.textContent = (bits.length ? bits.join('，') + '。' : '') +
        '程序上次没有正常收尾（可能崩溃、强杀或断电），先确认这次怎么继续。';

      const rows = [];
      if (crashes > 0) rows.push(['未正常退出累计', crashes + ' 次']);
      if (version) rows.push(['当前版本', version]);
      facts.replaceChildren();
      rows.forEach(([label, value]) => {
        const li = document.createElement('li');
        const s = document.createElement('span'); s.textContent = label;
        const strong = document.createElement('strong'); strong.textContent = value;
        li.append(s, strong);
        facts.append(li);
      });

      status.textContent = '';
      status.removeAttribute('data-tone');
      overlay.hidden = false;
      // 键盘用户开箱即用：焦点落在主按钮上，回车就是「正常启动」。
      // 恢复页不响应 Esc —— 必须明确选一个，不然关掉之后用户还是不知道界面怎么了。
      try { overlay.querySelector('[data-recovery="normal"]').focus(); } catch (_) { /* 拿不到焦点不影响使用 */ }
    }

    function dismiss() { overlay.hidden = true; }

    document.querySelectorAll('[data-recovery]').forEach(button => {
      button.addEventListener('click', async () => {
        const action = button.dataset.recovery;
        try {
          if (action === 'export') {
            // 导出需要拿回路径，走带回复的 request；其余动作单向 send 即可。
            const result = await window.CelesteTransport.request('recovery', { action: 'export' });
            const name = result && result.fileName ? result.fileName : '诊断报告';
            status.textContent = '已导出诊断：' + name;
            status.removeAttribute('data-tone');
            shell.notify('诊断已导出：' + name);
            return;
          }
          shell.send('recovery', { action });
          if (action === 'safe') applySafeMode();
          if (action === 'normal' || action === 'safe' || action === 'reload') dismiss();
        } catch (error) {
          status.textContent = (error && error.message) ? error.message : '操作失败，请重试。';
          status.setAttribute('data-tone', 'error');
        }
      });
    });

    // 宿主推送或回复都走 celeste:message，这里只认恢复页那一条。
    document.addEventListener('celeste:message', event => {
      const message = event.detail;
      if (message && message.kind === 'celeste.recovery') show(message.data);
    });
  });
})();
