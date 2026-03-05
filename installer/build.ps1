# Accessibility Mod Manager - Build & Package Script
# Usage: powershell -ExecutionPolicy Bypass -File installer\build.ps1
#
# Prerequisites:
#   - .NET 10 SDK
#   - Inno Setup 6 (iscc.exe on PATH, or installed at default location)

param(
    [string]$Configuration = "Release",
    [string]$Version = "1.0.0"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")

# Ensure dotnet is on PATH
$DotnetDir = "C:\Program Files\dotnet"
if (Test-Path $DotnetDir) {
    $env:PATH = "$DotnetDir;$env:PATH"
}

Write-Host "=== Building Accessibility Mod Manager v$Version ===" -ForegroundColor Cyan

# Step 1: Clean previous publish output
$PublishDir = Join-Path $Root "publish\win-x64"
if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish output..."
    Remove-Item $PublishDir -Recurse -Force
}

# Step 2: Run tests
Write-Host "`n--- Running tests ---" -ForegroundColor Yellow
dotnet test (Join-Path $Root "AccessibilityModManager.slnx") -c $Configuration
if ($LASTEXITCODE -ne 0) {
    Write-Host "Tests failed! Aborting build." -ForegroundColor Red
    exit 1
}

# Step 3: Publish (self-contained, normal directory layout)
Write-Host "`n--- Publishing self-contained ---" -ForegroundColor Yellow
$AppProj = Join-Path $Root "src\AccessibilityModManager.App\AccessibilityModManager.App.csproj"
dotnet publish $AppProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    exit 1
}

$FileCount = (Get-ChildItem $PublishDir -Recurse -File).Count
$TotalSizeMB = [math]::Round((Get-ChildItem $PublishDir -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB, 1)
Write-Host "Published $FileCount files ($TotalSizeMB MB) to $PublishDir" -ForegroundColor Green

# Step 4: Build installer (if Inno Setup is available)
$IsccPaths = @(
    "iscc.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)

$Iscc = $null
foreach ($p in $IsccPaths) {
    if (Get-Command $p -ErrorAction SilentlyContinue) {
        $Iscc = $p
        break
    }
    if (Test-Path $p) {
        $Iscc = $p
        break
    }
}

if ($Iscc) {
    Write-Host "`n--- Building installer ---" -ForegroundColor Yellow

    $DistDir = Join-Path $Root "dist"
    if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

    $IssFile = Join-Path $Root "installer\setup.iss"
    & $Iscc "/DMyAppVersion=$Version" $IssFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Installer build failed!" -ForegroundColor Red
        exit 1
    }

    $InstallerPath = Join-Path $DistDir "AccessibilityModManager-$Version-Setup.exe"
    if (Test-Path $InstallerPath) {
        $InstallerSizeMB = [math]::Round((Get-Item $InstallerPath).Length / 1MB, 1)
        Write-Host "`nInstaller: $InstallerPath ($InstallerSizeMB MB)" -ForegroundColor Green
    }
} else {
    Write-Host "`nInno Setup not found - skipping installer build." -ForegroundColor Yellow
    Write-Host "Install Inno Setup 6 from: https://jrsoftware.org/isdl.php"
    Write-Host "Then re-run this script, or compile installer\setup.iss manually."
}

Write-Host "`n=== Build complete ===" -ForegroundColor Cyan
