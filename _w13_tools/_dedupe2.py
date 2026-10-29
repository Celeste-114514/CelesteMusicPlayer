import re, os

TYPES = ['Window','FrameworkElement','BitmapImage']
FILES = ['LegacyWinUiShimMembers.cs','LegacyWinUiShimStubs.cs','LegacyWinUiShimTypes2.cs','LegacyWinUiShimFixes.cs','LegacyWinUiShimExtras.cs']

def split_blocks(text):
    """返回 [(ns, start, end)] 按顶层 namespace 划分"""
    blocks=[]
    for m in re.finditer(r'(?m)^namespace ([\w\.]+)\n\{\n', text):
        ns=m.group(1); i=text.find('{', m.start()); depth=0; j=i
        while j < len(text):
            if text[j]=='{': depth+=1
            elif text[j]=='}':
                depth-=1
                if depth==0: break
            j+=1
        blocks.append((ns, m.start(), j+1))
    return blocks

for f in FILES:
    p='CelesteMusicPlayer/'+f
    if not os.path.exists(p): continue
    text=open(p,encoding='utf-8').read()
    removed=0
    for guard in range(200):
        blocks=split_blocks(text)
        hit=False
        for ns, s0, s1 in blocks:
            if ns != 'Microsoft.UI.Xaml':   # 只保留 Xaml / 其他正确命名空间里的定义
                body=text[s0:s1]
                for t in TYPES:
                    m=re.search(r'(?m)^    public (?:sealed )?partial class %s\b'%re.escape(t), body)
                    if m:
                        # 删除该类块
                        abs_start=s0+m.start()
                        i=text.find('{', abs_start); depth=0; j=i
                        while j < len(text):
                            if text[j]=='{': depth+=1
                            elif text[j]=='}':
                                depth-=1
                                if depth==0: break
                            j+=1
                        text=text[:abs_start]+text[j+1:]
                        removed+=1; hit=True
                        break
                if hit: break
        if not hit: break
    open(p,'w',encoding='utf-8').write(text)
    print(f, '删除', removed)
