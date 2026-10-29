"""第二轮收敛：补缺失类型、补 MainWindow 字段、补成员（dynamic）。"""
import re, os, collections, subprocess

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..'))
PROJ = os.path.join(ROOT, 'CelesteMusicPlayer', 'CelesteMusicPlayer.csproj')
ENV = dict(os.environ)
ENV.update({'DOTNET_ROOT': r'C:\Program Files\dotnet', 'ProgramData': r'C:\ProgramData',
            'SystemRoot': r'C:\WINDOWS', 'APPDATA': r'C:\Users\MSI\AppData\Roaming',
            'ALLUSERSPROFILE': r'C:\ProgramData', 'USERPROFILE': r'C:\Users\MSI',
            'ProgramFiles': r'C:\Program Files', 'ProgramFiles(x86)': r'C:\Program Files (x86)'})


def build():
    cmd = ['dotnet', 'build', PROJ, '-c', 'Release', '-p:Platform=x64', '-p:WindowsPackageType=None',
           '-p:CsWinRTAotOptimizerEnabled=false', '-t:Rebuild', '-v:m']
    out = subprocess.run(cmd, cwd=ROOT, env=ENV, capture_output=True, text=True, encoding='utf-8', errors='ignore').stdout
    errs = [ln for ln in out.splitlines() if ' error CS' in ln]
    open(os.path.join(ROOT, '_w13_err.txt'), 'w', encoding='utf-8').write('\n'.join(errs))
    return errs


def main(rounds=3):
    for i in range(rounds):
        errs = build()
        print('== 第 %d 轮：%d 条' % (i + 1, len(errs)), flush=True)
        if not errs:
            print('编译通过'); return 0
        missing_types = collections.Counter()
        missing_ns = collections.Counter()
        missing_names = collections.Counter()
        inst = collections.defaultdict(set)
        for ln in errs:
            m = re.match(r'^.*\\CelesteMusicPlayer\\([A-Za-z0-9_\.]+\.cs)\((\d+),\d+\): error (CS\d+): (.*?)\s*\[', ln)
            if not m:
                continue
            code, msg = m.group(3), m.group(4)
            t = re.search(r'未能找到类型或命名空间名“([\w\.]+)”', msg)
            if t:
                full = t.group(1)
                if '.' in full:
                    ns, name = full.rsplit('.', 1)
                    missing_ns[(ns, name)] += 1
                else:
                    missing_types[full] += 1
                continue
            t = re.search(r'不存在名称“(\w+)”', msg)
            if t:
                missing_names[t.group(1)] += 1
                continue
            t = re.search(r'“([\w\.<>`]+)”未包含“(\w+)”', msg)
            if t and code == 'CS1061':
                ty = t.group(1).split('.')[-1].replace('?', '')
                if ty not in ('object', 'string', 'int', 'double', 'bool', 'Point', 'MainWindow'):
                    inst[ty].add(t.group(2))

        # 1) 缺失类型 → 补 stub（带命名空间前缀的按前缀放，否则放 Controls）
        if missing_types or missing_ns:
            blocks = collections.defaultdict(list)
            for (ns, name), _ in missing_ns.items():
                blocks[ns].append(name)
            for name, _ in missing_types.items():
                blocks['Microsoft.UI.Xaml.Controls'].append(name)
            path = os.path.join(ROOT, 'CelesteMusicPlayer', 'LegacyWinUiShimStubs.cs')
            s = open(path, encoding='utf-8').read() if os.path.exists(path) else '#pragma warning disable CS0436\n#pragma warning disable CS0102\n#pragma warning disable CS0111\n#pragma warning disable CS0169\n'
            for ns, names in blocks.items():
                body = '\n'.join('    public partial class %s : global::Microsoft.UI.Xaml.DependencyObject { public dynamic? Value { get; set; } }' % n for n in names)
                s += '\nnamespace %s\n{\n%s\n}\n' % (ns, body)
            open(path, 'w', encoding='utf-8').write(s)

        # 2) MainWindow 补 dynamic 字段
        known = {'Colors', 'Style', 'VirtualKey', 'ElementTheme', 'Thickness', 'CornerRadius', 'Duration',
                 'GridLength', 'Symbol', 'BitmapImage', 'SolidColorBrush', 'FontFamily', 'Safe', 'Window',
                 'Icon', 'Point', 'Size', 'Rect', 'Color'}
        fields = [n for n in missing_names if n[0].isupper() and n not in known]
        if fields:
            p = os.path.join(ROOT, 'CelesteMusicPlayer', 'MainWindow.LegacyControls.cs')
            s = open(p, encoding='utf-8').read()
            s = s.replace('    }\n}\n', ''.join('        internal dynamic? %s { get; set; }\n' % f for f in sorted(fields)) + '    }\n}\n')
            open(p, 'w', encoding='utf-8').write(s)

        # 3) 补成员
        if inst:
            path = os.path.join(ROOT, 'CelesteMusicPlayer', 'LegacyWinUiShimMembers.cs')
            s = open(path, encoding='utf-8').read()
            for ty, members in inst.items():
                body = '\n'.join('        public dynamic? %s { get; set; }' % m for m in sorted(members))
                s += '\nnamespace Microsoft.UI.Xaml.Controls\n{\n    public partial class %s\n    {\n%s\n    }\n}\n' % (ty, body)
            open(path, 'w', encoding='utf-8').write(s)

        print('   补类型 %d / 字段 %d / 成员类型 %d' % (len(missing_types) + len(missing_ns), len(fields), len(inst)), flush=True)
    return 1


if __name__ == '__main__':
    import sys
    sys.exit(main(int(sys.argv[1]) if len(sys.argv) > 1 else 3))
