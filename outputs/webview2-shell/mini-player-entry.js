(() => { document.getElementById('mini-player-open')?.addEventListener('click', () => window.chrome?.webview?.postMessage({ kind: 'celeste.command', version: 1, command: 'miniPlayer' })); })();
