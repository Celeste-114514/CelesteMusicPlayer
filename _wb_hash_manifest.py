"""Write a SHA256 manifest for a delivery folder (program + web assets)."""
import hashlib
import os
import sys
import time

DIRS = ['', 'WebShell']


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def main():
    root = sys.argv[1]
    lines = ['CelesteMusicPlayer 交付哈希清单',
             '生成时间：%s' % time.strftime('%Y-%m-%d %H:%M:%S'),
             '目录：%s' % os.path.basename(root),
             '']
    count = 0
    for sub in DIRS:
        base = os.path.join(root, sub) if sub else root
        if not os.path.isdir(base):
            continue
        names = sorted(os.listdir(base))
        for name in names:
            p = os.path.join(base, name)
            if not os.path.isfile(p):
                continue
            if sub == '' and not name.lower().endswith(('.dll', '.exe', '.json', '.pri')):
                continue
            rel = name if not sub else sub + '/' + name
            lines.append('%s  %10d  %s' % (sha256(p), os.path.getsize(p), rel))
            count += 1
    out = os.path.join(root, '交付哈希清单.txt')
    with open(out, 'w', encoding='utf-8', newline='\r\n') as f:
        f.write('\n'.join(lines) + '\n')
    print('wrote %s  (%d entries)' % (out, count))
    for line in lines[:12]:
        print('  ' + line)


if __name__ == '__main__':
    main()
