[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [string]$OutDir
)

# Runs every Win32Harness self-test unattended and writes <name>.json (result) and <name>.jsonl (log)
# to artifacts\evidence\native. Exit code: number of failed tests.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$bin = Join-Path $repo ("artifacts\bin\Win32Harness\" + $Configuration.ToLowerInvariant())
$exe = Join-Path $bin 'Win32Harness.exe'
if (-not $OutDir) { $OutDir = Join-Path $repo 'artifacts\evidence\native' }
New-Item -ItemType Directory -Force $OutDir | Out-Null

$tests = @(
    @{ Name = 'drift';         Args = '--selftest-drift';     Env = @{} },
    @{ Name = 'synthetic';     Args = '--selftest-synthetic'; Env = @{} },
    @{ Name = 'monitors-os';   Args = '--selftest-monitors';  Env = @{} },
    @{ Name = 'monitors-192';  Args = '--selftest-monitors';  Env = @{ APPSCALE_DPI = '192' } },
    @{ Name = 'detours-192';   Args = '--selftest-detours';   Env = @{ APPSCALE_DPI = '192'; APPSCALE_EXPERIMENTAL_DETOURS = '1' } }
)

$failed = 0
foreach ($t in $tests) {
    $json = Join-Path $OutDir "$($t.Name).json"
    $log = Join-Path $OutDir "$($t.Name).jsonl"
    Remove-Item $json, $log -ErrorAction SilentlyContinue
    foreach ($k in 'APPSCALE_DPI', 'APPSCALE_EXPERIMENTAL_DETOURS', 'APPSCALE_DETOURS_HOOKS') { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
    foreach ($k in $t.Env.Keys) { Set-Item "Env:$k" $t.Env[$k] }
    $p = Start-Process -FilePath $exe -ArgumentList "$($t.Args) --result `"$json`" --log `"$log`"" -PassThru -Wait
    foreach ($k in $t.Env.Keys) { Remove-Item "Env:$k" -ErrorAction SilentlyContinue }
    $status = if ($p.ExitCode -eq 0) { 'PASS' } else { $failed++; 'FAIL' }
    Write-Host ("{0,-14} {1} (exit {2})" -f $t.Name, $status, $p.ExitCode)
}
exit $failed
