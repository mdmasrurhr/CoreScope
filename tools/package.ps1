# Builds the shippable CoreScope package:
#   dist\CoreScope-<version>-win-x64\        self-contained app (no .NET install needed on the target PC)
#   dist\CoreScope-<version>-win-x64.zip     the same folder, zipped, ready to share
# Usage:  powershell -ExecutionPolicy Bypass -File tools\package.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

[xml]$proj = Get-Content "src\CoreScope\CoreScope.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { $version = "1.0.0" }
$name = "CoreScope-$version-win-x64"
$out = Join-Path $root "dist\$name"

# The folder may be open in Explorer (Windows then refuses to delete the folder itself): empty it instead.
if (Test-Path $out) {
    try { Remove-Item $out -Recurse -Force -ErrorAction Stop }
    catch { Get-ChildItem $out -Force | Remove-Item -Recurse -Force }
}
New-Item -ItemType Directory -Force -Path $out | Out-Null

Write-Host "Publishing self-contained $name ..." -ForegroundColor Cyan
dotnet publish "src\CoreScope\CoreScope.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o "$out\app" -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

# Remove dev leftovers if any.
Get-ChildItem "$out\app" -Include corescope.log, selftest.json, sensors.txt -Recurse | Remove-Item -Force

Copy-Item "tools\installer\Install.cmd", "tools\installer\Uninstall.cmd", "tools\installer\install.ps1", "tools\installer\uninstall.ps1" $out
Copy-Item "README.md", "CHANGELOG.md", "THIRD-PARTY-NOTICES.txt" $out -ErrorAction SilentlyContinue
Copy-Item "THIRD-PARTY-NOTICES.txt" "$out\app" -ErrorAction SilentlyContinue

$zip = "$out.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
# .NET ZipFile writes portable "/" separators (Windows PowerShell 5 Compress-Archive writes "\").
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($out, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host "Done: $zip ($mb MB)" -ForegroundColor Green

# ── Setup.exe (Inno Setup). Install it once with:  winget install JRSoftware.InnoSetup
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$out\app" "/DOutputDir=$root\dist" "tools\installer\CoreScope.iss"
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed ($LASTEXITCODE)" }
    $setup = Join-Path $root "dist\CoreScope-$version-Setup.exe"
    $hash = (Get-FileHash $setup -Algorithm SHA256).Hash
    Set-Content -Path "$setup.sha256" -Value "$hash  CoreScope-$version-Setup.exe"
    Write-Host "Done: $setup  SHA256 $hash" -ForegroundColor Green
} else {
    Write-Host "Inno Setup not found, so no Setup.exe was made (run: winget install JRSoftware.InnoSetup)" -ForegroundColor Yellow
}
