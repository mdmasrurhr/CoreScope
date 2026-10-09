# Builds CoreScope as an MSIX package for the Microsoft Store (and optionally a self-signed copy to test locally).
#
#   Store upload:   powershell -ExecutionPolicy Bypass -File tools\msix\make-msix.ps1 `
#                       -IdentityName "12345YourName.CoreScope" -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" `
#                       -PublisherDisplayName "Your Name"
#                   (copy all three values from Partner Center → your app → Product identity)
#   Local test:     add -TestSign   (creates a self-signed certificate, trusts it on THIS PC, signs, installs)
#
# Output: dist\CoreScope-<version>-x64.msix

param(
    [string]$IdentityName = "CoreScope.Dev",
    [string]$Publisher = "CN=CoreScopeDev",
    [string]$PublisherDisplayName = "CoreScope",
    [switch]$TestSign
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Set-Location $root

[xml]$proj = Get-Content "src\CoreScope\CoreScope.csproj"
$v = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$parts = @($v.Split('.') + @('0', '0', '0', '0'))[0..3]
$parts[3] = '0'                       # the Store requires the 4th number to be 0
$version = ($parts -join '.')

# ── Tools: makeappx / makepri / signtool come from Microsoft's Windows SDK BuildTools NuGet package ──
$tools = Join-Path $root "tools\msix\.sdk"
$makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
if (-not $makeappx) {
    Write-Host "Downloading Windows SDK build tools (one time, ~30 MB)..." -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $tools | Out-Null
    $pkg = Join-Path $tools "buildtools.zip"
    Invoke-WebRequest "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools" -OutFile $pkg -UseBasicParsing
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::ExtractToDirectory($pkg, (Join-Path $tools "pkg"))
    Remove-Item $pkg
    $makeappx = Get-ChildItem $tools -Recurse -Filter makeappx.exe | Where-Object { $_.FullName -match '\\x64\\' } | Select-Object -First 1
}
$bin = $makeappx.DirectoryName
$makepri = Join-Path $bin "makepri.exe"
$signtool = Join-Path $bin "signtool.exe"

# ── Layout: app\ (self-contained publish) + Assets\ + AppxManifest.xml + resources.pri ──
$layout = Join-Path $root "dist\msix-layout"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force -Path $layout | Out-Null

Write-Host "Publishing CoreScope $version (self-contained)..." -ForegroundColor Cyan
dotnet publish "src\CoreScope\CoreScope.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -o "$layout\app" -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
Get-ChildItem "$layout\app" -Include corescope.log, selftest.json, sensors.txt -Recurse | Remove-Item -Force
Copy-Item "THIRD-PARTY-NOTICES.txt" "$layout\app" -ErrorAction SilentlyContinue

Copy-Item "tools\msix\Assets" "$layout\Assets" -Recurse
$manifest = Get-Content "tools\msix\AppxManifest.template.xml" -Raw
$manifest = $manifest.Replace("{{IdentityName}}", $IdentityName).Replace("{{PublisherDisplayName}}", $PublisherDisplayName)
$manifest = $manifest.Replace("{{Publisher}}", $Publisher).Replace("{{Version}}", $version)
[IO.File]::WriteAllText("$layout\AppxManifest.xml", $manifest, (New-Object Text.UTF8Encoding($false)))

Push-Location $layout
& $makepri createconfig /cf "$layout\..\priconfig.xml" /dq en-US /o | Out-Null
& $makepri new /pr $layout /cf "$layout\..\priconfig.xml" /of "$layout\resources.pri" /o | Out-Null
Pop-Location
if ($LASTEXITCODE -ne 0) { throw "makepri failed" }

$msix = Join-Path $root "dist\CoreScope-$version-x64.msix"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $layout /p $msix /o
if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
Write-Host "Built $msix" -ForegroundColor Green

if ($TestSign) {
    # Local testing only. The Store re-signs the real upload with Microsoft's certificate.
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) { throw "-TestSign needs an administrator PowerShell (it trusts the test certificate on this PC)." }
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object { $_.Subject -eq $Publisher } | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature `
            -FriendlyName "CoreScope test signing" -CertStoreLocation "Cert:\CurrentUser\My" `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    $cer = Join-Path $env:TEMP "corescope-test.cer"
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
    Add-AppxPackage -Path $msix -ForceApplicationShutdown
    Write-Host "Installed the test package. Find 'CoreScope' in the Start menu." -ForegroundColor Green
}
