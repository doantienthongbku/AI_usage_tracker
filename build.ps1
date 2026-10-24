# Builds dist\AIUsageTracker-Setup-<version>.exe: run the tests, publish one self-contained exe,
# then package it with Inno Setup 6. Usage: .\build.ps1 [-Version 1.1.0]
param([string]$Version = "1.1.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

# A per-user .NET SDK (dotnet-install.ps1 default location) wins over an older machine-wide one.
if (Test-Path "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe") { $env:PATH = "$env:LOCALAPPDATA\Microsoft\dotnet;$env:PATH" }

dotnet test tests
if ($LASTEXITCODE) { throw "tests failed" }

dotnet publish src -c Release -p:Version=$Version -o publish
if ($LASTEXITCODE) { throw "publish failed" }

$iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install --id JRSoftware.InnoSetup -e --scope user" }
& $iscc /Qp "/DVersion=$Version" installer\setup.iss
if ($LASTEXITCODE) { throw "installer build failed" }

Get-Item "dist\AIUsageTracker-Setup-$Version.exe"
