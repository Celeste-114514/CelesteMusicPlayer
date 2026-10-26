import os, subprocess, sys, time

BASE = r'C:\Users\MSI\source\repos\CelesteMusicPlayer'
PROJ = r'CelesteMusicPlayer\CelesteMusicPlayer.csproj'

def env():
    e = dict(os.environ)
    e.update({
        'DOTNET_ROOT': r'C:\Program Files\dotnet',
        'APPDATA': r'C:\Users\MSI\AppData\Roaming',
        'SystemRoot': r'C:\WINDOWS',
        'windir': r'C:\WINDOWS',
        'ProgramData': r'C:\ProgramData',
        'ALLUSERSPROFILE': r'C:\ProgramData',
        'USERPROFILE': r'C:\Users\MSI',
        'ProgramFiles': r'C:\Program Files',
        'ProgramFiles(x86)': r'C:\Program Files (x86)',
        'CommonProgramFiles': r'C:\Program Files\Common Files',
        'CommonProgramW6432': r'C:\Program Files\Common Files',
        'TEMP': r'C:\Users\MSI\AppData\Local\Temp',
        'TMP': r'C:\Users\MSI\AppData\Local\Temp',
        'MSYS_NO_PATHCONV': '1',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1',
        'DOTNET_NOLOGO': '1',
    })
    e['PATH'] = r'C:\Program Files\dotnet;' + e.get('PATH', '')
    return e

def run(args, log):
    with open(log, 'wb') as f:
        p = subprocess.run(args, cwd=BASE, env=env(), stdout=f, stderr=subprocess.STDOUT)
    return p.returncode

def diagnose(log):
    raw = open(log, 'rb').read()
    try: txt = raw.decode('utf-8', 'replace')
    except Exception: txt = raw.decode('mbcs', 'replace')
    import re
    cs = len(re.findall(r'error CS\d+', txt))
    msb = len(re.findall(r'error MSB\d+', txt))
    fatal = len(re.findall(r'Fatal error\.|Internal CLR error|error : ', txt))
    return cs, msb, fatal, txt

if __name__ == '__main__':
    mode = sys.argv[1] if len(sys.argv) > 1 else 'build'
    tries = int(sys.argv[2]) if len(sys.argv) > 2 else 4
    log = os.path.join(BASE, '_build_%s.log' % mode)
    args = ['dotnet', mode, PROJ, '-c', 'Release', '-p:Platform=x64']
    if mode == 'publish':
        args += ['-r', 'win-x64', '-p:CelesteSelfContainedDistribute=true']
    for i in range(1, tries + 1):
        t0 = time.time()
        ec = run(args, log)
        cs, msb, fatal, txt = diagnose(log)
        print('attempt %d/%d exit=%d  %.0fs  errorCS=%d MSB=%d fatal=%d' % (i, tries, ec, time.time() - t0, cs, msb, fatal))
        if cs == 0 and msb == 0 and fatal == 0 and ec == 0:
            print('BUILD_OK')
            break
        if cs > 0:
            print('--- 真编译错误（不重试，直接列出）---')
            import re
            seen = set()
            for m in re.finditer(r'([^\s:]*\.cs)\((\d+),(\d+)\): (error CS\d+: [^\n\r]*)', txt):
                key = m.group(1) + m.group(2)
                if key in seen: continue
                seen.add(key)
                print('  %s(%s,%s): %s' % (m.group(1).split('\\')[-1], m.group(2), m.group(3), m.group(4)[:180]))
            print('  合计不同报错位置: %d' % len(seen))
            break
        tail = '\n'.join(txt.strip().splitlines()[-6:])
        print('  [环境崩] %s' % tail[:400])
    else:
        print('EXHAUSTED')
