# Builds publish\PrivacyToggle.exe (self-contained single file) and, if Inno Setup is installed,
# the installer in installer\Output.
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

dotnet publish src/PrivacyToggle -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Write-Host "Built publish\PrivacyToggle.exe"

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if ($iscc) {
    & $iscc installer\PrivacyToggle.iss
    if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
    Write-Host "Built installer in installer\Output"
} else {
    Write-Host "Inno Setup not found; skipped the installer (get it from https://jrsoftware.org/isinfo.php)."
}
