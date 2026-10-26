"""Copy the verified publish output into a clean delivery folder.

Deliberately excludes the WebView2 user-data cache (78 MB of runtime junk that the app
recreates) so the delivered folder is nothing but program + assets.

Copies into a fresh directory, never over an existing one: in-place overwrites in this
sandbox can silently produce correct-sized all-zero files.

usage: python _wb_make_delivery.py <target_dir>
"""
import os
import shutil
import sys

BASE = r'C:\Users\MSI\source\repos\CelesteMusicPlayer'
PUB = os.path.join(BASE, 'CelesteMusicPlayer', 'bin', 'x64', 'Release',
                   'net9.0-windows10.0.19041.0', 'win-x64')

SKIP_DIRS = {'CelesteMusicPlayer.exe.WebView2', 'WebView2Cache', 'logs'}
SKIP_EXT = set()   # .pdb kept on purpose: crash reports from the user's machine are readable with it

LAUNCHER = '@echo off\r\ncd /d "%~dp0"\r\nstart "" "%~dp0CelesteMusicPlayer.exe" --web-shell\r\n'


def main():
    if len(sys.argv) < 2:
        print('usage: python _wb_make_delivery.py <target_dir>')
        return 2
    dst = sys.argv[1]
    if os.path.abspath(dst) == os.path.abspath(PUB):
        print('refusing to copy onto the publish directory itself')
        return 2
    if os.path.exists(dst):
        print('target already exists, move it away first: %s' % dst)
        return 2

    files = 0
    total = 0
    os.makedirs(dst)
    for root, dirs, names in os.walk(PUB):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        rel = os.path.relpath(root, PUB)
        out = dst if rel == '.' else os.path.join(dst, rel)
        if not os.path.isdir(out):
            os.makedirs(out)
        for name in names:
            if os.path.splitext(name)[1].lower() in SKIP_EXT:
                continue
            shutil.copy2(os.path.join(root, name), os.path.join(out, name))
            files += 1
            total += os.path.getsize(os.path.join(root, name))

    with open(os.path.join(dst, '打开网页直接进播放器.cmd'), 'w', encoding='gbk',
              newline='') as f:
        f.write(LAUNCHER)

    print('copied %d files, %.1f MB -> %s' % (files, total / 1048576.0, dst))
    return 0


if __name__ == '__main__':
    sys.exit(main())
