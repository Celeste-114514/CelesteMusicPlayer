import re, glob, collections

names = set(re.findall(r'x:Name="([^"]+)"', open('MainWindow.xaml', encoding='utf-8').read()))
print('x:Name 数:', len(names))

tok = re.compile(r'[A-Za-z_][A-Za-z0-9_]*')
files = glob.glob('MainWindow*.cs')
total = 0
per_file = collections.Counter()
for f in files:
    text = open(f, encoding='utf-8', errors='ignore').read()
    c = 0
    for ln in text.splitlines():
        words = set(tok.findall(ln))
        if words & names:
            c += 1
    if c:
        per_file[f] = c
    total += c
print('含控件名的代码行数(粗估):', total)
for f, c in per_file.most_common(20):
    print('  %5d  %s' % (c, f))
