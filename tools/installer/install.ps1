# CoreScope installer (per-user, no admin needed to install).
# Copies the app to %LocalAppData%\Programs\CoreScope, adds Start menu (and optional desktop) shortcuts
# and an entry in Settings > Apps so it can be uninstalled normally.
param([switch]$Desktop, [switch]$Quiet)
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "app"
if (-not (Test-Path (Join-Path $src "CoreScope.exe"))) { throw "app\CoreScope.exe not found next to this script." }

if (-not [Environment]::Is64BitOperatingSystem) { throw "CoreScope needs 64-bit Windows 10 (2004) or Windows 11." }
$build = [Environment]::OSVersion.Version.Build
if ($build -lt 19041) { throw "CoreScope needs Windows 10 version 2004 (build 19041) or newer. This PC is build $build." }

$dest = Join-Path $env:LOCALAPPDATA "Programs\CoreScope"
Get-Process CoreScope -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
if (Test-Path $dest) { Remove-Item $dest -Recurse -Force }
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item "$src\*" $dest -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "uninstall.ps1") $dest -Force
# Files from a downloaded zip carry the "from the internet" mark, which makes Windows ask
# "publisher could not be verified" on every launch. The user has chosen to install, so clear it once.
Get-ChildItem $dest -Recurse -File | Unblock-File -ErrorAction SilentlyContinue

$exe = Join-Path $dest "CoreScope.exe"
$version = (Get-Item $exe).VersionInfo.ProductVersion -replace '\+.*$', ''
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut($path) {
    $s = $shell.CreateShortcut($path)
    $s.TargetPath = $exe; $s.WorkingDirectory = $dest; $s.IconLocation = "$exe,0"
    $s.Description = "CoreScope - hardware info, live sensors and PC health"
    $s.Save()
}
New-Shortcut (Join-Path ([Environment]::GetFolderPath("Programs")) "CoreScope.lnk")
if ($Desktop) { New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "CoreScope.lnk") }

$key = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CoreScope"
New-Item -Path $key -Force | Out-Null
$size = [int]((Get-ChildItem $dest -Recurse | Measure-Object Length -Sum).Sum / 1KB)
$values = @{
    DisplayName = "CoreScope"; DisplayVersion = $version; Publisher = "CoreScope"
    DisplayIcon = "$exe,0"; InstallLocation = $dest; EstimatedSize = $size
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
    NoModify = 1; NoRepair = 1
}
foreach ($k in $values.Keys) {
    $type = if ($values[$k] -is [int]) { "DWord" } else { "String" }
    New-ItemProperty -Path $key -Name $k -Value $values[$k] -PropertyType $type -Force | Out-Null
}

Write-Host "CoreScope $version installed to $dest" -ForegroundColor Green
if (-not $Quiet) { Start-Process $exe }
