"""去掉补录文件里与主 shim 重复的类型/成员（可重复运行）。"""
import re, collections, sys

MAIN = ['CelesteMusicPlayer/LegacyWinUiShim.cs', 'CelesteMusicPlayer/LegacyWinUiShimExtras.cs']
EXTRA = 'CelesteMusicPlayer/LegacyWinUiShimMembers.cs'

main_src = ''.join(open(p, encoding='utf-8').read() for p in MAIN)

# 收集主 shim 里每个类型的成员名
have = collections.defaultdict(set)
for m in re.finditer(r'(?:public|internal)\s+(?:sealed\s+)?(?:static\s+)?partial class ([\w]+)\s*\{(.*?)\n    \}', main_src, re.S):
    ty, body = m.group(1), m.group(2)
    for mm in re.finditer(r'(?:public|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?[\w\?<>\.\[\]]+\s+([\w]+)\s*(?:\{|=>|;)', body):
        have[ty].add(mm.group(1))

lines = open(EXTRA, encoding='utf-8').read().split('\n')
out = []
cur = None
removed = 0
for ln in lines:
    m = re.match(r'    public partial class ([\w]+)', ln)
    if m:
        cur = m.group(1)
        out.append(ln)
        continue
    m2 = re.search(r'(?:public\s+)(?:static\s+)?(?:readonly\s+)?[\w\?<>\.\[\]]+?\s+([\w]+)\s*(?:\(|=>|;)', ln)
    if cur and m2 and m2.group(1) in have.get(cur, set()):
        removed += 1
        continue
    out.append(ln)
open(EXTRA, 'w', encoding='utf-8').write('\n'.join(out))
print('去重成员', removed)

# Types 里的冲突类：Application（shim 已有）、Controls.FrameworkElement（与 Xaml 同名会歧义）
p = 'CelesteMusicPlayer/LegacyWinUiShimTypes.cs'
s = open(p, encoding='utf-8').read()
s = re.sub(r'    public static class Application\n    \{.*?\n    \}\n', '', s, flags=re.S)
s = s.replace('''namespace Microsoft.UI.Xaml.Controls
{
    public enum ContentDialogButton''', '''namespace Microsoft.UI.Xaml.Controls
{
    public enum ContentDialogButton''')
open(p, 'w', encoding='utf-8').write(s)
print('types cleaned')
