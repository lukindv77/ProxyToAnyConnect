from pathlib import Path
import subprocess

expected = {
    '.github/workflows/build.yml': '2baa12bca9dbb3b09cd1aff2a801d8a3c7f4e713',
    '.github/workflows/handoff.yml': '8c9e69d1aac8e953a1138e3b491fc54ff0d277df',
}
for path, sha in expected.items():
    actual = subprocess.check_output(['git', 'rev-parse', f'HEAD:{path}'], text=True).strip()
    if actual != sha:
        raise SystemExit(f'unexpected #36 input blob for {path}: {actual}')


def replace_block(path: Path, old_lf: bytes, new_lf: bytes, label: str) -> None:
    data = path.read_bytes()
    matches = []
    for newline in (b'\r\n', b'\n'):
        old = old_lf.replace(b'\n', newline)
        count = data.count(old)
        if count:
            matches.append((newline, old, count))
    if len(matches) != 1 or matches[0][2] != 1:
        detail = ', '.join(f'{nl!r}:{count}' for nl, _, count in matches) or 'none'
        raise SystemExit(f'expected one {label}, matches={detail}')
    newline, old, _ = matches[0]
    path.write_bytes(data.replace(old, new_lf.replace(b'\n', newline)))


replace_block(
    Path('.github/workflows/build.yml'),
    b'''      - name: Validate PowerShell tools
        shell: pwsh
''',
    b'''      - name: Synthetic native-command failure sentinel
        id: native_failure_sentinel
        continue-on-error: true
        shell: pwsh
        run: |
          $ErrorActionPreference = 'Stop'
          $PSNativeCommandUseErrorActionPreference = $true
          & $env:ComSpec /d /c 'exit 23'
          throw 'Native command failure was incorrectly ignored.'

      - name: Assert native-command failure propagation
        shell: pwsh
        run: |
          if ('${{ steps.native_failure_sentinel.outcome }}' -ne 'failure') {
            throw 'Synthetic native command failure did not fail its Actions step.'
          }

      - name: Validate PowerShell tools
        shell: pwsh
''',
    'build native-failure sentinel insertion point')

replace_block(
    Path('.github/workflows/handoff.yml'),
    b'''        run: |
          $ErrorActionPreference = 'Stop'
          $stage = Join-Path $env:RUNNER_TEMP 'ProxyToAnyConnect-handoff'
''',
    b'''        run: |
          $ErrorActionPreference = 'Stop'
          $PSNativeCommandUseErrorActionPreference = $true
          $stage = Join-Path $env:RUNNER_TEMP 'ProxyToAnyConnect-handoff'
''',
    'handoff native fail-fast insertion point')
