#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
dotnet_bin=${DOTNET_BIN:-dotnet}
cc_bin=${CC_BIN:-cc}
curl_bin=${CURL_BIN:-curl}
output_root="$project_root/publish/linux"
dist_root="$project_root/dist"
mkdir -p -- "$dist_root"
mkdir -p -- "$output_root"
final="$output_root/AccessibilityModManager-linux-x64"
stage=$(mktemp -d "${TMPDIR:-/tmp}/amm-linux-stage-XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT

"$dotnet_bin" publish \
    "$project_root/src/AccessibilityModManager.LinuxApp/AccessibilityModManager.LinuxApp.csproj" \
    --configuration Release --runtime linux-x64 --self-contained true \
    --output "$stage/manager" --verbosity quiet
"$dotnet_bin" publish \
    "$project_root/src/AccessibilityModManager.LinuxAuthorTool/AccessibilityModManager.LinuxAuthorTool.csproj" \
    --configuration Release --runtime linux-x64 --self-contained true \
    --output "$stage/author" --verbosity quiet
DOTNET_BIN="$dotnet_bin" "$project_root/installer/linux/build-avalonia-atspi.sh" "$stage/manager"
cp -- "$stage/manager/Avalonia.FreeDesktop.AtSpi.dll" "$stage/author/Avalonia.FreeDesktop.AtSpi.dll"
cp -a -- "$stage/manager/third-party" "$stage/author/third-party"
"$cc_bin" -std=c11 -O2 -Wall -Wextra -Werror \
    -o "$stage/manager/steam-proton-launch" \
    "$project_root/installer/linux/steam-proton-launch.c"
chmod 755 "$stage/manager/AccessibilityModManager.LinuxApp" \
    "$stage/manager/steam-proton-launch" \
    "$stage/author/AccessibilityModManager.LinuxAuthorTool"

# The author workflow uses gh for repository discovery and release publishing. Bundle the
# official pinned CLI so a fresh desktop installation works without an administrator account.
cache_root="${XDG_CACHE_HOME:-$HOME/.cache}/accessibility-mod-manager-build"
mkdir -p -- "$cache_root"
gh_archive="$cache_root/gh_2.102.0_linux_amd64.tar.gz"
gh_hash=bb766f710eef8ede859c18578c72c327597cd4c8a85b06001b1f3843c6019386
if [[ ! -f "$gh_archive" ]] || ! printf '%s  %s\n' "$gh_hash" "$gh_archive" | sha256sum -c --status; then
    "$curl_bin" -fL --retry 2 -o "$gh_archive" \
        https://github.com/cli/cli/releases/download/v2.102.0/gh_2.102.0_linux_amd64.tar.gz
fi
printf '%s  %s\n' "$gh_hash" "$gh_archive" | sha256sum -c --status
mkdir -p -- "$stage/author/tools"
tar -xOf "$gh_archive" gh_2.102.0_linux_amd64/bin/gh > "$stage/author/tools/gh"
tar -xOf "$gh_archive" gh_2.102.0_linux_amd64/LICENSE > "$stage/author/tools/gh-LICENSE"
chmod 755 "$stage/author/tools/gh"

backup=""
if [[ -d "$final" ]]; then
    backup="$output_root/AccessibilityModManager-linux-x64-backup-$(date -u +%Y%m%dT%H%M%SZ)"
    mv -- "$final" "$backup"
    printf 'Previous build saved: %s\n' "$backup"
fi
if ! cp -a -- "$stage" "$final" || ! diff -qr -- "$stage" "$final" >/dev/null; then
    rm -rf -- "$final"
    if [[ -n "$backup" ]]; then mv -- "$backup" "$final"; fi
    printf 'Could not verify the copied package directory. The previous build was restored.\n' >&2
    exit 1
fi
rm -rf -- "$stage"
trap - EXIT
printf 'Linux manager and AuthorTool built: %s\n' "$final"

# Keep dist limited to versioned downloads and their checksums. Build trees stay in publish.
python3 "$project_root/installer/linux/package-release.py" "$project_root" "$final" "$dist_root"
