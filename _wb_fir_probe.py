"""Build and run the FIR convolution probe with a complete environment."""
import os, re, subprocess, sys

BASE = r'C:\Users\MSI\source\repos\CelesteMusicPlayer'
PROJ = r'tools\FirConvolutionProbe\FirConvolutionProbe.csproj'
IR = os.path.join(os.environ.get('TEMP', r'C:\Users\MSI\AppData\Local\Temp'),
                  'celeste-fieldtest', 'test-ir-2048.wav')


def env():
    e = dict(os.environ)
    e.update({
        'DOTNET_ROOT': r'C:\Program Files\dotnet',
        'APPDATA': r'C:\Users\MSI\AppData\Roaming',
        'SystemRoot': r'C:\WINDOWS', 'windir': r'C:\WINDOWS',
        'ProgramData': r'C:\ProgramData', 'ALLUSERSPROFILE': r'C:\ProgramData',
        'USERPROFILE': r'C:\Users\MSI',
        'ProgramFiles': r'C:\Program Files',
        'ProgramFiles(x86)': r'C:\Program Files (x86)',
        'CommonProgramFiles': r'C:\Program Files\Common Files',
        'CommonProgramW6432': r'C:\Program Files\Common Files',
        'TEMP': r'C:\Users\MSI\AppData\Local\Temp', 'TMP': r'C:\Users\MSI\AppData\Local\Temp',
        'MSYS_NO_PATHCONV': '1',
        'DOTNET_CLI_TELEMETRY_OPTOUT': '1', 'DOTNET_NOLOGO': '1',
    })
    e['PATH'] = r'C:\Program Files\dotnet;' + e.get('PATH', '')
    return e


def main():
    p = subprocess.run(['dotnet', 'build', PROJ, '-c', 'Release', '-p:Platform=x64', '--nologo', '-v', 'q'],
                       cwd=BASE, env=env(), capture_output=True)
    txt = (p.stdout + p.stderr).decode('utf-8', 'replace')
    if p.returncode != 0:
        print('BUILD_FAILED')
        for m in re.finditer(r'([^\s:]*\.cs)\((\d+),(\d+)\): (error [^\n\r]*)', txt):
            print('  %s(%s): %s' % (m.group(1).split('\\')[-1], m.group(2), m.group(4)[:180]))
        print(txt[-1200:])
        return 1
    print('build ok')
    exe = os.path.join(BASE, 'tools', 'FirConvolutionProbe', 'bin', 'x64', 'Release',
                       'net9.0-windows10.0.19041.0', 'FirConvolutionProbe.exe')
    if not os.path.exists(exe):
        print('missing exe: ' + exe)
        return 1
    run = subprocess.run([exe, IR], cwd=os.path.dirname(exe), env=env(), capture_output=True)
    print(run.stdout.decode('utf-8', 'replace'))
    err = run.stderr.decode('utf-8', 'replace')
    if err.strip():
        print('--- stderr ---')
        print(err[-1000:])
    print('EXIT=%d' % run.returncode)
    return run.returncode


if __name__ == '__main__':
    sys.exit(main())
