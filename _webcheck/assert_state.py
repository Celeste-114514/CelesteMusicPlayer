#!/usr/bin/env python3
"""从 dump-dom 产物里抠出 __state 快照，按场景断言关键状态。"""
import io
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))

def state(name):
    p = os.path.join(HERE, 'dom_%s.html' % name)
    with io.open(p, encoding='utf-8') as f:
        dom = f.read()
    # 源码里也有 /*STATE*/ 字面量（common.js 被内联进页面），运行时生成的
    # __statedump 元素追加在 body 末尾——取最后一次匹配
    ms = re.findall(r'/\*STATE\*/(.*?)/\*END\*/', dom, re.S)
    if not ms:
        return None, 'dom_%s.html 里没有 __state（页面脚本可能没跑起来）' % name
    return json.loads(ms[-1]), None

fails = []
def check(name, cond, msg):
    tag = 'OK ' if cond else 'FAIL'
    if not cond:
        fails.append('%s: %s' % (name, msg))
    print('  [%s] %s' % (tag, msg))

def posted_kinds(st):
    return [p.get('kind') for p in st['posted'] if isinstance(p, dict)]

print('== songs（基线：歌曲面板）')
st, err = state('songs')
if st:
    check('songs', st['title'] == '歌曲', '页头标题 = %r' % st['title'])
    check('songs', st['rowCount'] == 12, '行数 = %s（期望 12）' % st['rowCount'])
    check('songs', 'cols' not in st['cols'].split(), '非详情视图不带 cols（%r）' % st['cols'])
    check('songs', st['theadN'] == '#', '首列表头 = %r' % st['theadN'])
    check('songs', st['firstRowFav'].find('on') >= 0, '首行收藏心实心（fav=true 播种）')
    check('songs', not st['errors'], '无脚本错误: %r' % st['errors'])
    check('songs', 'ready' in posted_kinds(st), '已上报 ready')

