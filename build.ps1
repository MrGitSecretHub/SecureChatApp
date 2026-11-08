# Build script for SecureChat.
# Usage:
#   .\build.ps1                 # Windows + Android + tests
#   .\build.ps1 -Target windows
#   .\build.ps1 -Target android
#   .\build.ps1 -Target tests
#   .\build.ps1 -WindowsConfiguration Release

param(
    [ValidateSet("all", "windows", "android", "tests")]
    [string]$Target = "all",
    [string]$WindowsConfiguration = "Debug",
    [string]$AndroidConfiguration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Invoke-DotNet {
    param(
        [string]$Title,
        [string[]]$Arguments
    )

    Write-Host ""
    Write-Host "==> $Title" -ForegroundColor Cyan
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $Title (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}

function Show-Artifact {
    param([string]$Path)

    if (-not (Test-Path $Path)) {
        Write-Host "  MISSING: $Path" -ForegroundColor Yellow
        return
    }

    $item = Get-Item $Path
    Write-Host ("  {0}  {1:N1} MB  {2}" -f $item.LastWriteTime, ($item.Length / 1MB), $item.FullName)
}

if ($Target -in @("all", "windows")) {
    # Running app locks the output exe (MSB3027/MSB3021).
    Get-Process -Name "SecureChat.App" -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    Invoke-DotNet "Windows build ($WindowsConfiguration)" @(
        "build", "src\SecureChat.App\SecureChat.App.csproj",
        "-f", "net9.0-windows10.0.19041.0",
        "-c", $WindowsConfiguration,
        "--nologo", "-v", "q"
    )
}

if ($Target -in @("all", "android")) {
    Invoke-DotNet "Android build ($AndroidConfiguration)" @(
        "build", "src\SecureChat.App\SecureChat.App.csproj",
        "-f", "net9.0-android",
        "-c", $AndroidConfiguration,
        "--nologo", "-v", "q"
    )
}

if ($Target -in @("all", "tests")) {
    Invoke-DotNet "Tests" @(
        "test", "tests\SecureChat.Core.Tests\SecureChat.Core.Tests.csproj",
        "--nologo", "-v", "q"
    )
}

Write-Host ""
Write-Host "==> Artifacts" -ForegroundColor Cyan
Show-Artifact "src\SecureChat.App\bin\$WindowsConfiguration\net9.0-windows10.0.19041.0\win10-x64\SecureChat.App.exe"
Show-Artifact "src\SecureChat.App\bin\$AndroidConfiguration\net9.0-android\com.securechat.app-Signed.apk"

Write-Host ""
Write-Host "Done." -ForegroundColor Green
