# Accessibility Mod Manager Author CLI - local build script
#
# Produces a single Windows x64 executable and a matching SHA256 file.
# This script only writes local build artifacts; it never creates or uploads a release.

[CmdletBinding()]
param(
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$SelfContained,
    [switch]$Admin
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$Project = Join-Path $Root "src\AccessibilityModManager.AuthorCli\AccessibilityModManager.AuthorCli.csproj"

if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectXml = [xml](Get-Content -LiteralPath $Project -Raw)
    $Version = @($projectXml.Project.PropertyGroup.Version |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1)[0]
    if ([string]::IsNullOrWhiteSpace($Version)) {
        throw "No -Version was given and no <Version> was found in $Project."
    }
    $Version = $Version.Trim()
}

$DotnetDirectory = "C:\Program Files\dotnet"
if (Test-Path -LiteralPath $DotnetDirectory) {
    $env:PATH = "$DotnetDirectory;$env:PATH"
}

$variant = if ($Admin) { "admin" } else { "standard" }
$publishName = if ($Admin) { "author-cli-win-x64-admin" } else { "author-cli-win-x64" }
$distName = if ($Admin) { "dist-author-cli-admin" } else { "dist-author-cli" }
$outputName = if ($Admin) { "amm-author-admin.exe" } else { "amm-author.exe" }
$PublishDirectory = Join-Path $Root "publish\$publishName"
$DistDirectory = Join-Path $Root $distName

function Assert-PathUnderRepository([string]$PathToCheck) {
    $fullPath = [IO.Path]::GetFullPath($PathToCheck).TrimEnd('\')
    $rootPrefix = $Root.TrimEnd('\') + '\'
    if (-not $fullPath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a path outside the repository: $fullPath"
    }
}

Assert-PathUnderRepository $PublishDirectory
if (Test-Path -LiteralPath $PublishDirectory) {
    Remove-Item -LiteralPath $PublishDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $DistDirectory -Force | Out-Null

$selfContainedValue = if ($SelfContained) { "true" } else { "false" }
$adminValue = if ($Admin) { "true" } else { "false" }

Write-Host "Building Author CLI $Version ($variant, win-x64, self-contained=$selfContainedValue)..."
& dotnet publish $Project `
    -c $Configuration `
    -r win-x64 `
    --self-contained $selfContainedValue `
    -p:PublishProfile=win-x64 `
    -p:Version=$Version `
    -p:RegistryAdmin=$adminValue `
    -p:SelfContained=$selfContainedValue `
    -o $PublishDirectory

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$publishedExecutable = Join-Path $PublishDirectory "amm-author.exe"
if (-not (Test-Path -LiteralPath $publishedExecutable -PathType Leaf)) {
    throw "Expected published executable was not found: $publishedExecutable"
}

$distExecutable = Join-Path $DistDirectory $outputName
Copy-Item -LiteralPath $publishedExecutable -Destination $distExecutable -Force

$hash = (Get-FileHash -LiteralPath $distExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
$hashPath = "$distExecutable.sha256"
$hashLine = "$hash  $outputName`r`n"
[IO.File]::WriteAllText($hashPath, $hashLine, (New-Object Text.UTF8Encoding($false)))

$sizeMiB = [Math]::Round((Get-Item -LiteralPath $distExecutable).Length / 1MB, 1)
Write-Host "Executable: $distExecutable ($sizeMiB MiB)"
Write-Host "SHA256:    $hash"
Write-Host "Hash file: $hashPath"
