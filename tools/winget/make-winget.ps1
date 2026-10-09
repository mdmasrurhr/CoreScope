# Fills the winget manifests for a GitHub release.
#   powershell -ExecutionPolicy Bypass -File tools\winget\make-winget.ps1 -GitHubUser yourname
# Output: dist\winget\manifests\r\Rakin\CoreScope\<version>\ (3 files, ready for a pull request to microsoft/winget-pkgs)
param([Parameter(Mandatory)][string]$GitHubUser, [string]$Repo = "CoreScope")
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
[xml]$proj = Get-Content "$root\src\CoreScope\CoreScope.csproj"
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$setup = "$root\dist\CoreScope-$version-Setup.exe"
if (-not (Test-Path $setup)) { throw "Build the installer first (tools\package.ps1): $setup not found" }
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash
$repoUrl = "https://github.com/$GitHubUser/$Repo"
$values = @{
    "{{VERSION}}" = $version
    "{{SHA256}}" = $hash
    "{{REPO_URL}}" = $repoUrl
    "{{RELEASE_URL}}" = "$repoUrl/releases/tag/v$version"
    "{{INSTALLER_URL}}" = "$repoUrl/releases/download/v$version/CoreScope-$version-Setup.exe"
}
$out = "$root\dist\winget\manifests\r\Rakin\CoreScope\$version"
New-Item -ItemType Directory -Force -Path $out | Out-Null
foreach ($file in Get-ChildItem $PSScriptRoot -Filter "*.yaml") {
    $text = Get-Content $file.FullName -Raw
    foreach ($k in $values.Keys) { $text = $text.Replace($k, $values[$k]) }
    $text = ($text -split "`r?`n" | Where-Object { $_ -notmatch '^# ' }) -join "`n"
    [IO.File]::WriteAllText((Join-Path $out $file.Name), $text.TrimStart(), (New-Object Text.UTF8Encoding($false)))
}
Write-Host "Manifests written to $out" -ForegroundColor Green
Write-Host "Test locally:  winget settings --enable LocalManifestFiles ; winget install --manifest `"$out`""
