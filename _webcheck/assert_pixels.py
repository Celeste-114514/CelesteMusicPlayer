#!/usr/bin/env python3
"""截图像素级抽检：验证两个详情页的关键视觉元素落在该在的位置。
   窗口 1660×980：顶栏 44 + 页头 98 → 主内容从 y=142 起；侧栏 228；
   view 左右 padding 28。covS(k) 的 SVG 中心有个 opacity .22 的白圆，
   所以封面/头像要撇开中心点取样。"""
import os
import sys
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))

def px(img, x, y):
    return img.getpixel((x, y))[:3]

def near(c1, c2, tol=30):
    return all(abs(a - b) <= tol for a, b in zip(c1, c2))

fails = []
def check(name, cond, msg):
    if not cond:
        fails.append('%s: %s' % (name, msg))
    print('  [%s] %s' % ('OK ' if cond else 'FAIL', msg))

def count_band(img, y0, y1, target, tol=10):
    """[y0,y1) 里目标色像素数（选中带是一整行 62px，数量级几百；
    抗锯齿杂点只有一两个，用计数区分）"""
    return count_band_x(img, 240, img.size[0] - 20, y0, y1, target, tol)

def count_band_x(img, x0, x1, y0, y1, target, tol=10):
    """[x0,x1)×[y0,y1) 里目标色像素数（限定横向范围，验证内容落没落错栏）"""
    n = 0
    for y in range(y0, y1):
        for x in range(x0, x1, 6):
            c = img.getpixel((x, y))[:3]
            if near(c, target, tol):
                n += 1
    return n

def count_colored(img, x0, x1, y0, y1, tol=60):
    """区域内彩色像素数（封面图 vs 白底）"""
    n = 0
    for y in range(y0, y1, 2):
        for x in range(x0, x1, 2):
            c = img.getpixel((x, y))[:3]
            if max(c) - min(c) > tol:
                n += 1
    return n

def count_dark(img, x0, x1, y0, y1, thresh=200):
    """区域内暗像素数（图标/文字 vs 纯色底）"""
    n = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            if max(img.getpixel((x, y))[:3]) < thresh:
                n += 1
    return n

def count_red(img, x0, x1, y0, y1, target=(0xFA, 0x24, 0x3C), tol=40):
    """区域内强调色像素数（激活胶囊是红底 pill）"""
    n = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            if near(img.getpixel((x, y))[:3], target, tol):
                n += 1
    return n

def count_heart(img, x0, x1, y0, y1):
    """红心像素：R 通道明显主导（强调色系，抗锯齿边缘仍是红通道领先）"""
    n = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            r, g, b = img.getpixel((x, y))[:3]
            if r > 140 and r - g > 80 and r - b > 80:
                n += 1
    return n

print('== songs（基线）')
im = Image.open(os.path.join(HERE, 'shot_songs.png')).convert('RGB')
check('songs', (im.size == (1660, 980)), '尺寸 %s' % (im.size,))
# 第 3 行（index 2，正在播）选中带：内容 142 起，thead 26+4 → 行0 顶 172，
# 行2 = 172 + 62*2 = 296..356
check('songs', count_band(im, 296, 357, (0xFD, 0xEC, 0xF0)) > 50,
      '正在播行（第 3 行）有选中底色（%d px）' % count_band(im, 296, 357, (0xFD, 0xEC, 0xF0)))
check('songs', count_band(im, 172, 233, (0xFD, 0xEC, 0xF0)) < 10,
      '第 1 行无选中底色（高亮没跑偏，%d px）' % count_band(im, 172, 233, (0xFD, 0xEC, 0xF0)))
# 播放条在底部 90px：y=980-45 应为 #F1F1F6
check('songs', near(px(im, 830, 980 - 45), (0xF1, 0xF1, 0xF6), 12), '底部播放条底色')

print('== albdetail（专辑详情左栏大封面）')
im = Image.open(os.path.join(HERE, 'shot_albdetail.png')).convert('RGB')
# 左栏封面：x=228+28=256 起，232px；y=142 起。撇开中心白圆取样（+70,+70）
c = px(im, 256 + 116 + 70, 142 + 116 + 70)
check('albdetail', near(c, (0x8e, 0x44, 0xad), 40),
      '左栏大封面（撇开中心圆）#8e44ad，实取 %s' % (c,))
