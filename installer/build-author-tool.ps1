# Plugin Index Author - Build Script
# Usage: powershell -ExecutionPolicy Bypass -File installer\build-author-tool.ps1
#
# Produces a single .exe in dist\ that plugin authors can grab from a GitHub release.
# Default is framework-dependent single-file (small .exe, requires .NET 10 Desktop Runtime).
# Pass -SelfContained to bundle the runtime into the exe (~80 MB, no dependency).

param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0",
    [switch]$SelfContained,
    [switch]$Admin
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")

$DotnetDir = "C:\Program Files\dotnet"
if (Test-Path $DotnetDir) {
    $env:PATH = "$DotnetDir;$env:PATH"
}

Write-Host "=== Building Plugin Index Author v$Version ===" -ForegroundColor Cyan

$PublishDir = Join-Path $Root "publish\author-tool-win-x64"
if (Test-Path $PublishDir) {
    Write-Host "Cleaning previous publish output..."
    Remove-Item $PublishDir -Recurse -Force
}

$AuthorProj = Join-Path $Root "src\AccessibilityModManager.AuthorTool\AccessibilityModManager.AuthorTool.csproj"

$selfContainedFlag = if ($SelfContained) { "true" } else { "false" }
$adminFlag = if ($Admin) { "true" } else { "false" }
Write-Host "`n--- Publishing (self-contained=$selfContainedFlag, admin=$Admin) ---" -ForegroundColor Yellow

dotnet publish $AuthorProj `
    -c $Configuration `
    -r win-x64 `
    --self-contained $selfContainedFlag `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:RegistryAdmin=$adminFlag `
    -o $PublishDir

if ($LASTEXITCODE -ne 0) {
    Write-Host "Publish failed!" -ForegroundColor Red
    exit 1
}

$ExePath = Join-Path $PublishDir "AccessibilityModManager.AuthorTool.exe"
if (-not (Test-Path $ExePath)) {
    Write-Host "Expected exe not found at $ExePath" -ForegroundColor Red
    exit 1
}

$ExeSizeMB = [math]::Round((Get-Item $ExePath).Length / 1MB, 1)
Write-Host "Built: $ExePath ($ExeSizeMB MB)" -ForegroundColor Green

# Admin builds go to a separate folder so they can never be bundled into a public GitHub
# release alongside the regular dist artifacts. dist-admin/ is gitignored too.
$DistDir = if ($Admin) { Join-Path $Root "dist-admin" } else { Join-Path $Root "dist" }
if (-not (Test-Path $DistDir)) { New-Item -ItemType Directory -Path $DistDir | Out-Null }

$adminSuffix = if ($Admin) { "-admin" } else { "" }
$DistName = if ($SelfContained) {
    "PluginIndexAuthor-$Version-selfcontained$adminSuffix.exe"
} else {
    "PluginIndexAuthor-$Version$adminSuffix.exe"
}
$DistPath = Join-Path $DistDir $DistName
try {
    Copy-Item $ExePath $DistPath -Force -ErrorAction Stop
    Write-Host "Copied to: $DistPath" -ForegroundColor Green
} catch {
    Write-Host "Could not copy to $DistPath - file may be in use (close any running instance)." -ForegroundColor Yellow
    Write-Host "Fresh build still available at: $ExePath" -ForegroundColor Yellow
}

$Sha = (Get-FileHash $DistPath -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Host "SHA256: $Sha"

Write-Host "`n=== Build complete ===" -ForegroundColor Cyan
