#!/usr/bin/env python3
"""无头 harness 构建器：把 mock 宿主 + 场景脚本注入网页页面，产出 out_<scenario>.html。
   注入 JS 一律从文件原样读入（不用 repr/转义），避免引号转义把 script 打成语法错误。
   用法：build_harness.py [场景[:页面] ...]，页面默认 main（dsp 场景用 dsp:dsp）。"""
import io
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))


def read(rel):
    with io.open(os.path.join(HERE, rel), encoding='utf-8') as f:
        return f.read()


def build(scenario, page='main'):
    html = read(os.path.join('..', 'CelesteMusicPlayer', 'WebUI', page + '.html'))
    mock = read('mock_host.js')
    tail = read('common.js') + '\n' + read('scen_%s.js' % scenario)
    assert html.count('<script>') == 1, '%s.html 应有且仅有一个 <script> 块，当前 %d' % (page, html.count('<script>'))
    assert '</body>' in html, '%s.html 缺少 </body>' % page
    # mock 必须在页面脚本之前（页面脚本执行时就注册监听、上报 ready）
    html = html.replace('<script>', '<script>\n' + mock + '\n</script>\n<script>', 1)
    html = html.replace('</body>', '<script>\n' + tail + '\n</script>\n</body>', 1)
    out = os.path.join(HERE, 'out_%s.html' % scenario)
    with io.open(out, 'w', encoding='utf-8') as f:
        f.write(html)
    return out


if __name__ == '__main__':
    names = ['songs', 'favs', 'recent', 'ratings', 'queue', 'most',
             'albdetail', 'artdetail', 'artwall', 'xtalbum', 'placeholder',
             'folders', 'foldersload', 'folderssearch', 'foldersempty',
             'tagsort', 'tagsortsort', 'tagsortgroup', 'tagsortempty',
             'plwall', 'plwalldetail', 'plwallempty']
    specs = sys.argv[1:] or [n + ':main' for n in names]
    for spec in specs:
        name, _, page = spec.partition(':')
        print(build(name, page or 'main'))
