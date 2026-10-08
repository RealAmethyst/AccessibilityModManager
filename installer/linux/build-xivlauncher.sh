#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
destination=${1:?Pass the manager package directory}
dotnet_bin=${DOTNET_BIN:-dotnet}
source_tree=$(mktemp -d "${TMPDIR:-/tmp}/amm-accessible-xl-source-XXXXXXXX")
trap 'rm -rf -- "$source_tree"' EXIT
git clone --quiet --depth 1 --branch 1.4.0 --recurse-submodules --shallow-submodules \
    https://github.com/goatcorp/XIVLauncher.Core.git "$source_tree/upstream"
python3 "$project_root/installer/linux/xivlauncher/prepare-source.py" "$source_tree/upstream"
"$dotnet_bin" test "$project_root/installer/linux/xivlauncher/tests/LauncherTests.csproj" \
    --configuration Release --runtime linux-x64 -p:SelfContained=true \
    -p:XivSourceRoot="$source_tree/upstream" --verbosity quiet
"$dotnet_bin" publish "$source_tree/upstream/src/XIVLauncher.Core/XIVLauncher.Core.csproj" \
    --configuration Release --runtime linux-x64 --self-contained true \
    -p:PublishSingleFile=false --output "$destination/xivlauncher" --verbosity quiet
cp -- "$destination/Avalonia.FreeDesktop.AtSpi.dll" "$destination/xivlauncher/Avalonia.FreeDesktop.AtSpi.dll"
cp -a -- "$destination/third-party" "$destination/xivlauncher/third-party"
curl -fsSL --retry 2 -o "$source_tree/aria2c.tar.gz" \
    https://raw.githubusercontent.com/Blooym/xlm/242ce078cf23d5ed95b570e9966ba6df2c58941e/static/aria2c-static.tar.gz
printf '%s  %s\n' 75a8f03e4bafeae9bb05ed7a1b39250a75b151c74f1ba7af3b44550c535c2142 "$source_tree/aria2c.tar.gz" | sha256sum -c --status
tar -xOzf "$source_tree/aria2c.tar.gz" aria2c > "$destination/xivlauncher/aria2c"
tar -xOzf "$source_tree/aria2c.tar.gz" build_info.md > "$destination/xivlauncher/aria2c-build-info.md"
chmod 755 "$destination/xivlauncher/aria2c"
python3 "$project_root/installer/linux/xivlauncher/package-frontend.py" "$source_tree/upstream" "$destination/xivlauncher"