print('== favs（我喜欢的音乐）')
st, err = state('favs')
if st:
    check('favs', st['title'] == '我喜欢的音乐', '页头标题 = %r' % st['title'])
    check('favs', st['rowCount'] == 5, '行数 = %s' % st['rowCount'])
    check('favs', st['firstRowFav'].find('on') >= 0, '收藏心默认实心')
    check('favs', 'love' in posted_kinds(st), '点心上报 love')
    check('favs', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== recent（最近播放：行小字 = 播放时间口径）')
st, err = state('recent')
if st:
    check('recent', st['title'] == '最近播放', '页头标题 = %r' % st['title'])
    check('recent', st['rowCount'] == 4, '行数 = %s' % st['rowCount'])
    check('recent', st['firstRowSub'] == '播放于 10-09 14:30 · 播放 3:45 · 播完',
          '首行小字 = %r' % st['firstRowSub'])
    check('recent', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== ratings（评分：胶囊行 + 点 ★3 上报）')
st, err = state('ratings')
if st:
    check('ratings', st['title'] == '评分', '页头标题 = %r' % st['title'])
    check('ratings', st['ratingVisible'] is True, '胶囊行可见')
    check('ratings', st['ratingOn'] == ['3'], '★3 选中（当前 %r）' % st['ratingOn'])
    check('ratings', '3 星' in (st['sub'] or ''), '页头副标题带星级 = %r' % st['sub'])
    check('ratings', 'rating' in posted_kinds(st), '点胶囊上报 rating')
    check('ratings', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== queue（播放队列）')
st, err = state('queue')
if st:
    check('queue', st['title'] == '播放队列', '页头标题 = %r' % st['title'])
    check('queue', st['rowCount'] == 6, '行数 = %s' % st['rowCount'])
    check('queue', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== most（播放最多）')
st, err = state('most')
if st:
    check('most', st['title'] == '播放最多', '页头标题 = %r' % st['title'])
    check('most', st['rowCount'] == 7, '行数 = %s' % st['rowCount'])
    check('most', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== albdetail（专辑详情：左栏 + CD 分组 + enqueue）')
st, err = state('albdetail')
if st:
    check('albdetail', st['title'] == '专辑 1', '页头标题 = %r' % st['title'])
    check('albdetail', 'cols' in st['cols'].split(), '多栏布局（%r）' % st['cols'])
    check('albdetail', st['albmetaVisible'] is True, '左栏可见')
    check('albdetail', st['artmetaVisible'] is False, '艺术家左栏不出现')
    check('albdetail', st['theadN'] == '音轨号', '首列表头 = %r' % st['theadN'])
    check('albdetail', st['discs'] == ['CD1', 'CD2', 'CD?'], '碟片分组头 = %r' % st['discs'])
    check('albdetail', st['rowCount'] == 5, '行数 = %s' % st['rowCount'])
    check('albdetail', st['tech'] == 'FLAC | 24bit/96kHz | 2.3 Mbps | 25:00', '质量行 = %r' % st['tech'])
    check('albdetail', st['dsdhint'] is True, 'DSD 提示在')
    check('albdetail', st['enqueueBtn'] is True, '有添加至播放队列按钮')
    check('albdetail', 'enqueue' in posted_kinds(st), '点按钮上报 enqueue')
    check('albdetail', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== artdetail（艺术家详情：三栏 + 网络头像更新大头像）')
st, err = state('artdetail')
if st:
    check('artdetail', st['title'] == '歌手 0', '页头标题 = %r' % st['title'])
    check('artdetail', 'cols' in st['cols'].split(), '多栏布局（%r）' % st['cols'])
    check('artdetail', st['artmetaVisible'] is True, '左栏可见')
    check('artdetail', st['albmetaVisible'] is False, '专辑左栏不出现')
    # 2026-10-09 用户拍板改版：歌曲列表移到歌手信息下方（左栏），专辑墙在右栏跨两行，
    # 两段各带独立滚轮——布局由 #view 的 cols+art 两个类驱动（不再给 #listwrap 加 art）
    check('artdetail', 'art' in st['cols'].split(),
          '艺术家详情 grid 两栏布局（#view=%r）' % st['cols'])
    check('artdetail', st['listArt'] == '', '歌曲列表不再固定 430px（%r）' % st['listArt'])
    check('artdetail', st['xtalbHead'].startswith('该艺术家的专辑'), '右栏标题 = %r' % st['xtalbHead'])
    check('artdetail', st['xtalbCards'] == 4, '右栏专辑卡 = %s' % st['xtalbCards'])
    check('artdetail', st['rowCount'] == 6, '中栏歌曲行 = %s' % st['rowCount'])
    check('artdetail', st['firstRowSub'] == '专辑 1', '中栏小字只显专辑 = %r' % st['firstRowSub'])
    check('artdetail', 'enqueue' in posted_kinds(st), '点按钮上报 enqueue')
    check('artdetail', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== artwall（艺术家墙：只带 name 的头像补推按名字匹配）')
st, err = state('artwall')
if st:
    check('artwall', st['title'] == '艺术家', '页头标题 = %r' % st['title'])
    check('artwall', st['artCardsWithImg'] == 2, '按名字补到头像的卡片 = %s（期望 2）' % st['artCardsWithImg'])
    check('artwall', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== xtalbum（艺术家详情点专辑卡 → 专辑详情，两类详情互斥）')
st, err = state('xtalbum')
if st:
    check('xtalbum', st['title'] == '专辑 1', '页头标题 = %r（不能还是艺术家名）' % st['title'])
    check('xtalbum', st['albmetaVisible'] is True, '专辑左栏可见')
    check('xtalbum', st['artmetaVisible'] is False, '艺术家左栏不残留')
    check('xtalbum', st['discs'] == ['CD1', 'CD2', 'CD3'], '碟片分组头 = %r' % st['discs'])
    check('xtalbum', '25:00' in (st['sub'] or ''),
          '页头副标题带总时长 = %r' % st['sub'])
    check('xtalbum', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== placeholder（没做的面板 → 占位页；网络音乐库用户说暂时不动）')
st, err = state('placeholder')
if st:
    check('placeholder', st['title'] == '网络音乐库', '页头标题 = %r' % st['title'])
    check('placeholder', st['rowCount'] == 0, '没有行残留')
    check('placeholder', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== folders（媒体库全流程：根行完整路径/展开/右栏/行播放/文件播放/折叠）')
st, err = state('folders')
if st:
    check('folders', st['folderVisible'] is True, '媒体库视图可见')
    check('folders', st['title'] == '媒体库', '页头标题 = %r' % st['title'])
    check('folders', '2 个目录' in (st['sub'] or '') and '华语' in (st['sub'] or ''),
          '页头副标题 = %r' % st['sub'])
    # 折叠后只剩两个根行；根行显示完整路径、pad=8px、选中态在第一个根上
    check('folders', st['folderNodeCount'] == 2, '折叠后左树节点 = %s（期望 2）' % st['folderNodeCount'])
    n0 = st['folderNodes'][0]
    check('folders', n0['n'] == 'D:\\音乐\\华语' and n0['root'] and n0['pad'] == '8px',
          '根行完整路径+根样式+缩进 = %r' % n0)
    check('folders', n0['sel'] is True and n0['open'] is False, '根行选中、已折叠（%r）' % n0)
    check('folders', st['folderNodes'][1]['sel'] is False, '第二个根不选中')
    check('folders', st['folderHead'] == 'D:\\音乐\\华语', '右栏台头完整路径 = %r' % st['folderHead'])
    check('folders', st['folderRowCount'] == 2, '右栏行数 = %s（折叠不清右栏）' % st['folderRowCount'])
    check('folders', st['folderFirstRow']['t'] == '01 - 晴天.flac'
                    and st['folderFirstRow']['s'] == '周杰伦 · 叶惠美'
                    and st['folderFirstRow']['d'] == '4:29',
          '右栏首行 = %r' % st['folderFirstRow'])
    kinds = posted_kinds(st)
    check('folders', kinds.count('folder') == 1, '点文件夹上报 folder 一次（当前 %d）' % kinds.count('folder'))
    check('folders', kinds.count('folderplay') == 2, '行+文件各上报 folderplay 一次（当前 %d）' % kinds.count('folderplay'))
    check('folders', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== foldersload（媒体库加载中：大文件夹读盘期间右栏显示加载中）')
st, err = state('foldersload')
if st:
    check('foldersload', st['folderHead'] == 'D:\\音乐', '右栏台头 = %r' % st['folderHead'])
    check('foldersload', st['folderEmptyText'] == '加载中…', '右栏空态文案 = %r' % st['folderEmptyText'])
    check('foldersload', st['folderRowCount'] == 0, '还没有行')
    n0 = st['folderNodes'][0] if st['folderNodes'] else {}
    check('foldersload', n0.get('sel') is True and n0.get('open') is True, '根行选中+展开 = %r' % n0)
    check('foldersload', 'folder' in posted_kinds(st), '已上报 folder')
    check('foldersload', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== folderssearch（媒体库搜索：只筛右栏文件名；重复点已展开文件夹重load）')
st, err = state('folderssearch')
if st:
    check('folderssearch', st['folderSearchPh'] == '搜索文件名', '搜索框占位符 = %r' % st['folderSearchPh'])
    check('folderssearch', st['folderRowCount'] == 1, '筛「七里香」后行数 = %s' % st['folderRowCount'])
    check('folderssearch', st['folderFirstRow']['t'] == '02 - 七里香.flac',
          '筛选后首行 = %r' % st['folderFirstRow'])
    check('folderssearch', posted_kinds(st).count('folder') == 2,
          '重复点已展开文件夹再次上报 folder（防右栏卡空）')
    check('folderssearch', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== foldersempty（媒体库空态：没配置媒体库目录）')
st, err = state('foldersempty')
if st:
    check('foldersempty', st['folderNodeCount'] == 0, '左树无节点')
    check('foldersempty', (st['folderEmptyText'] or '').find('请选择文件夹') >= 0,
          '左树空态文案 = %r' % st['folderEmptyText'])
    check('foldersempty', st['folderHead'] == '选择文件夹查看歌曲', '右栏台头 = %r' % st['folderHead'])
    check('foldersempty', st['sub'] == '未配置媒体库文件夹', '页头副标题 = %r' % st['sub'])
    check('foldersempty', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== tagsort（标签排序全流程：墙/五视角/列头排序/播放意图）')
st, err = state('tagsort')
if st:
    check('tagsort', st['tagsortVisible'] is True, '标签排序视图可见')
    check('tagsort', st['searchVisible'] is False, '搜索框已隐藏（原生标签排序页无搜索）')
    # 收尾在曲目视角（墙上进分组后又回曲目）
    check('tagsort', st['tagsortMode'] == 'Songs', '当前视角 = 曲目（%r）' % st['tagsortMode'])
    check('tagsort', st['tagsortPanelVisible'] is True, '钻取面板可见')
    check('tagsort', st['tagsortTitle'] == '流派：流行', '面板标题 = %r' % st['tagsortTitle'])
    check('tagsort', st['tagsortRowCount'] == 3, '曲目行数 = %s' % st['tagsortRowCount'])
    check('tagsort', st['tagsortFirstRow']['cells'][0] == '歌曲 1',
          '首行首列 = %r' % st['tagsortFirstRow']['cells'][0])
    check('tagsort', st['tagsortCols'][1]['on'] is True and st['tagsortCols'][1]['k'] == 'Artist',
          '排序标记在「艺术家」列（%r）' % st['tagsortCols'][1])
    # now 高亮：Songs 视角 index 按分类曲目算，第 3 行（index=2）亮 ♪
    check('tagsort', st['tagsortOnIdx'] == '2', '高亮行 data-i = %r（期望 2）' % st['tagsortOnIdx'])
    check('tagsort', st['tagsortOnN'] == '\u266a', '高亮行序号换 ♪（%r）' % st['tagsortOnN'])
    check('tagsort', st['playAllVisible'] == '',
          '面板播放全部按钮可见（visibility=%r）' % st['playAllVisible'])
    kinds = posted_kinds(st)
    check('tagsort', kinds.count('tagsortmore') == 1,
          '墙上「加载全部剩余」上报一次（当前 %d）' % kinds.count('tagsortmore'))
    check('tagsort', kinds.count('tagsortfield') == 1,
          '切分类字段上报一次（当前 %d）' % kinds.count('tagsortfield'))
    check('tagsort', kinds.count('tagsortopen') == 1,
          '点分类卡上报一次（当前 %d）' % kinds.count('tagsortopen'))
    check('tagsort', kinds.count('tagsortcol') == 2,
          '列头点击两次=新列升序+同列降序（当前 %d）' % kinds.count('tagsortcol'))
    check('tagsort', kinds.count('tagsortsong') == 1, '曲目行播放上报一次（不换队列）')
    check('tagsort', kinds.count('tagsortdrill') == 2,
          '专辑+艺术家卡各钻取一次（当前 %d）' % kinds.count('tagsortdrill'))
    check('tagsort', kinds.count('tagsortmode') == 6,
          '视角胶囊+墙上分组入口共 6 次（当前 %d）' % kinds.count('tagsortmode'))
    check('tagsort', kinds.count('tagsortsort') == 1, '排序预设上报一次')
    check('tagsort', kinds.count('tagsortorder') == 1, '升降序上报一次')
    check('tagsort', kinds.count('tagsortcustom') == 2, '自定义配置窗两次（排序+字段）')
    check('tagsort', kinds.count('tagsorttoggle') == 2, '组头折叠+展开各一次')
    check('tagsort', kinds.count('tagsortexpand') == 2, '全部折叠+全部展开各一次')
    check('tagsort', kinds.count('tagsortgroupplay') == 1, '组头播放钮上报一次')
    check('tagsort', kinds.count('tagsortgroupsong') == 1, '组内歌曲上报一次')
    check('tagsort', kinds.count('tagsortgroup') == 1, '分组预设切换上报一次')
    check('tagsort', kinds.count('tagsortplay') == 1, '面板播放全部上报一次')
    check('tagsort', kinds.count('tagsortback') == 1, '返回分类墙上报一次')
    check('tagsort', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== tagsortsort（排序方式视角：预设/升降序守卫/自定义状态文本）')
st, err = state('tagsortsort')
if st:
    check('tagsortsort', st['tagsortSortVisible'] is True, '排序视角可见')
    check('tagsortsort', st['tagsortMode'] == 'Sort', '当前视角 = 排序方式（%r）' % st['tagsortMode'])
    check('tagsortsort', st['tagsortSortOn'] == ['流派 / 专辑'], '选中的预设 = %r' % st['tagsortSortOn'])
    check('tagsortsort', st['tagsortAscOn'] is False and st['tagsortDescOn'] is True, '降序激活、升序未激活')
    check('tagsortsort', st['tagsortSortStatus'] == '当前排序依据：流派 / 专辑',
          '状态文本 = %r' % st['tagsortSortStatus'])
    kinds = posted_kinds(st)
    check('tagsortsort', kinds.count('tagsortorder') == 1,
          '升降序只上报一次（点已激活项被守卫拦下，当前 %d）' % kinds.count('tagsortorder'))
    check('tagsortsort', kinds.count('tagsortsort') == 1, '预设切换上报一次')
    check('tagsortsort', kinds.count('tagsortcustom') == 1, '自定义排序…上报一次')
    check('tagsortsort', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== tagsortgroup（分组浏览：三级树/缩进/折叠/播放钮/预设切换）')
st, err = state('tagsortgroup')
if st:
    check('tagsortgroup', st['tagsortGroupVisible'] is True, '分组视角可见')
    check('tagsortgroup', st['tagsortMode'] == 'GroupBy', '当前视角 = 分组浏览（%r）' % st['tagsortMode'])
    check('tagsortgroup', st['tagsortGroupSel'] == 'Artist,Album,Year',
          '预设下拉当前值 = %r' % st['tagsortGroupSel'])
    # 收尾在「歌手 0 已折叠」的三级树：收起的组头仍在（只剩组头行），歌手 1 的子树完整在
    heads = st['tagsortGroupHeads']
    check('tagsortgroup', [h['path'] for h in heads] == ['0', '1', '1.0', '1.0.0'],
          '折叠后剩余组头 = %r（歌手 0 收起只剩组头行，歌手 1 三级子树在）' % [h['path'] for h in heads])
    check('tagsortgroup', [h['pad'] for h in heads] == ['0px', '0px', '22px', '44px'],
          '组头缩进 depth×22 = %r' % [h['pad'] for h in heads])
    check('tagsortgroup', heads[0]['v'] == '歌手 0' and heads[0]['l'] == '艺术家' and not heads[0]['open']
          and heads[1]['v'] == '歌手 1' and heads[1]['l'] == '艺术家'
          and heads[2]['v'] == '专辑 1' and heads[2]['l'] == '专辑'
          and heads[3]['v'] == '2021' and heads[3]['l'] == '年份',
          '组头值+字段标签逐级对上（歌手 0 已收起）= %r' % heads)
    check('tagsortgroup', not heads[0]['open'] and all(h['open'] for h in heads[1:]),
          '歌手 0 组头收起、其余组头全部展开')
    check('tagsortgroup', st['tagsortGroupSongCount'] == 1,
          '组内歌曲行 = %s（期望 1，歌手 0 子树已折叠）' % st['tagsortGroupSongCount'])
    gs = st['tagsortFirstGroupSong']
    check('tagsortgroup', gs and gs['pad'] == '66px' and gs['t'] == '歌曲 3'
          and gs['a'] == '歌手 1' and gs['d'] == '3:02' and gs['file'] == 'D:\\音乐\\c.flac',
          '歌曲行（缩进 66px/标题/艺术家/时长/文件路径）= %r' % gs)
    kinds = posted_kinds(st)
    check('tagsortgroup', kinds.count('tagsorttoggle') == 3,
          '组头点击三次=折/展/收尾再折（当前 %d）' % kinds.count('tagsorttoggle'))
    check('tagsortgroup', kinds.count('tagsortexpand') == 2, '全部折叠+全部展开各一次')
    check('tagsortgroup', kinds.count('tagsortgroupplay') == 1, '组头播放钮上报一次（不触发展折）')
    check('tagsortgroup', kinds.count('tagsortgroupsong') == 1, '组内歌曲上报一次')
    check('tagsortgroup', kinds.count('tagsortgroup') == 1, '预设切换上报一次')
    gp = [p for p in st['posted'] if p.get('kind') == 'tagsortgroupplay']
    check('tagsortgroup', gp and gp[0].get('path') == '0',
          '组头播放 path = %r' % (gp[0].get('path') if gp else None))
    gsn = [p for p in st['posted'] if p.get('kind') == 'tagsortgroupsong']
    check('tagsortgroup', gsn and gsn[0].get('path') == '0.0' and gsn[0].get('group') == '专辑 1'
          and gsn[0].get('file') == 'D:\\音乐\\a.flac',
          '组内歌曲 path+group+file = %r' % (gsn[0] if gsn else None))
    gg = [p for p in st['posted'] if p.get('kind') == 'tagsortgroup']
    check('tagsortgroup', gg and gg[0].get('tag') == 'Artist,Album,Year',
          '预设切换 tag = %r' % (gg[0].get('tag') if gg else None))
    check('tagsortgroup', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== tagsortempty（标签排序空态：空曲库/空分组/空分类）')
st, err = state('tagsortempty')
if st:
    check('tagsortempty', st['tagsortVisible'] is True, '标签排序视图可见')
    check('tagsortempty', st['tagsortWallEmpty'] == '曲库里还没有歌曲',
          '墙空态文案 = %r' % st['tagsortWallEmpty'])
    check('tagsortempty', st['tagsortGroupEmpty'] == '没有可分组的内容',
          '分组空态文案 = %r' % st['tagsortGroupEmpty'])
    check('tagsortempty', st['tagsortSongsEmpty'] == '这个分类下没有歌曲',
          '曲目空态文案 = %r' % st['tagsortSongsEmpty'])
    check('tagsortempty', st['tagsortGridEmpty'] == '这个分类下没有歌曲',
          '专辑视角空态文案 = %r' % st['tagsortGridEmpty'])
    check('tagsortempty', st['tagsortMode'] == 'Albums', '收尾在专辑视角（%r）' % st['tagsortMode'])
    check('tagsortempty', st['sub'] == '0 张专辑', '页头副标题 = %r' % st['sub'])
    kinds = posted_kinds(st)
    check('tagsortempty', kinds.count('tagsortmode') == 3,
          '视角切换三次（分组入口+曲目+专辑，当前 %d）' % kinds.count('tagsortmode'))
    check('tagsortempty', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== plwall（播放列表全流程：墙/封面回填/详情/行播放/入队/重命名/删除两级确认/回墙）')
st, err = state('plwall')
if st:
    check('plwall', st['plLoadingEmpty'] == '加载中…',
          '墙数据没到前显示加载中 = %r' % st['plLoadingEmpty'])
    check('plwall', st['plLoadingSub'] == '加载中…', '页头副标题加载中 = %r' % st['plLoadingSub'])
    # 收尾在墙上（删除后 C# 推 back=true 回墙）：被删的「华语经典」不见了
    check('plwall', st['plwallVisible'] is True, '收尾在播放列表墙')
    check('plwall', st['pldetailVisible'] is False, '详情已收起')
    check('plwall', st['plCardCount'] == 2, '删除后墙上卡片 = %s（期望 2）' % st['plCardCount'])
    check('plwall', st['plFirstCard']['n'] == '睡前纯音乐'
                    and st['plFirstCard']['a'] == '1 首',
          '墙首卡（名字+首数）= %r' % st['plFirstCard'])
    check('plwall', st['plCardsWithImg'] == 2, '回墙后封面重新预热补齐（%s 张）' % st['plCardsWithImg'])
    check('plwall', st['title'] == '播放列表' and st['sub'] == '2 个',
          '页头标题/副标题 = %r / %r' % (st['title'], st['sub']))
    check('plwall', st['searchVisible'] is False, '墙上无搜索框（原生同口径）')
    check('plwall', st['playAllVisible'] == 'hidden', '播放全部按钮已藏')
    kinds = posted_kinds(st)
    check('plwall', kinds.count('plopen') == 1, '点卡上报 plopen 一次')
    check('plwall', kinds.count('plplay') == 1, '详情行播放上报一次')
    pp = [p for p in st['posted'] if p.get('kind') == 'plplay']
    check('plwall', pp and pp[0].get('name') == '华语经典'
                    and pp[0].get('path') == 'D:\\音乐\\b.flac',
          'plplay 带单名+文件路径（整单替换从该首）= %r' % (pp[0] if pp else None))
    check('plwall', kinds.count('plqueue') == 1, '整单入队上报一次')
    pr = [p for p in st['posted'] if p.get('kind') == 'plrename']
    check('plwall', pr and pr[0].get('name') == '华语经典'
                    and pr[0].get('newName') == '华语经典2',
          'plrename 带旧名+新名 = %r' % (pr[0] if pr else None))
    check('plwall', kinds.count('pldel') == 1,
          '删除两级确认后只上报一次（第一下待确认不上报）')
    check('plwall', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== plwalldetail（播放列表详情：行内容/收藏心/now 按路径高亮/重命名输入框/plmsg）')
st, err = state('plwalldetail')
if st:
    check('plwalldetail', st['pldetailVisible'] is True, '详情可见')
    check('plwalldetail', st['plwallVisible'] is False, '墙已收起')
    check('plwalldetail', st['title'] == '华语经典' and st['sub'] == '2 首',
          '页头标题/副标题 = %r / %r' % (st['title'], st['sub']))
    check('plwalldetail', st['plRowCount'] == 2, '详情行数 = %s' % st['plRowCount'])
    r0 = st['plFirstRow']
    check('plwalldetail', r0['n'] == '1' and r0['t'] == '晴天'
                         and r0['s'] == '周杰伦 · 叶惠美' and r0['d'] == '4:29'
                         and r0['p'] == 'D:\\音乐\\a.flac',
          '详情首行（序号/标题/小字/时长/路径）= %r' % r0)
    check('plwalldetail', r0['fav'].find('on') < 0, '未收藏行空心')
    # now 的 path 驱动高亮：index=5 对不上行序，亮的必须是 path 对应的第 2 行
    check('plwalldetail', st['plOnP'] == 'D:\\音乐\\b.flac',
          '高亮行按 path 命中 = %r（不是按 index）' % st['plOnP'])
    check('plwalldetail', st['plOnN'] == '\u266a', '高亮行序号换 ♪ = %r' % st['plOnN'])
    check('plwalldetail', st['plNameInput'] is False, '重命名输入框已收起（确定后）')
    check('plwalldetail', st['plMsgVisible'] is True, 'plmsg 失败提示可见')
    check('plwalldetail', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== plwallempty（播放列表空态 + 新建）')
st, err = state('plwallempty')
if st:
    # 流程收尾在「新建的空单详情」里（点新建后 C# 推 pldetail 直接进详情）
    check('plwallempty', st['pldetailVisible'] is True, '新建后进空单详情')
    check('plwallempty', st['plwallVisible'] is False, '墙已收起（在详情里）')
    check('plwallempty', st['plWallEmpty'].find('还没有创建播放列表') >= 0,
          '墙空态文案（还在 DOM 里，已隐藏）= %r' % st['plWallEmpty'])
    check('plwallempty', 'plnew' in posted_kinds(st), '点新建上报 plnew')
    check('plwallempty', st['title'] == '新建播放列表' and st['sub'] == '0 首',
          '详情页头 = %r / %r' % (st['title'], st['sub']))
    check('plwallempty', st['plRowCount'] == 0, '空单无行')
    check('plwallempty', st['plDetailEmpty'] == '这个播放列表还是空的',
          '详情空态文案 = %r' % st['plDetailEmpty'])
    check('plwallempty', not st['errors'], '无脚本错误: %r' % st['errors'])

print('== dsp（音效处理网页：全量状态 / 12 页控件 / 全部交互上报 / 播放条 / 旁路三态 / 主题）')
st, err = state('dsp')
if st:
    check('dsp', not st['errors'], '无脚本错误: %r' % st['errors'])
    check('dsp', 'ready' in posted_kinds(st), '已上报 ready')
    # ---- 左导航：12 项 + 6 个分组头 + 收尾在参数 EQ 页 ----
    check('dsp', st['navItemCount'] == 12, '导航 12 项（当前 %s）' % st['navItemCount'])
    check('dsp', st['navGroupTexts'] == ['输入', '采样率', '塑形', '声道', '空间', '输出安全'],
          '导航分组头 = %r' % st['navGroupTexts'])
    check('dsp', st['navOn'] == 5 and st['pgOn'] == 5,
          '收尾在参数 EQ 页（nav=%s pg=%s）' % (st['navOn'], st['pgOn']))
    check('dsp', st['navDots'][6] is True and st['navDots'][5] is False and st['navDots'][0] is False,
          '压缩模块圆点亮、当前页/方案页不亮 = %r' % st['navDots'])
    # ---- 页头电源开关 ----
    check('dsp', st['pwr']['eq'] == {'on': True, 'dis': False, 'hint': ''},
          'EQ 电源接通态 = %r' % st['pwr']['eq'])
    check('dsp', st['pwr']['opra']['dis'] is True and st['pwr']['opra']['hint'] == '未安装 OPRA 数据库',
          '耳机校正禁用+原因 = %r' % st['pwr']['opra'])
    check('dsp', st['pwr']['fir']['dis'] is True and st['pwr']['fir']['hint'] == '未导入 IR',
          'FIR 禁用+原因 = %r' % st['pwr']['fir'])
    # ---- 总旁路 + bit-perfect 三态 ----
    check('dsp', st['bypassCk'] is False, '总旁路开关关')
    check('dsp', st['bpStatus'] == '输出非 bit-perfect（DSP 生效）' and st['bpStatusCls'] == 'bpst warn',
          'DSP 生效时顶栏转琥珀 = %r/%r' % (st['bpStatus'], st['bpStatusCls']))
    bs = st['bypassSeen']
    check('dsp', bs['hint'] == '已旁路' and bs['bp'].startswith('bit-perfect 直通（DSP 已旁路）')
                 and bs['pillCls'] == 'pill ok',
          '旁路态：电源提示「已旁路」+ 顶栏/pill 转绿 = %r' % bs)
    ds = st['directSeen']
    check('dsp', ds['bp'].startswith('输出 bit-perfect 直出') and ds['pillCls'] == 'pill ok',
          '直出态：顶栏/pill 均绿 = %r' % ds)
    # ---- dspmsg ----
    check('dsp', st['dmsgSeen']['vis'] == '' and '已应用' in st['dmsgSeen']['text'],
          'dspmsg 显示过操作结果 = %r' % st['dmsgSeen'])
    check('dsp', st['dmsgVisible'] == 'none' and st['dmsgText'] == '', 'dspmsg 已收起')
    # ---- EQ 页 ----
    check('dsp', st['eqMode'] == '0' and st['eqProVisible'] == '' and st['eqSimpleVisible'] == 'none',
          '专业模式：专业区可见/简单区隐藏')
    check('dsp', st['eqBandBoxVisible'] == '', '选中段编辑器可见')
    check('dsp', st['eqPresetSel'] == 2 and st['eqPresetOpts'] == ['默认', '流行', '摇滚'],
          '预设下拉选中「摇滚」= %r' % st['eqPresetOpts'])
    pills = st['eqStrip']
    check('dsp', [p['t'] for p in pills] == ['EQ 生效中', '输出非 bit-perfect（DSP 生效）',
                                              '预增益 -2.5 dB', '余量 -6.0 dB', '预设：摇滚'],
          '快捷条五 pill 全文 = %r' % [p['t'] for p in pills])
    check('dsp', [p['c'] for p in pills] == ['hi', 'amber', '', '', ''],
          'pill 配色（生效=红/非直通=琥珀/其余默认）= %r' % [p['c'] for p in pills])
    chips = st['eqChips']
    check('dsp', len(chips) == 3 and chips[0]['on'] is True and chips[1]['on'] is False,
          '频段 chips 选中第 1 段 = %r' % chips)
    check('dsp', chips[0]['off'] is True and chips[1]['off'] is False and chips[2]['off'] is True,
          '停用段空心点、启用段实心点 = %r' % chips)
    eb = st['eqBand']
    check('dsp', eb['freq'] == '12' and eb['freqRd'] == '4.10 kHz' and eb['gain'] == '6'
                 and eb['gainRd'] == '+6.0 dB' and eb['q'] == '2.5' and eb['qRd'] == '2.50'
                 and eb['ty'] == 'HighPass' and eb['on'] is False,
          '选中段编辑器（频率/增益/Q/类型/停用）= %r' % eb)
    # ---- 各模块控件读数 ----
    c = st['ctrl']
    check('dsp', c['headroom']['v'] == '-3' and c['headroom']['rd'] == '-3.0 dB'
                 and c['limiter']['ck'] is False, '输入余量页 = %r' % c['headroom'])
    check('dsp', c['rgmode']['v'] == '0' and c['rgmode']['opts'] == ['关闭', '单曲 (Track)', '专辑 (Album)']
                 and c['rgpreamp']['rd'] == '+3.0 dB' and c['rgclip']['ck'] is False,
          '响度页（模式下拉三选项+读数）')
    check('dsp', c['srcrate']['v'] == '2' and len(c['srcrate']['opts']) == 7
                 and c['srcquality']['v'] == '2' and len(c['srcquality']['opts']) == 3
                 and c['srcdither']['v'] == '3' and len(c['srcdither']['opts']) == 4,
          'SRC 三下拉（7/3/4 项）')
    check('dsp', c['compthreshold']['rd'] == '-18.0 dB' and c['compratio']['rd'] == '2.50'
                 and c['compattack']['rd'] == '25 ms' and c['comprelease']['rd'] == '350 ms'
                 and c['compknee']['rd'] == '+12.0 dB' and c['compmakeup']['rd'] == '+4.0 dB'
                 and c['compmix']['rd'] == '60 %' and c['comppeak']['ck'] is False,
          '压缩器八控件读数')
    check('dsp', c['chbalance']['rd'] == '偏左 35 %' and c['chlgain']['rd'] == '-2.0 dB'
                 and c['chrgain']['rd'] == '+2.0 dB' and c['chldelay']['rd'] == '2.5 ms'
                 and c['chrdelay']['rd'] == '0.0 ms' and c['chmono']['v'] == '3'
                 and len(c['chmono']['opts']) == 4,
          '声道工具读数（平衡偏左/增益/延迟/单声道）')
    check('dsp', c['chswap']['ck'] is True and c['chinvertl']['ck'] is True
                 and c['chinvertr']['ck'] is False, '声道三开关')
    check('dsp', c['xfeedlevel']['rd'] == '40 %' and c['xfeedcutoff']['rd'] == '500 Hz',
          'Crossfeed 两控件读数')
    check('dsp', c['fieldwidth']['rd'] == '120 %' and c['fieldcenter']['rd'] == '-3.0 dB'
                 and c['fieldside']['rd'] == '+2.0 dB', '声场三旋钮读数')
    check('dsp', c['mll']['rd'] == '1.05' and c['mrl']['rd'] == '-0.20'
                 and c['mlr']['rd'] == '0.15' and c['mrr']['rd'] == '0.95', '矩阵四系数读数')
    check('dsp', c['roomtrim']['rd'] == '-3.0 dB', 'FIR trim 读数')
    check('dsp', c['eqbass']['v'] == '0.8' and c['eqbass']['rd'] == '80 %', '简单模式低音读数')
    # ---- 听音方案页 ----
    check('dsp', st['pfStatus'] == '当前方案：客厅耳机', '方案状态行 = %r' % st['pfStatus'])
    check('dsp', st['pfRows'] == [{'nm': '默认', 'st': '2026-10-01 10:24', 'on': False, 'cur': False},
                                  {'nm': '客厅耳机', 'st': '2026-10-09 21:03', 'on': True, 'cur': True}],
          '方案行（保存→删除后剩两行，选中行带「当前」）= %r' % st['pfRows'])
    check('dsp', st['pfApplyDisabled'] is False and st['pfUpdateDisabled'] is False
                 and st['pfDeleteDisabled'] is False, '方案三按钮可用')
    check('dsp', st['pfNameValue'] == '', '保存后名字输入框已清空')
    # ---- 机架编排页 ----
    check('dsp', len(st['rkRows']) == 8, '机架 8 行（当前 %s）' % len(st['rkRows']))
    r0 = st['rkRows'][0]
    check('dsp', r0['slot'] == '1.' and r0['n'] == '输入余量' and r0['st'] == '生效中'
                 and r0['col'] == 'var(--ok)', '机架首行（槽位/名/状态/配色）= %r' % r0)
    check('dsp', st['rkRows'][2]['on'] is True and st['rkRows'][0]['on'] is False,
          '选中行高亮落在第 3 行')
    check('dsp', st['rkUpDisabled'] is False and st['rkDownDisabled'] is False,
          '上移/下移按钮可用（sel=2）')
    check('dsp', st['rkHint'] == '目标采样率与质量档位，改完下次开播生效。',
          '选中行说明 = %r' % st['rkHint'])
    # ---- 输出监控 + 各页说明文本 ----
    check('dsp', st['mon'] == {'peak': '-0.2 dBFS', 'clip': '7 次', 'over': '过载',
                               'overCls': 'v warn', 'active': 'EQ · 压缩 · 限幅',
                               'chain': '44.1 → 96 kHz'},
          '监控五读数（过载转琥珀）= %r' % st['mon'])
    check('dsp', st['srcState'] == '当前 44.1 kHz 直出；目标 96 kHz，均衡档，TPDF 抖动。',
          'SRC 状态文本 = %r' % st['srcState'])
    check('dsp', st['rgInfo'] == '专辑模式 · 预增益 0 dB · 防削波开。'
                 and st['firStatus'] == '未导入 IR；导入后可按当前 trim 试算安全余量。'
                 and st['firClip'] == '按当前 trim 试算：安全。'
                 and st['mxPhase'] == '矩阵当前无异常相位。',
          '响度/FIR/削波/相位四段说明文本')
    # ---- 播放条 ----
    check('dsp', st['pbTitle'] == '晴天' and st['pbSub'] == '周杰伦 · 叶惠美',
          '播放条曲目 = %r / %r' % (st['pbTitle'], st['pbSub']))
    check('dsp', st['pbCovImg'] is True and '\u25b6' in st['playIcon'],
          '封面在 + 暂停态显播放三角')
    check('dsp', st['trackFillW'] == '50%' and st['tmCur'] == '1:53' and st['tmDur'] == '3:47'
                 and st['volFillW'] == '30%',
          '拖进度 50%%/拖音量 30%% 后读数 = %r/%r/%r' % (st['trackFillW'], st['tmCur'], st['volFillW']))
    # ---- 主题 ----
    check('dsp', st['htmlClass'] == '' and st['themeDarkSeen'] == 'dark',
          '深色主题生效过、收尾回浅色 = %r/%r' % (st['htmlClass'], st['themeDarkSeen']))
    # ---- 上报消息 ----
    posted = st['posted']
    kinds = posted_kinds(st)
    navs = [p.get('i') for p in posted if p.get('kind') == 'dspnav']
    check('dsp', navs == [5, 0, 1, 2, 3, 4, 6, 7, 8, 9, 10, 11, 5],
          '12 页走巡一遍后回 EQ = %r' % navs)
    pws = [p for p in posted if p.get('kind') == 'dsppower']
    check('dsp', pws == [{'kind': 'dsppower', 'k': 'eq', 'on': False},
                         {'kind': 'dsppower', 'k': 'eq', 'on': True}],
          'EQ 电源关→开两条 = %r' % pws)
    def dset(ck):
        return [p.get('v') for p in posted if p.get('kind') == 'dspset' and p.get('c') == ck]
    check('dsp', dset('eqbandsel') == [0, 0], '频段选中上报（中途+收尾）= %r' % dset('eqbandsel'))
    check('dsp', dset('eqpreset') == [2] and dset('eqbandgain') == [6]
                 and dset('eqbandtype') == ['HighPass'] and dset('eqbandon') == [False]
                 and dset('eqbandfreq') == [12] and dset('eqbandq') == [2.5],
          'EQ 选中段六控件上报')
    check('dsp', dset('eqmode') == [1, 0] and dset('eqbass') == [0.8],
          '专业↔简单 + 简单模式低音上报')
    check('dsp', dset('headroom') == [-3] and dset('limiter') == [False], '余量页上报')
    check('dsp', dset('rgmode') == [0] and dset('rgpreamp') == [3] and dset('rgclip') == [False],
          '响度页上报')
    check('dsp', dset('srcrate') == [2] and dset('srcquality') == [2] and dset('srcdither') == [3],
          'SRC 页上报')
    check('dsp', dset('compthreshold') == [-18] and dset('compratio') == [2.5]
                 and dset('compattack') == [25] and dset('comprelease') == [350]
                 and dset('compknee') == [12] and dset('compmakeup') == [4]
                 and dset('compmix') == [60] and dset('comppeak') == [False],
          '压缩器八控件上报')
    check('dsp', dset('chbalance') == [-0.35] and dset('chlgain') == [-2] and dset('chrgain') == [2]
                 and dset('chldelay') == [2.5] and dset('chrdelay') == [0] and dset('chmono') == [3]
                 and dset('chswap') == [True] and dset('chinvertl') == [True]
                 and dset('chinvertr') == [False] and dset('xfeedlevel') == [40]
                 and dset('xfeedcutoff') == [500],
          '声道工具十一控件上报')
    check('dsp', dset('fieldwidth') == [120] and dset('fieldcenter') == [-3] and dset('fieldside') == [2],
          '声场三旋钮上报')
    check('dsp', dset('mll') == [1.05] and dset('mrl') == [-0.2] and dset('mlr') == [0.15]
                 and dset('mrr') == [0.95], '矩阵四系数上报')
    check('dsp', dset('roomtrim') == [-3], 'FIR trim 上报')
    def dact(a):
        return [p for p in posted if p.get('kind') == 'dspact' and p.get('a') == a]
    ps = dact('pfsave')
    check('dsp', len(ps) == 1 and ps[0].get('name') == '睡前耳机', '保存方案带名字 = %r' % ps)
    check('dsp', [p.get('i') for p in dact('pfsel')] == [2], '方案行选中上报')
    for a in ['pfapply', 'pfupdate', 'pfdelete', 'rkdown', 'rkup', 'rkreset', 'eqaddband',
              'eqdelband', 'eqflat', 'equndo', 'eqredo', 'eqab', 'eqautogain', 'eqspectrum',
              'eqsavepreset', 'eqimportapo', 'eqexportapo', 'openroom', 'firsafe', 'monreset']:
        check('dsp', len(dact(a)) == 1, '动作 %s 上报一次' % a)
    check('dsp', [p.get('i') for p in dact('rksel')] == [0, 2], '机架行选中上报 = %r' % dact('rksel'))
    bps = dact('bypass')
    check('dsp', len(bps) == 2 and bps[0].get('on') is True and bps[1].get('on') is False,
          '总旁路开→关两条 = %r' % bps)
    check('dsp', kinds.count('pause') == 1 and kinds.count('next') == 1
                 and kinds.count('prev') == 1 and kinds.count('exit') == 1,
          '播放/上一首/下一首/返回各一次')
    sk = [p for p in posted if p.get('kind') == 'seek']
    check('dsp', len(sk) == 1 and abs(sk[0].get('position', 0) - 113.5) < 0.6,
          '拖进度条到中点 = %r' % (sk[0].get('position') if sk else None))
    vm = [p for p in posted if p.get('kind') == 'volume']
    check('dsp', len(vm) == 1 and abs(vm[0].get('value', 0) - 0.3) < 0.02,
          '拖音量到 30%% = %r' % (vm[0].get('value') if vm else None))

print()
if fails:
    print('失败 %d 项：' % len(fails))
    for f in fails:
        print('  - ' + f)
    sys.exit(1)
print('全部断言通过。')
