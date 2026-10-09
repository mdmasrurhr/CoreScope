# CoreScope uninstaller. Removes the app, shortcuts, the "Start with Windows" task and the Apps entry.
# Your settings and benchmark history in %LocalAppData%\CoreScope are kept unless you pass -RemoveData.
param([switch]$RemoveData)
$dest = Join-Path $env:LOCALAPPDATA "Programs\CoreScope"

# Running from inside the folder we're deleting: re-launch from a temp copy.
if ($PSScriptRoot -and ($PSScriptRoot.TrimEnd('\') -ieq $dest.TrimEnd('\'))) {
    $tmp = Join-Path $env:TEMP "corescope-uninstall.ps1"
    Copy-Item $PSCommandPath $tmp -Force
    $args2 = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$tmp`"")
    if ($RemoveData) { $args2 += "-RemoveData" }
    Start-Process powershell.exe -ArgumentList $args2
    exit
}

Get-Process CoreScope -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

# The startup task is created with highest privileges; deleting it may need elevation.
schtasks.exe /Query /TN "CoreScope" *> $null
if ($LASTEXITCODE -eq 0) {
    schtasks.exe /Delete /TN "CoreScope" /F *> $null
    if ($LASTEXITCODE -ne 0) {
        Start-Process schtasks.exe -ArgumentList '/Delete /TN "CoreScope" /F' -Verb RunAs -Wait -WindowStyle Hidden -ErrorAction SilentlyContinue
    }
}

Remove-Item (Join-Path ([Environment]::GetFolderPath("Programs")) "CoreScope.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath("Desktop")) "CoreScope.lnk") -Force -ErrorAction SilentlyContinue
Remove-Item "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CoreScope" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
if ($RemoveData) { Remove-Item (Join-Path $env:LOCALAPPDATA "CoreScope") -Recurse -Force -ErrorAction SilentlyContinue }

Add-Type -AssemblyName PresentationFramework
[System.Windows.MessageBox]::Show("CoreScope has been uninstalled.", "CoreScope") | Out-Null
