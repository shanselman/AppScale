[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$Clean
)

# Configures and builds the native superbuild (Win32Harness + AppScale.Native).
# Output: artifacts\bin\Win32Harness\<release|debug>\{Win32Harness.exe, AppScale.Native.dll, AppScale.Injector.exe}
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$buildDir = Join-Path $repo 'build\native'

$cmake = (Get-Command cmake -ErrorAction SilentlyContinue).Source
if (-not $cmake) {
    $candidate = Join-Path $env:ProgramFiles 'CMake\bin\cmake.exe'
    if (Test-Path $candidate) { $cmake = $candidate } else { throw 'cmake not found on PATH or in Program Files\CMake' }
}

if ($Clean -and (Test-Path $buildDir)) { Remove-Item -Recurse -Force $buildDir }

& $cmake -S $repo -B $buildDir -A x64
if ($LASTEXITCODE -ne 0) { throw "CMake configure failed ($LASTEXITCODE)" }

& $cmake --build $buildDir --config $Configuration --parallel
if ($LASTEXITCODE -ne 0) { throw "CMake build failed ($LASTEXITCODE)" }

$out = Join-Path $repo ("artifacts\bin\Win32Harness\" + $Configuration.ToLowerInvariant())
foreach ($f in 'Win32Harness.exe', 'AppScale.Native.dll', 'AppScale.Injector.exe') {
    if (-not (Test-Path (Join-Path $out $f))) { throw "Expected output missing: $out\$f" }
}
Write-Host "Native build OK: $out"
