import re, os

MAIN='CelesteMusicPlayer/LegacyWinUiShim.cs'
FILES=['LegacyWinUiShimMembers.cs','LegacyWinUiShimStubs.cs','LegacyWinUiShimTypes2.cs','LegacyWinUiShimFixes.cs','LegacyWinUiShimExtras.cs']

main=open(MAIN,encoding='utf-8').read()

def ns_of(text, cls):
    for m in re.finditer(r'(?m)^namespace ([\w\.]+)\n\{\n', text):
        i=text.find('{', m.start()); depth=0; j=i
        while j < len(text):
            if text[j]=='{': depth+=1
            elif text[j]=='}':
                depth-=1
                if depth==0: break
            j+=1
        body=text[m.start():j]
        if re.search(r'class %s\b'%re.escape(cls), body):
            return m.group(1)
    return None

def drop_in(text, cls, keep_ns):
    out=text; removed=0
    for guard in range(100):
        blocks=[]
        for m in re.finditer(r'(?m)^namespace ([\w\.]+)\n\{\n', out):
            i=out.find('{', m.start()); depth=0; j=i
            while j < len(out):
                if out[j]=='{': depth+=1
                elif out[j]=='}':
                    depth-=1
                    if depth==0: break
                j+=1
            blocks.append((m.group(1), m.start(), j))
        hit=False
        for ns, s0, s1 in blocks:
            if ns==keep_ns: continue
            body=out[s0:s1]
            m=re.search(r'(?m)^    public (?:sealed )?partial class %s\b'%re.escape(cls), body)
            if m:
                a=s0+m.start()
                i=out.find('{', a); depth=0; j=i
                while j < len(out):
                    if out[j]=='{': depth+=1
                    elif out[j]=='}':
                        depth-=1
                        if depth==0: break
                    j+=1
                out=out[:a]+out[j+1:]; removed+=1; hit=True; break
        if not hit: break
    return out, removed

types=set()
err=open('_w13_err38.txt',encoding='utf-8',errors='ignore').read()
for m in re.finditer(r'error CS0104: “(\w+)”是', err):
    types.add(m.group(1))
print('歧义类型', sorted(types))

for cls in types:
    keep = ns_of(main, cls)
    if not keep:
        print('  主 shim 无此类型，跳过:', cls); continue
    for f in FILES:
        p='CelesteMusicPlayer/'+f
        if not os.path.exists(p): continue
        t=open(p,encoding='utf-8').read()
        t2,n=drop_in(t, cls, keep)
        if n: open(p,'w',encoding='utf-8').write(t2); print('  %s 在 %s 删除 %d'%(cls,f,n))
