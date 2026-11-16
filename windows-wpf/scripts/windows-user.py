#!/usr/bin/env python3
"""Run a PowerShell file in the logged-in Windows desktop session (no credentials)."""
import argparse
import base64
import json
from pathlib import Path
import sys
import time
import uuid

from windows import ROOT, agent, quote_ps


def start_ps(script):
    encoded = base64.b64encode(script.encode('utf-16le')).decode('ascii')
    return agent('guest-exec', {
        'path': r'C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe',
        'arg': ['-NoLogo', '-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded],
        'capture-output': True,
    })['pid']


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('script', type=Path)
    parser.add_argument('--timeout', type=int, default=600)
    args = parser.parse_args()
    task = 'Postinstall-Test-' + uuid.uuid4().hex
    folder = ROOT / 'shared' / 'artifacts' / 'tests'
    folder.mkdir(parents=True, exist_ok=True)
    source = folder / (task + '.ps1')
    log = folder / (task + '.log')
    done = folder / (task + '.done')
    unc = '\\\\host.lan\\Data\\artifacts\\tests\\' + task
    user_script = "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Console]::OutputEncoding=[Text.Encoding]::UTF8; " + args.script.read_text(encoding='utf-8-sig')
    script_encoded = base64.b64encode(user_script.encode('utf-16le')).decode('ascii')
    wrapper = (
        "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; "
        "[Console]::OutputEncoding=[Text.Encoding]::UTF8; "
        f"Start-Transcript -Path {quote_ps(unc + '.log')} -Force | Out-Null; "
        '$testExitCode=0; try { & "$env:WINDIR\\System32\\WindowsPowerShell\\v1.0\\powershell.exe" '
        "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + script_encoded +
        "; $testExitCode=$LASTEXITCODE } "
        "catch { Write-Output ($_ | Out-String); $testExitCode=1 } "
        "finally { Stop-Transcript | Out-Null; "
        f"Set-Content -LiteralPath {quote_ps(unc + '.done')} -Value $testExitCode -Encoding UTF8 }}"
    )
    source.write_text(wrapper, encoding='utf-8-sig')
    launch = (
        "$ErrorActionPreference='Stop'; "
        "$user=(Get-CimInstance Win32_ComputerSystem).UserName; "
        "if (-not $user) { throw 'No interactive Windows user.' }; "
        f"$a=New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "
        + quote_ps('-NoLogo -NoProfile -ExecutionPolicy Bypass -File "' + unc + '.ps1"') + '; '
        "$p=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest; "
        f"Register-ScheduledTask -TaskName {quote_ps(task)} -Action $a -Principal $p -Force | Out-Null; "
        f"Start-ScheduledTask -TaskName {quote_ps(task)}"
    )
    pid = start_ps(launch)
    deadline = time.monotonic() + args.timeout
    launch_checked = False
    while time.monotonic() < deadline:
        if not launch_checked:
            status = agent('guest-exec-status', {'pid': pid})
            if status['exited']:
                launch_checked = True
                if status.get('exitcode', 0):
                    print(base64.b64decode(status.get('err-data', '')).decode('utf-8', 'replace'), file=sys.stderr)
                    return 1
        if done.exists():
            if log.exists():
                print(log.read_text(encoding='utf-8-sig', errors='replace'))
            code = int(done.read_text(encoding='utf-8-sig').strip())
            start_ps(f"Unregister-ScheduledTask -TaskName {quote_ps(task)} -Confirm:$false")
            print(json.dumps({'task': task, 'exit_code': code, 'log': str(log)}, ensure_ascii=False))
            return code
        time.sleep(2)
    print(f'Task {task} is still running. Log: {log}. No second copy was started.', file=sys.stderr)
    return 1


if __name__ == '__main__':
    sys.exit(main())
