#!/usr/bin/env python3
"""Uruchamianie PowerShell i kompilacji WPF w Windows przez QEMU Guest Agent."""

import argparse
import base64
import json
from pathlib import Path
import re
import subprocess
import sys
import time
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent.parent
TRANSPORT = r'''
import json, secrets, socket, sys
request = json.load(sys.stdin)
with socket.socket(socket.AF_UNIX) as sock:
    sock.settimeout(15)
    sock.connect('/storage/qga.sock')
    stream = sock.makefile('rb')
    sync_id = secrets.randbits(63)
    sock.sendall(b'\xff' + json.dumps({'execute': 'guest-sync-delimited', 'arguments': {'id': sync_id}}).encode() + b'\n')
    while True:
        raw = stream.readline()
        if not raw:
            raise RuntimeError('QEMU Guest Agent zamknal polaczenie.')
        try:
            response = json.loads(raw.rsplit(b'\xff', 1)[-1])
        except (ValueError, UnicodeDecodeError):
            continue
        if response.get('return') == sync_id:
            break
    sock.sendall(json.dumps(request).encode() + b'\n')
    while True:
        raw = stream.readline()
        if not raw:
            raise RuntimeError('QEMU Guest Agent zamknal polaczenie.')
        response = json.loads(raw)
        if 'return' in response or 'error' in response:
            print(json.dumps(response))
            break
'''


def agent(command, arguments=None):
    request = {'execute': command}
    if arguments is not None:
        request['arguments'] = arguments
    result = subprocess.run(
        [str(ROOT / 'scripts/docker.sh'), 'exec', '-i', 'windows-wpf', 'python3', '-c', TRANSPORT],
        input=json.dumps(request), text=True, capture_output=True, timeout=25,
    )
    if result.returncode:
        error = result.stderr.strip()
        if 'TimeoutError: timed out' in error:
            error = 'QEMU Guest Agent jest jeszcze niedostępny. Poczekaj na zakończenie instalacji Windows i skryptu OEM.'
        raise RuntimeError(error or 'QEMU Guest Agent jest jeszcze niedostępny.')
    response = json.loads(result.stdout)
    if 'error' in response:
        raise RuntimeError(response['error'].get('desc', str(response['error'])))
    return response['return']


def powershell(script, timeout):
    wrapped = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + script
    encoded = base64.b64encode(wrapped.encode('utf-16le')).decode('ascii')
    process = agent('guest-exec', {
        'path': r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
        'arg': ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded],
        'capture-output': True,
    })
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        status = agent('guest-exec-status', {'pid': process['pid']})
        if status['exited']:
            for key, stream in [('out-data', sys.stdout), ('err-data', sys.stderr)]:
                if status.get(key):
                    output = base64.b64decode(status[key]).decode('utf-8', errors='replace')
                    if output.startswith('#< CLIXML'):
                        try:
                            xml = ET.fromstring(output.split('\n', 1)[1])
                            output = ''.join(node.text or '' for node in xml.findall('.//{*}S') if node.get('S') in {'Error', 'Warning', 'Verbose', 'Debug', 'Information'})
                            output = re.sub(r'_x([0-9A-Fa-f]{4})_', lambda match: chr(int(match[1], 16)), output)
                        except (ET.ParseError, IndexError):
                            pass
                    stream.write(output)
                    stream.flush()
            if status.get('out-truncated') or status.get('err-truncated'):
                print('QEMU skrócił wynik; pełny log kompilacji znajduje się w shared/artifacts.', file=sys.stderr)
            return status.get('exitcode', 1)
        time.sleep(2)
    raise TimeoutError(f'Proces Windows PID {process["pid"]} nadal działa po {timeout} s; nie uruchomiono drugiej kopii.')


def quote_ps(value):
    return "'" + str(value).replace("'", "''") + "'"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--timeout', type=int, default=1800, help='Limit czasu procesu Windows w sekundach.')
    sub = parser.add_subparsers(dest='action', required=True)
    sub.add_parser('info', help='Informacje o Windows.')
    sub.add_parser('status', help='Status instalacji WPF zapisany w Windows.')
    execute = sub.add_parser('exec', help='Uruchom podane polecenie PowerShell jako SYSTEM.')
    execute.add_argument('command')
    build = sub.add_parser('build', help='Zbuduj projekt umieszczony w shared/.')
    build.add_argument('project', help='Ścieżka względem shared/, np. projects/MyApp/MyApp.csproj.')
    build.add_argument('--configuration', default='Release')
    build.add_argument('--framework', help='Framework dla dotnet build, np. net8.0-windows.')
    build.add_argument('--msbuild', action='store_true', help='MSBuild dla klasycznego .NET Framework.')
    args = parser.parse_args()
    if args.timeout <= 0:
        parser.error('--timeout musi być dodatni.')
    try:
        if args.action == 'info':
            print(json.dumps(agent('guest-get-osinfo'), indent=2, ensure_ascii=False))
            return 0
        if args.action == 'status':
            return powershell("Get-Content 'C:\\WpfSetup\\status.json' -Raw", args.timeout)
        if args.action == 'exec':
            return powershell(args.command, args.timeout)
        if args.msbuild and args.framework:
            parser.error('--framework jest dostępny dla dotnet build; MSBuild używa frameworka projektu.')
        relative = Path(args.project)
        if relative.is_absolute() or '..' in relative.parts or '\\' in args.project:
            parser.error('Podaj ścieżkę względem shared/, używając /.')
        source = (ROOT / 'shared' / relative).resolve()
        if not source.is_relative_to((ROOT / 'shared').resolve()) or not source.is_file():
            parser.error('Projekt musi być istniejącym plikiem wewnątrz shared/.')
        windows_project = '\\\\host.lan\\Data\\' + '\\'.join(relative.parts)
        parent = windows_project.rsplit('\\', 1)[0]
        log = r'\\host.lan\Data\artifacts\build.log'
        script = (
            "$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'; "
            "$env:PATH=\"$env:ProgramFiles\\dotnet;$env:PATH\"; "
            f'Set-Location -LiteralPath {quote_ps(parent)}; '
        )
        if args.msbuild:
            script += (
                "$vswhere=Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\\Installer\\vswhere.exe'; "
                "$tool=& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Workload.ManagedDesktopBuildTools "
                "-find 'MSBuild\\**\\Bin\\MSBuild.exe' | Select-Object -First 1; "
                "if (-not $tool) { throw 'Brak MSBuild .NET Desktop.' }; "
                "$ErrorActionPreference='Continue'; "
                f'& $tool {quote_ps(windows_project)} /restore /nologo /p:RestorePackagesConfig=true '
                f'{quote_ps("/p:Configuration=" + args.configuration)}'
            )
        else:
            script += f'$ErrorActionPreference=\'Continue\'; & "$env:ProgramFiles\\dotnet\\dotnet.exe" build {quote_ps(windows_project)} --nologo --configuration {quote_ps(args.configuration)}'
            if args.framework:
                script += f' --framework {quote_ps(args.framework)}'
        script += f' 2>&1 | Tee-Object -FilePath {quote_ps(log)}; $buildExitCode=$LASTEXITCODE; $ErrorActionPreference=\'Stop\'; exit $buildExitCode'
        return powershell(script, args.timeout)
    except (RuntimeError, TimeoutError, subprocess.TimeoutExpired, ValueError) as exc:
        print(str(exc), file=sys.stderr)
        return 1


if __name__ == '__main__':
    sys.exit(main())
