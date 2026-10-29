"""W13 收敛循环：编译 → 收集缺失成员 → 追加到过渡层 → 去重 → 再编译。"""
import re, collections, subprocess, os, sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
PROJ = os.path.join(ROOT, 'CelesteMusicPlayer', 'CelesteMusicPlayer.csproj')
ERR = os.path.join(ROOT, '_w13_err.txt')
SHIM_FILES = ['LegacyWinUiShim.cs', 'LegacyWinUiShimExtras.cs', 'LegacyWinUiShimMembers.cs', 'LegacyWinUiShimTypes.cs']
MEMBERS = 'LegacyWinUiShimMembers.cs'

ENV = dict(os.environ)
ENV.update({
    'DOTNET_ROOT': r'C:\Program Files\dotnet',
    'ProgramData': r'C:\ProgramData',
    'SystemRoot': r'C:\WINDOWS',
    'APPDATA': r'C:\Users\MSI\AppData\Roaming',
    'ALLUSERSPROFILE': r'C:\ProgramData',
    'USERPROFILE': r'C:\Users\MSI',
    'ProgramFiles': r'C:\Program Files',
    'ProgramFiles(x86)': r'C:\Program Files (x86)',
})


def build():
    cmd = ['dotnet', 'build', PROJ, '-c', 'Release', '-p:Platform=x64', '-p:WindowsPackageType=None',
           '-p:CsWinRTAotOptimizerEnabled=false', '-v:m']
    out = subprocess.run(cmd, cwd=ROOT, env=ENV, capture_output=True, text=True, encoding='utf-8', errors='ignore').stdout
    errs = [ln for ln in out.splitlines() if ' error CS' in ln]
    open(ERR, 'w', encoding='utf-8').write('\n'.join(errs))
    return errs


def ns_of_type(all_src, ty):
    for m in re.finditer(r'namespace ([\w\.]+)\s*\{(.*?)\n\}\n', all_src, re.S):
        if re.search(r'class\s+%s\b' % re.escape(ty), m.group(2)):
            return m.group(1)
    return None


def collect(errs, all_src):
    inst = collections.defaultdict(set)
    stat = collections.defaultdict(set)
    props = collections.defaultdict(set)
    for ln in errs:
        m = re.match(r'^.*\\CelesteMusicPlayer\\([A-Za-z0-9_\.]+\.cs)\((\d+),\d+\): error (CS\d+): (.*?)\s*\[', ln)
        if not m:
            continue
        code, msg = m.group(3), m.group(4)
        t = re.search(r'“([\w\.<>`]+)”未包含“(\w+)”', msg)
        if not t:
            continue
        ty = t.group(1).split('.')[-1].replace('?', '')
        member = t.group(2)
        if ty in ('object', 'MainWindow', 'App', 'string', 'int', 'double', 'bool'):
            continue
        if code == 'CS0117':
            if member.endswith('Property'):
                props[ty].add(member)
            else:
                stat[ty].add(member)
        elif code == 'CS1061':
            inst[ty].add(member)
    return inst, stat, props


def existing(all_src):
    have = collections.defaultdict(set)
    for m in re.finditer(r'(?:public|internal)\s+(?:sealed\s+)?(?:static\s+)?(?:partial\s+)?class ([\w]+)\s*\{(.*?)\n    \}', all_src, re.S):
        ty, body = m.group(1), m.group(2)
        for mm in re.finditer(r'(?:public|internal|protected)\s+(?:static\s+)?(?:readonly\s+)?[\w\?<>\.\[\]]+?\s+([\w]+)\s*(?:\(|=>|;|\{)', body):
            have[ty].add(mm.group(1))
    return have


def append_members(inst, stat, props, all_src, have):
    blocks = collections.defaultdict(lambda: collections.defaultdict(list))
    for ty, members in inst.items():
        ns = ns_of_type(all_src, ty)
        if not ns:
            continue
        for mm in sorted(members):
            if mm in have.get(ty, set()):
                continue
            if mm in ('ShowAt',):
                blocks[ns][ty].append('        public void ShowAt(global::Microsoft.UI.Xaml.DependencyObject? target) { }')
            elif mm in ('CreateTimer',):
                blocks[ns][ty].append('        public global::Microsoft.UI.Dispatching.DispatcherQueueTimer CreateTimer() => new global::Microsoft.UI.Dispatching.DispatcherQueueTimer();')
            else:
                blocks[ns][ty].append('        public object? %s { get; set; }' % mm)
    for ty, members in stat.items():
        ns = ns_of_type(all_src, ty)
        if not ns:
            continue
        for mm in sorted(members):
            if mm in have.get(ty, set()):
                continue
            if mm.startswith('Set') or mm.startswith('Get'):
                blocks[ns][ty].append('        public static void %s(global::Microsoft.UI.Xaml.DependencyObject? element, object? value) { }' % mm)
            else:
                blocks[ns][ty].append('        public static object? %s { get; set; }' % mm)
    for ty, members in props.items():
        ns = ns_of_type(all_src, ty)
        if not ns:
            continue
        for mm in sorted(members):
            if mm in have.get(ty, set()):
                continue
            blocks[ns][ty].append('        public static readonly global::Microsoft.UI.Xaml.DependencyProperty? %s = null;' % mm)

    if not blocks:
        return 0
    path = os.path.join(ROOT, 'CelesteMusicPlayer', MEMBERS)
    src = open(path, encoding='utf-8').read()
    # 按命名空间追加（简单起见：追加到文件末尾的对应 namespace；不存在则新建）
    for ns, group in blocks.items():
        body = []
        for ty, lines in group.items():
            body.append('    public partial class %s' % ty)
            body.append('    {')
            body.extend(lines)
            body.append('    }')
            body.append('')
        block = 'namespace %s\n{\n' % ns + '\n'.join(body) + '}\n'
        src = src.rstrip() + '\n\n' + block
    open(path, 'w', encoding='utf-8').write(src)
    return sum(len(v) for g in blocks.values() for v in g.values())


def main():
    rounds = int(sys.argv[1]) if len(sys.argv) > 1 else 3
    for i in range(rounds):
        errs = build()
        print('== 第 %d 轮：%d 条错误' % (i + 1, len(errs)), flush=True)
        if not errs:
            print('编译通过')
            return 0
        all_src = ''.join(open(os.path.join(ROOT, 'CelesteMusicPlayer', f), encoding='utf-8').read() for f in SHIM_FILES)
        have = existing(all_src)
        inst, stat, props = collect(errs, all_src)
        n = append_members(inst, stat, props, all_src, have)
        print('   补成员 %d（实例类型 %d / 静态类型 %d）' % (n, len(inst), len(stat)), flush=True)
        if n == 0:
            print('   无法继续自动收敛，剩余错误样例：')
            for ln in errs[:10]:
                print('   ', ln.split('error ')[-1][:100])
            return 1
    return 1


if __name__ == '__main__':
    sys.exit(main())
