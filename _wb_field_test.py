"""Field test the delivered build without touching the UI.

Two launches of dist/web-shell-dsp-tags-complete/CelesteMusicPlayer.exe, closed with WM_CLOSE so
the log buffers flush, and only the log lines written during each run are inspected:

  A. `--web-shell`            -> the delivered web shell really boots against the real WebView2
                                 and hands the library over
  B. `--web-shell` + song     -> MainWindow + web shell, plus a real FLAC goes through the audio
                                 chain (QuickPlayLauncher routes argv[1] into the scattered window,
                                 which is why FIR cannot be exercised from here — that is covered
                                 by tools/FirConvolutionProbe)

usage: python _wb_field_test.py [seconds_per_phase]
"""
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
import time

REPO = r'C:\Users\MSI\source\repos\CelesteMusicPlayer'
FOLDER = os.path.join(REPO, 'dist', 'web-shell-dsp-tags-complete')
EXE = os.path.join(FOLDER, 'CelesteMusicPlayer.exe')
LOCAL = os.path.join(os.environ.get('LOCALAPPDATA', r'C:\Users\MSI\AppData\Local'), 'CelesteMusicPlayer')
SONG = r'D:\天翼云盘同步盘\本地音乐\Nujabes\Anti-Lilly & Phoniks - Blue In Green.flac'

SECONDS = int(sys.argv[1]) if len(sys.argv) > 1 else 14

user32 = ctypes.WinDLL('user32', use_last_error=True)
WM_CLOSE = 0x0010
EnumWindowsProc = ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)


def log_files():
    out = []
    for root, _, files in os.walk(LOCAL):
        if os.sep + 'logs' + os.sep not in os.path.join(root, ''):
            continue
        for name in files:
            if name.lower().endswith('.log'):
                out.append(os.path.join(root, name))
    return out


def sizes(files):
    d = {}
    for p in files:
        try:
            d[p] = os.path.getsize(p)
        except OSError:
            d[p] = 0
    return d


def delta(before):
    text = ''
    for p in log_files():
        try:
            size = os.path.getsize(p)
        except OSError:
            continue
        start = before.get(p, 0)
        if size <= start:
            continue
        with open(p, 'rb') as f:
            f.seek(start)
            chunk = f.read().decode('utf-8', 'replace')
        if chunk.strip():
            tag = os.path.basename(os.path.dirname(p))
            text += '\n---- %s ----\n%s' % (tag, chunk)
    return text


def close_gracefully(pid):
    hits = []

    def cb(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            user32.PostMessageW(hwnd, WM_CLOSE, 0, 0)
            hits.append(hwnd)
        return True

    user32.EnumWindows(EnumWindowsProc(cb), 0)
    return len(hits)


def run_phase(name, args, seconds):
    print('\n=== %s ===' % name)
    before = sizes(log_files())
    t0 = time.time()
    proc = subprocess.Popen([EXE] + args, cwd=FOLDER)
    print('  launched pid=%d  args=%s' % (proc.pid, args if args else '(none)'))
    for i in range(seconds):
        time.sleep(1)
        if proc.poll() is not None:
            print('  !! exited early at %ds code=%s' % (i + 1, proc.returncode))
            break
    if proc.poll() is None:
        n = close_gracefully(proc.pid)
        try:
            proc.wait(timeout=20)
            print('  WM_CLOSE -> %d window(s), exit=%s, %.1fs total' % (n, proc.returncode, time.time() - t0))
        except subprocess.TimeoutExpired:
            print('  WM_CLOSE did not finish in 20s, terminating')
            proc.kill()
    news = delta(before)
    lines = [l for l in news.splitlines() if l.strip() and not l.startswith('----')]
    print('  new log lines: %d' % len(lines))
    return news, lines


def show(news, patterns, limit=10):
    got = []
    for line in news.splitlines():
        if any(p in line for p in patterns) and not line.startswith('----'):
            got.append(line.strip())
    for line in got[:limit]:
        print('    ' + line[:225])
    return got


def main():
    if not os.path.exists(EXE):
        print('missing exe: ' + EXE)
        return 2

    a_news, a_lines = run_phase('A 网页外壳单独启动（--web-shell）', ['--web-shell'], SECONDS)
    shell_hits = show(a_news, ['[WebShell]', '[WebDsp]', '[WebTags]'])
    a_errs = [l for l in a_lines if any(k in l for k in (' ERR ', 'Exception', '崩溃', 'Failed'))]

    b_news, b_lines = run_phase('B 网页外壳 + 真实 FLAC 播放', ['--web-shell', SONG], SECONDS) \
        if os.path.exists(SONG) else ('', [])
    chain_hits = show(b_news, ['[链路]', '输出启动', 'celeste_dsp_core', 'HiFi播放', '零散'])
    b_errs = [l for l in b_lines if any(k in l for k in (' ERR ', 'Exception', '崩溃', 'Failed'))]

    print('\n=== 判定 ===')
    # 已知噪音，非产品缺陷：
    #   PrimaryLanguageOverride  —— WinRT 在 unpackaged 启动时必抛一次，历来如此
    #   UpdateChecker 403        —— GitHub API 按 IP 限流，和程序无关
    NOISE = ('PrimaryLanguageOverride', 'UpdateChecker', 'rate limit')

    def noise_free(lines):
        return [l for l in lines if not any(k in l for k in NOISE)]

    checks = [
        ('A 网页外壳真的初始化了', any('[WebShell] initialized' in l for l in a_lines)),
        ('A 曲库交给网页了', any('library sent tracks=' in l for l in a_lines)),
        ('A 没有启动级错误', len(noise_free(a_errs)) == 0),
        ('B 真实音频链路打开并出声', any('输出启动' in l or 'HiFi播放' in l for l in b_lines)),
        ('B 报出了源格式与设备格式', any('[链路] 源=' in l for l in b_lines)),
        ('B 没有播放级错误', len(noise_free(b_errs)) == 0),
    ]
    bad = 0
    for label, ok in checks:
        print('  %s  %s' % ('PASS' if ok else 'FAIL', label))
        if not ok:
            bad += 1
    noise = [l for l in (a_lines + b_lines) if any(k in l for k in NOISE)]
    print('  已知噪音行: %d' % len(noise))
    for l in noise[:4]:
        print('    ~ ' + l[:200])
    other = noise_free(a_errs + b_errs)
    if other:
        print('  其他可疑错误:')
        for l in other[:10]:
            print('    ! ' + l[:210])
    print('\nFIELD_%s' % ('OK' if bad == 0 else 'FAILS=%d' % bad))
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
