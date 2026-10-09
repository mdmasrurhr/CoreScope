# CoreScope overnight build helper.
# Watches for a ".build-request" file in the project folder, then builds (non-admin test build into .\app),
# optionally runs the self-test and/or launches the app, and writes the results to files Claude can read.
# Close this window to stop it. It never deletes anything outside the project folder.

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$Host.UI.RawUI.WindowTitle = "CoreScope build helper (close to stop)"
Write-Host "CoreScope build helper is running. Leave this window open; close it to stop." -ForegroundColor Cyan

while ($true) {
    $requestFile = Join-Path $root ".build-request"
    if (Test-Path $requestFile) {
        $request = (Get-Content $requestFile -Raw).Trim()
        Remove-Item $requestFile -Force
        $stamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Write-Host "[$stamp] Request: $request"

        # Stop any running copy so its files can be replaced.
        Get-Process CoreScope -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 1

        $config = if ($request -match "release") { "release" } else { "test" }
        $extra = if ($config -eq "test") { "-p:TestBuild=true" } else { "" }
        dotnet publish "src\CoreScope\CoreScope.csproj" -c Release -r win-x64 --self-contained false -o app -nologo $extra *> build.log
        $code = $LASTEXITCODE
        $result = "build=$code config=$config at $stamp"

        if ($code -eq 0 -and $request -match "selftest") {
            Remove-Item "app\selftest.json" -ErrorAction SilentlyContinue
            $p = Start-Process "app\CoreScope.exe" -ArgumentList "--selftest" -PassThru
            if (-not $p.WaitForExit(180000)) { $p | Stop-Process -Force; $result += " selftest=timeout" }
            else { $result += " selftest=$($p.ExitCode)" }
        }
        if ($code -eq 0 -and $request -match "launch") {
            Start-Process "app\CoreScope.exe"
            $result += " launched"
        }
        Set-Content -Path (Join-Path $root ".build-done") -Value $result
        Write-Host "  -> $result"
    }
    Start-Sleep -Seconds 4
}