# 右栏曲目区在封面右侧：x>520 应为白底卡片
check('albdetail', min(px(im, 900, 400)) > 240, '右栏曲目区浅色底')
# 正在播行（cur=1）选中带应在右栏：行0 顶 142+26+4=172，行1 = 234..295
check('albdetail', count_band(im, 234, 296, (0xFD, 0xEC, 0xF0)) > 50,
      '右栏第 2 行（cur=1）有选中底色（%d px）' % count_band(im, 234, 296, (0xFD, 0xEC, 0xF0)))

print('== artdetail（艺术家详情：左上头像 + 左下歌曲独立滚轮 + 右栏专辑墙跨两行）')
im = Image.open(os.path.join(HERE, 'shot_artdetail.png')).convert('RGB')
# 左栏圆形头像 160px：x=256 起，y=142 起，中心 (336,222)。
# artistavatar 把头像换成了 covS(9)=#8e44ad——撇开中心圆取样
c = px(im, 336 + 55, 222 + 55)
check('artdetail', near(c, (0x8e, 0x44, 0xad), 40),
      '左栏大头像已是网络头像色 #8e44ad，实取 %s' % (c,))
# 圆外四角应为白（border-radius:50% + overflow:hidden）
for dx, dy, tag in [(-62, -62, '左上'), (62, -62, '右上'), (-62, 62, '左下'), (62, 62, '右下')]:
    cc = px(im, 336 + dx, 222 + dy)
    check('artdetail', min(cc) > 240, '头像%s圆外为白底 %s' % (tag, cc))
# 2026-10-09 用户拍板改版：歌曲列表在歌手信息**下方**（左栏，不再是中栏），
# 右栏专辑墙跨两行、独立滚轮。左栏 x=256..549，右栏 x=580..1632。
# 正在播第 1 行（cur=0）选中带应落在左栏：头像 142+160 → 段头~30 → 表头 26+4
# → 行0 顶实测 y=432..492（探针定标）
n = count_band_x(im, 256, 549, 432, 492, (0xFD, 0xEC, 0xF0))
check('artdetail', n > 50, '歌曲列表第 1 行选中带落在左栏（%d px）' % n)
# 左栏里不能出现中栏 430px 时代的行带：x>560 的同一 y 区间无选中底色（歌曲没跑右栏去）
n2 = count_band_x(im, 560, 1632, 432, 492, (0xFD, 0xEC, 0xF0))
check('artdetail', n2 < 10, '歌曲行没有残留到右栏（%d px）' % n2)
# 右栏专辑卡（132px）：x=580 起，跨两行铺到内容底部
n3 = count_colored(im, 580, 1660, 142, 900)
check('artdetail', n3 > 500, '右栏专辑卡有封面图（%d 彩色像素）' % n3)
# 两栏之间的 24px 间隙（x=556..580）应为白：歌曲段和专辑段各滚各的
gap_white = all(min(px(im, x, y)) > 240 for x in (560, 568, 576) for y in range(150, 900, 50))
check('artdetail', gap_white, '两栏间隙为白（歌曲段与专辑段互不侵占）')

print('== plwalldetail（播放列表详情：顶栏按钮 + 曲目行 + 正在播行按路径高亮）')
im = Image.open(os.path.join(HERE, 'shot_plwalldetail.png')).convert('RGB')
# 顶栏（pldTop）：返回钮 + 单名 + 右侧动作钮——整行有文字/按钮暗像素
nd = count_dark(im, 256, 1400, 142, 180)
check('plwalldetail', nd > 100, '详情顶栏有文字/按钮（%d px）' % nd)
# 曲目行：第 2 行（path 命中，正在播）选中带 #FDECF0。行0 顶≈142+30(顶栏)+12+44≈226，
# 行1 = 318..362（探针定标；plmsg 横幅在 229..260，底色 (249,238,240) 与选中带
# (253,236,240) 差 <tol，靠 y 区间错开）
n = count_band(im, 318, 362, (0xFD, 0xEC, 0xF0))
check('plwalldetail', n > 50, '正在播行（第 2 行，按 path 高亮）有选中底色（%d px）' % n)
n0 = count_band(im, 274, 318, (0xFD, 0xEC, 0xF0))
check('plwalldetail', n0 < 10, '第 1 行无选中底色（高亮没跑偏，%d px）' % n0)
# 红心（fav 行）：第 2 行 fav 列在行尾 x≈1590..1619——红通道主导即算
nr = count_heart(im, 1550, 1632, 318, 362)
check('plwalldetail', nr > 5, '收藏行有实心红心（%d px）' % nr)

