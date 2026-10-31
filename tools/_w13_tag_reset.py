"""标签探针的现场复位：素材、曲库、配置三样都回到同一状态。

用法：python tools/_w13_tag_reset.py
跑完探针之后用同一份备份把 library.db / app-settings.json 还原（探针不改备份）。
"""
import json
import os
import shutil
import sqlite3
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CFG = os.path.join(os.environ['LOCALAPPDATA'], 'CelesteMusicPlayer')
BAK = os.path.join(REPO, '_w13bak', 'tags')
FIX = os.path.join(os.path.expanduser('~'), '_w13tagfix')
PRISTINE = os.path.join(os.path.expanduser('~'), '_w13tagfix_pristine')


def main():
    if not os.path.isdir(PRISTINE):
        sys.exit('缺少 pristine 素材目录: ' + PRISTINE)

    if os.path.isdir(FIX):
        shutil.rmtree(FIX)
    shutil.copytree(PRISTINE, FIX)

    shutil.copy2(os.path.join(BAK, 'library.db.bak'), os.path.join(CFG, 'library.db'))
    for suffix in ('-wal', '-shm'):
        p = os.path.join(CFG, 'library.db' + suffix)
        if os.path.exists(p):
            os.remove(p)

    settings = json.loads(open(os.path.join(BAK, 'app-settings.json.bak'), 'rb').read().decode('utf-8-sig'))
    settings['LibraryWatchFolders'] = [FIX]
    out = json.dumps(settings, ensure_ascii=False, indent=2).replace('\n', '\r\n')
    open(os.path.join(CFG, 'app-settings.json'), 'wb').write(out.encode('utf-8'))

    conn = sqlite3.connect(os.path.join(CFG, 'library.db'))
    total = conn.execute('select count(*) from tracks').fetchone()[0]
    conn.close()
    print('fixtures=%d tracks=%d folder=%s' % (len(os.listdir(FIX)), total, FIX))


if __name__ == '__main__':
    main()
