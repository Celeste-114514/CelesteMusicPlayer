import os, shutil, hashlib, sys

PUB = r'CelesteMusicPlayer/bin/Release/net9.0-windows10.0.19041.0/win-x64/publish'
DIST = r'dist/web-shell-library-preview'

def md5(p):
    h = hashlib.md5()
    with open(p, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()

def collect():
    """只同步需要的文件：顶层 dll/json/config + WebShell 全部。跳过 Assets/EBWebView 等大件。"""
    items = []
    for name in os.listdir(PUB):
        src = os.path.join(PUB, name)
        if os.path.isdir(src):
            if name == 'WebShell':
                for root, _, files in os.walk(src):
                    for f in files:
                        s = os.path.join(root, f)
                        items.append((s, os.path.join(DIST, os.path.relpath(s, PUB))))
            continue
        if name.lower().endswith(('.dll', '.json')) or name in ('CelesteMusicPlayer.exe',):
            items.append((src, os.path.join(DIST, name)))
    return items

def main():
    items = collect()
    todo = []
    for s, d in items:
        if not os.path.exists(d):
            todo.append((s, d, 'NEW'))
        elif md5(s) != md5(d):
            todo.append((s, d, 'DIFF'))
    print('候选 %d 个，实际需要同步 %d 个' % (len(items), len(todo)))
    if not todo:
        print('NO_CHANGE'); return
    done = 0
    for i in range(0, len(todo), 49):
        batch = todo[i:i + 49]
        for s, d, why in batch:
            try:
                if os.path.exists(d):
                    os.remove(d)
            except Exception as e:
                print('  删除失败 %s: %s' % (d, e)); continue
            shutil.copy2(s, d)
            done += 1
        print('  批次 %d-%d 完成' % (i, min(i + 49, len(todo))))
    bad = []
    for s, d, why in todo:
        if not os.path.exists(d):
            bad.append((d, 'MISSING')); continue
        if os.path.getsize(d) == 0:
            bad.append((d, 'ZERO')); continue
        if md5(s) != md5(d):
            bad.append((d, 'MD5'))
    print('REPAIRED=%d/%d  FINAL_BAD=%d' % (done, len(todo), len(bad)))
    for d, k in bad[:10]:
        print('  BAD %s %s' % (k, d))

if __name__ == '__main__':
    main()