print('== plwall（播放列表墙：卡片封面 + 名字/首数 + 墙头新建钮）')
im = Image.open(os.path.join(HERE, 'shot_plwall.png')).convert('RGB')
# 卡片是 150px 网格：x=256 起、y=142+墙头~36 起。前两张卡有封面（covS(4)/covS(5)，
# 回墙后重新预热补推）
n = count_colored(im, 256, 1660, 185, 900)
check('plwall', n > 200, '墙上有封面卡片（%d 彩色像素）' % n)
# 墙头「＋ 新建播放列表」是红底白字 primary 钮（白字数不到，改数强调色底）
nr = count_red(im, 1300, 1632, 142, 180)
check('plwall', nr > 100, '墙头新建钮是强调色底（%d px）' % nr)
# 墙头左侧「N 个播放列表」汇总文字（x≈257..328）
nd = count_dark(im, 257, 330, 142, 180)
check('plwall', nd > 0, '墙头汇总文字（%d px）' % nd)


# 标签排序三个截图的布局坐标来自同参数探针（--window-size=1660,980 --hide-scrollbars）：
# 内容 x=256 起；tsbar y142..170；tspTop y184..214；tsmodes y226..254；
# 曲目行 y290..334 / 334..378 / 378..422（第 3 行正在播）。
print('== tagsort（标签排序曲目视角：播放行选中带 + 搜索框隐藏 + 视角胶囊激活）')
im = Image.open(os.path.join(HERE, 'shot_tagsort.png')).convert('RGB')
check('tagsort', (im.size == (1660, 980)), '尺寸 %s' % (im.size,))
check('tagsort', count_band(im, 378, 423, (0xFD, 0xEC, 0xF0)) > 50,
      '正在播行（第 3 行）有选中底色（%d px）' % count_band(im, 378, 423, (0xFD, 0xEC, 0xF0)))
check('tagsort', count_band(im, 290, 334, (0xFD, 0xEC, 0xF0)) < 10,
      '第 1 行无选中底色（%d px）' % count_band(im, 290, 334, (0xFD, 0xEC, 0xF0)))
# 搜索框隐藏：顶栏里搜索框原位置（x298..538, y9..35）应无图标/占位符暗像素
nd = count_dark(im, 298, 538, 9, 35)
check('tagsort', nd == 0, '顶栏搜索框位置无暗像素（已隐藏，%d px）' % nd)
ims = Image.open(os.path.join(HERE, 'shot_songs.png')).convert('RGB')
nds = count_dark(ims, 298, 538, 9, 35)
check('tagsort', nds > 10, '对照：歌曲页搜索框位置有暗像素（图标+占位符，%d px）' % nds)
# 面板标题「流派：流行」（tstitle x320, y189..209）
check('tagsort', count_dark(im, 320, 520, 190, 208) > 10,
      '面板标题有文字（%d px）' % count_dark(im, 320, 520, 190, 208))
# 视角胶囊「曲目」激活 = 红底 pill（tsmodes 第一个胶囊 x256 起）
check('tagsort', count_red(im, 258, 330, 228, 252) > 100,
      '「曲目」视角胶囊激活为强调色（%d px）' % count_red(im, 258, 330, 228, 252))

print('== tagsortsort（排序方式视角：激活预设红 pill + 未激活浅灰 + 状态文本）')
im = Image.open(os.path.join(HERE, 'shot_tagsortsort.png')).convert('RGB')
# 激活预设「流派 / 专辑」x713..801, y294..322
check('tagsortsort', count_red(im, 715, 799, 296, 320) > 100,
      '「流派 / 专辑」预设胶囊激活为强调色（%d px）' % count_red(im, 715, 799, 296, 320))
# 未激活预设「专辑」（行内第一个，x≈256 起）应是浅灰 pill 底
c = px(im, 270, 308)
check('tagsortsort', near(c, (0xE7, 0xE7, 0xEC), 25),
      '未激活预设是浅灰底（%s）' % (c,))
# 状态文本在视角区底部（tssort y266..428）
check('tagsortsort', count_dark(im, 256, 816, 390, 425) > 20,
      '状态文本有文字（%d px）' % count_dark(im, 256, 816, 390, 425))

print('== tagsortgroup（分组浏览：三级树缩进渲染 + 收起组头）')
im = Image.open(os.path.join(HERE, 'shot_tagsortgroup.png')).convert('RGB')
# 组区有内容（tsgrows y294..476）
check('tagsortgroup', count_dark(im, 256, 900, 294, 476) > 200,
      '分组行有文字（%d px）' % count_dark(im, 256, 900, 294, 476))
