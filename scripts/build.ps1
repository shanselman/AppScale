<#
.SYNOPSIS
  Reproducible build: native (CMake/MSVC) then managed (.NET 10), then tests.
.EXAMPLE
  scripts\build.ps1                 # build everything + unit tests
  scripts\build.ps1 -Integration    # also run integration tests against the harnesses (needs a desktop)
#>
param([switch]$Integration, [switch]$SkipNative, [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    if (-not $SkipNative) {
        cmake -S . -B build/native -A x64
        if ($LASTEXITCODE) { throw "cmake configure failed" }
        cmake --build build/native --config Release
        if ($LASTEXITCODE) { throw "native build failed" }
    }
    dotnet build AppScale.sln -c Release -nologo
    if ($LASTEXITCODE) { throw "dotnet build failed" }
    if (-not $SkipTests) {
        dotnet test tests/AppScale.Core.Tests -c Release --no-build -nologo
        if ($LASTEXITCODE) { throw "unit tests failed" }
        if ($Integration) {
            $env:APPSCALE_REQUIRE_HARNESSES = '1'
            dotnet test tests/AppScale.IntegrationTests -c Release --no-build -nologo
            if ($LASTEXITCODE) { throw "integration tests failed" }
        }
    }
} finally { Pop-Location }
