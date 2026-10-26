"""Run the TagEditTests project with a complete environment.

The bash sandbox strips a batch of Windows variables, which makes NuGet's static
initialiser throw "Value cannot be null (Parameter 'path1')". Same workaround as
_wb_build.py: rebuild the environment explicitly.

usage: python _wb_test.py [filter] [tries]
       python _wb_test.py "FullyQualifiedName~WebDspTagRulesTests|FullyQualifiedName~WebTagWriteTests"
"""
import os, re, subprocess, sys, time

BASE = r'C:\Users\MSI\source\repos\CelesteMusicPlayer'
PROJ = r'tools\TagEditTests\TagEditTests.csproj'
LOG = os.path.join(BASE, '_test_tagedit.log')


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


def main():
    flt = sys.argv[1] if len(sys.argv) > 1 else ''
    tries = int(sys.argv[2]) if len(sys.argv) > 2 else 3
    args = ['dotnet', 'test', PROJ, '-c', 'Release', '-p:Platform=x64',
            '--nologo', '-v', 'q']
    if flt:
        args += ['--filter', flt]
    for i in range(1, tries + 1):
        t0 = time.time()
        with open(LOG, 'wb') as f:
            p = subprocess.run(args, cwd=BASE, env=env(), stdout=f, stderr=subprocess.STDOUT)
        txt = open(LOG, 'rb').read().decode('utf-8', 'replace')
        cs = len(re.findall(r'error CS\d+', txt))
        # dotnet test localises its summary line (Chinese here), so accept both spellings.
        failed = (re.search(r'Failed!\s*-\s*Failed:\s*(\d+)', txt)
                  or re.search(r'失败!\s*-\s*失败:\s*(\d+)', txt))
        passed = (re.search(r'Passed!\s*-\s*Failed:\s*(\d+),\s*Passed:\s*(\d+)', txt)
                  or re.search(r'已通过!\s*-\s*失败:\s*(\d+)[，,]\s*通过:\s*(\d+)', txt))
        print('attempt %d/%d exit=%d %.0fs errorCS=%d' % (i, tries, p.returncode, time.time() - t0, cs))
        if cs:
            for m in re.finditer(r'([^\s:]*\.cs)\((\d+),(\d+)\): (error CS\d+: [^\n\r]*)', txt):
                print('  %s(%s): %s' % (m.group(1).split('\\')[-1], m.group(2), m.group(4)[:170]))
            return 2
        if passed:
            print('ALL_PASSED  passed=%s failed=%s' % (passed.group(2), passed.group(1)))
            print('-' * 60)
            print(txt.strip()[-1200:])
            return 0
        if failed:
            print('HAS_FAILURES failed=%s' % failed.group(1))
            print(_failures(txt))
            return 1
        # environment noise (MSBuild crash / XamlCompiler) -> retry
        tail = '\n'.join(txt.strip().splitlines()[-4:])
        print('  [环境问题/无结果] %s' % tail[:400])
    print('EXHAUSTED')
    return 3


def _summary(txt):
    out = []
    for m in re.finditer(r'^\s*(Passed|通过)\s+(\S+)', txt, re.M):
        out.append('  ok   ' + m.group(2))
    return '\n'.join(out) or txt.strip()[-1500:]


def _failures(txt):
    out = []
    for m in re.finditer(r'^\s*(Failed|失败)\s+(\S+)(.*)$', txt, re.M):
        out.append('  FAIL ' + m.group(2) + m.group(3)[:120])
    for m in re.finditer(r'(Assert\.[^\n\r]*|[^\n\r]*Exception[^\n\r]*)', txt):
        out.append('       ' + m.group(1).strip()[:170])
    return '\n'.join(out[:40]) or txt.strip()[-2500:]


if __name__ == '__main__':
    sys.exit(main())