# 歌曲行（depth=3，缩进 66px）：标题单元格 x=322, y449..466——行首暗像素应落在 322 附近
first = None
for x in range(256, 500):
    col_dark = False
    for y in range(449, 466):
        if max(im.getpixel((x, y))[:3]) < 200:
            col_dark = True
            break
    if col_dark:
        first = x
        break
check('tagsortgroup', first is not None and 315 <= first <= 340,
      '歌曲行标题从缩进 66px 处起始（x=%r，期望 322 附近）' % first)

# ── dsp（音效处理网页）──────────────────────────────────────────────
# 定标（--window-size=1660,980）：左导航 x12..236；ni_5 选中带 y315..346；
# 状态 pill（hi）y107..127 x256..324；频段 chip 0（on）y370..397 x256..290；
# EQ 电源开关（checked=红）x1326..1364 y66..88；活跃绿点 x19..25 y361..367。
# ⚠ 导航底色 #F5F5F8 与选中色 #FDECF0 只差 8~9，tol=10 会误判——这里一律 tol=6。
def count_sel(img, x0, x1, y0, y1, tol=6):
    n = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            if near(img.getpixel((x, y))[:3], (0xFD, 0xEC, 0xF0), tol):
                n += 1
    return n

def count_ok(img, x0, x1, y0, y1):
    n = 0
    for y in range(y0, y1):
        for x in range(x0, x1):
            if near(img.getpixel((x, y))[:3], (0x3B, 0x6D, 0x11), 30):
                n += 1
    return n

print('== dsp（音效处理网页：导航选中带/活跃绿点/EQ 电源红/状态 pill/频段 chip/播放条封面）')
im = Image.open(os.path.join(HERE, 'shot_dsp.png')).convert('RGB')
check('dsp', (im.size == (1660, 980)), '尺寸 %s' % (im.size,))
check('dsp', near(px(im, 830, 22), (0xF1, 0xF1, 0xF6), 12), '顶栏底色')
check('dsp', near(px(im, 830, 935), (0xF1, 0xF1, 0xF6), 12), '底部播放条底色')
# 左导航选中项（ni.on）整块选中底色——一列只有一项，32px 高
n = count_sel(im, 12, 236, 310, 352)
check('dsp', n > 1500, '导航选中项（参数 EQ）有选中底色（%d px）' % n)
# 选中项左侧小圆点是强调色红（.ni.on .dot）
check('dsp', count_red(im, 14, 30, 315, 346) > 10, '导航选中项圆点为强调色')
# 活跃模块绿点（压缩，navDots[6]=True）：7px 圆 ≈ 38px
check('dsp', count_ok(im, 12, 40, 350, 375) > 15,
      '压缩模块导航圆点为活跃绿（%d px）' % count_ok(im, 12, 40, 350, 375))
# EQ 电源开关（接通=强调色底）；右侧耳机校正开关（禁用=不上色）不能红
check('dsp', count_red(im, 1300, 1380, 60, 95) > 200,
      '页头 EQ 电源开关是强调色底（%d px）' % count_red(im, 1300, 1380, 60, 95))
check('dsp', count_red(im, 1450, 1660, 60, 95) < 20,
      '耳机校正开关未上色（禁用态，%d px）' % count_red(im, 1450, 1660, 60, 95))
# 快捷条状态 pill（hi = 选中底）+ 频段 chip 0（选中）
check('dsp', count_sel(im, 256, 340, 105, 130) > 200,
      '快捷条「EQ 生效中」pill 有选中底色（%d px）' % count_sel(im, 256, 340, 105, 130))
check('dsp', count_sel(im, 250, 300, 365, 400) > 150,
      '第 1 频段 chip 选中底色（%d px）' % count_sel(im, 250, 300, 365, 400))
# 播放条封面（covS(0) #c0392b）撇开中心白圆取样
check('dsp', near(px(im, 24, 918), (0xc0, 0x39, 0x2b), 40), '播放条封面渲染')
# 内容区有文字/控件（暗像素）
check('dsp', count_dark(im, 256, 1600, 150, 500) > 500,
      'EQ 页内容有文字/控件（%d px）' % count_dark(im, 256, 1600, 150, 500))

print()
if fails:
    print('失败 %d 项：' % len(fails))
    for f in fails:
        print('  - ' + f)
    sys.exit(1)
print('像素抽检全部通过。')
