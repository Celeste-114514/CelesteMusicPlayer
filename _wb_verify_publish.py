"""Verify a self-contained publish directory: web asset parity + no fake/zero files.

The bash sandbox is known to produce "right size, all zeros" files when it overwrites
in place, so a size match is not proof. Everything here compares real content hashes.

usage: python _wb_verify_publish.py [publish_dir]
"""
import hashlib
import os
import sys

BASE = r'C:\Users\MSI\source\repos\CelesteMusicPlayer'
SRC = os.path.join(BASE, 'outputs', 'webview2-shell')
DEFAULT_PUB = os.path.join(BASE, 'CelesteMusicPlayer', 'bin', 'x64', 'Release',
                           'net9.0-windows10.0.19041.0', 'win-x64')


def md5(path):
    h = hashlib.md5()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def sha256(path):
    h = hashlib.sha256()
    with open(path, 'rb') as f:
        for chunk in iter(lambda: f.read(1 << 20), b''):
            h.update(chunk)
    return h.hexdigest()


def is_blank(path):
    """True for 0 bytes, or content that is nothing but NUL (the sandbox failure mode)."""
    size = os.path.getsize(path)
    if size == 0:
        return True, 'size 0'
    with open(path, 'rb') as f:
        head = f.read(4096)
    if head.count(0) == len(head) and size <= len(head):
        return True, 'all NUL (%d bytes)' % size
    return False, ''


def main():
    pub = sys.argv[1] if len(sys.argv) > 1 else DEFAULT_PUB
    shell = os.path.join(pub, 'WebShell')
    problems = []

    print('publish dir: %s' % pub)
    if not os.path.isdir(pub):
        print('MISSING publish dir')
        return 2

    # ---- 1. web asset parity ------------------------------------------------------------
    names = sorted(f for f in os.listdir(SRC) if f.lower().endswith(('.html', '.css', '.js')))
    ok = 0
    for name in names:
        s = os.path.join(SRC, name)
        d = os.path.join(shell, name)
        if not os.path.exists(d):
            problems.append('MISSING  %s' % name)
            continue
        hs, hd = md5(s), md5(d)
        if hs != hd:
            problems.append('DIFF     %s  src=%s pub=%s  sizes %d/%d'
                            % (name, hs[:10], hd[:10], os.path.getsize(s), os.path.getsize(d)))
            continue
        blank, why = is_blank(d)
        if blank:
            problems.append('BLANK    %s  %s' % (name, why))
            continue
        ok += 1
    print('web assets: %d/%d identical' % (ok, len(names)))
    extra = sorted(set(f for f in os.listdir(shell)
                       if f.lower().endswith(('.html', '.css', '.js'))) - set(names))
    if extra:
        print('note: publish has %d asset(s) with no source: %s' % (len(extra), ', '.join(extra)))

    # ---- 2. new features really shipped -------------------------------------------------
    index = os.path.join(shell, 'index.html')
    if os.path.exists(index):
        html = open(index, encoding='utf-8', errors='replace').read()
        for ref in ('dsp-headphones.js', 'dsp-reference.css', 'dsp-eq.js', 'dsp-visuals.js',
                    'tags.js', 'tags-tools.js'):
            state = 'ok' if ref in html else 'NOT REFERENCED'
            print('  index.html -> %-18s %s' % (ref, state))
            if state != 'ok':
                problems.append('index.html does not load %s' % ref)

    dll = os.path.join(pub, 'CelesteMusicPlayer.dll')
    if os.path.exists(dll):
        print('CelesteMusicPlayer.dll  %d bytes  md5=%s  sha256=%s'
              % (os.path.getsize(dll), md5(dll), sha256(dll)[:32] + '...'))
        blob = open(dll, 'rb').read()
        # Member names live in the metadata #Strings heap as UTF-8.
        # Keep this list to entry points that only exist in the current round, so a stale
        # DLL cannot slip through: OPRA panel, tag sort rules, tag tools, tag cover/rename.
        probes = ['WebDspOpraAsync', 'WebTagSortRules', 'WebTagsReadAsync', 'WebTagsWriteAsync',
                  'WebTagToolsAsync', 'WebTagCoverAsync', 'WebTagRenameAsync', 'WebDspModuleInfo',
                  'WebShellWindow']
        for probe in probes:
            hit = probe.encode('utf-8') in blob
            print('  symbol %-20s %s' % (probe, 'FOUND' if hit else 'MISSING'))
            if not hit:
                problems.append('DLL lacks symbol %s' % probe)
    else:
        problems.append('MISSING CelesteMusicPlayer.dll')

    # ---- 3. no fake files anywhere in the publish tree ----------------------------------
    scanned = 0
    ext = ('.dll', '.exe', '.json', '.js', '.css', '.html', '.xbf', '.pri', '.xml', '.ico', '.png')
    for root, _, files in os.walk(pub):
        for name in files:
            if not name.lower().endswith(ext):
                continue
            p = os.path.join(root, name)
            scanned += 1
            blank, why = is_blank(p)
            if blank:
                problems.append('BLANK    %s  %s' % (os.path.relpath(p, pub), why))
    print('files scanned for blank content: %d' % scanned)

    # ---- verdict ------------------------------------------------------------------------
    print('-' * 68)
    if problems:
        print('PROBLEMS %d' % len(problems))
        for p in problems:
            print('  ' + p)
        return 1
    print('VERIFY_OK')
    return 0


if __name__ == '__main__':
    sys.exit(main())
