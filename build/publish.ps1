# Windows: self-contained single-file publish + Inno Setup installer.
# Usage (PowerShell, from the repo root):  ./build/publish.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$env:AVALONIA_TELEMETRY_OPTOUT = "1"

$publishDir = Join-Path $root "artifacts/publish/win-x64"
dotnet publish (Join-Path $root "src/PokeStudio.App/PokeStudio.App.csproj") -c Release -r win-x64 `
  -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = (Get-Command iscc -ErrorAction SilentlyContinue)?.Source
if (-not $iscc) { $iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe" }
if (-not (Test-Path $iscc)) {
  Write-Warning "Inno Setup not found. Install it (winget install JRSoftware.InnoSetup) to build the installer. Portable build is in $publishDir"
  exit 0
}

& $iscc "/DAppVersion=$Version" "/DPublishDir=$publishDir" (Join-Path $root "build/windows/PokeStudio.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Write-Host "Installer: $(Join-Path $root 'artifacts/installer')"
