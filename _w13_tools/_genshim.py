import re, collections, glob

src = open('_legacy_xaml/MainWindow.xaml.legacy', encoding='utf-8').read()

# 1) x:Name -> 元素标签
pairs = []
for m in re.finditer(r'<([A-Za-z_][\w\.]*)((?:"[^"]*"|\'[^\']*\'|[^>"\'])*)>', src):
    tag, attrs = m.group(1), m.group(2)
    nm = re.search(r'x:Name="([^"]+)"', attrs)
    if nm:
        pairs.append((nm.group(1), tag.split('.')[-1]))

# 2) 代码里实际用到的 Microsoft.UI.Xaml 类型名
types = collections.Counter()
tok = re.compile(r'Microsoft\.UI\.Xaml(?:\.[\w]+)*\.([A-Z][\w]*)')
for f in glob.glob('MainWindow*.cs'):
    t = open(f, encoding='utf-8', errors='ignore').read()
    for m in tok.finditer(t):
        types[m.group(1)] += 1
# XAML 标签自身也算
for _, tag in pairs:
    types[tag] += 1

names = {}
for n, t in pairs:
    names.setdefault(n, t)

print('控件字段:', len(names), ' 涉及类型:', len(types))
print(sorted(names.items())[:8])
with open('_shim_names.txt', 'w', encoding='utf-8') as f:
    for n, t in sorted(names.items()):
        f.write('%s\t%s\n' % (n, t))
with open('_shim_types.txt', 'w', encoding='utf-8') as f:
    for t, c in types.most_common():
        f.write('%s\t%d\n' % (t, c))
print('类型 TOP30:', [t for t, _ in types.most_common(30)])
