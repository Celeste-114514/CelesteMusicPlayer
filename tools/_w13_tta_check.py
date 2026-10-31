"""TTA 时长核对：探针出来的曲库时长 vs ffmpeg 解码出来的真实时长。

为什么要有这一步：`W13TtaLibraryProbe` 里的时长是曲子自己从 TTA 文件头算的，
再拿同一份清单去「核对」等于用同一个公式自证。这里换成 ffmpeg 真正解一遍音频，
用解码器报的时长当基准，跟曲库里的值比。

用法：
    python tools/_w13_tta_check.py [探针输出json]
    （默认读 _w13_tta_library_out.json，素材目录默认 _tagfixtures）
"""
import json
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, '_w13_tta_library_out.json')
SRC = os.path.join(REPO, '_tagfixtures')
FFMPEG = os.path.join(REPO, 'CelesteMusicPlayer', 'Assets', 'ffmpeg', 'ffmpeg.exe')


def decoded_seconds(path):
    """让 ffmpeg 完整解一遍，取它报的最后一行 time=。"""
    p = subprocess.run([FFMPEG, '-hide_banner', '-i', path, '-f', 'null', '-'],
                       capture_output=True, text=True, errors='replace')
    found = re.findall(r'time=(\d+):(\d+):([\d.]+)', p.stderr or '')
    if not found:
        return None
    h, m, s = found[-1]
    return int(h) * 3600 + int(m) * 60 + float(s)


def main():
    data = json.load(open(OUT, encoding='utf-8'))
    rows = {r['file']: r for r in data.get('tta') or []}
    if not rows:
        print('探针输出里没有 tta 行，先跑 tools/W13TtaLibraryProbe.cjs')
        return 2

    bad = 0
    print('%-42s %10s %10s %8s' % ('文件', 'ffmpeg解码', '曲库显示', '差'))
    for name in sorted(rows):
        real = decoded_seconds(os.path.join(SRC, name))
        got = rows[name]['duration']
        if real is None:
            print('%-42s %10s %10.3f %8s' % (name, '读不到', got, '?'))
            bad += 1
            continue
        diff = got - real
        flag = '' if abs(diff) <= 0.05 else '  <== 不一致'
        if flag:
            bad += 1
        print('%-42s %10.3f %10.3f %8.3f%s' % (name, real, got, diff, flag))

    print('\n共 %d 个 TTA，不一致 %d 个' % (len(rows), bad))
    return 0 if bad == 0 else 1


if __name__ == '__main__':
    sys.exit(main())
