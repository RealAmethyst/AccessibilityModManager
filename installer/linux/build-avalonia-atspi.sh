#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
dotnet_bin=${DOTNET_BIN:-dotnet}
destination=${1:?Pass the manager package directory}
source_commit=8eeda4f6f546165b3f72e63c9f42247abb306905
dbus_commit=cada3ecfc153a9efbc8d64f530136d737c1a7eec
patch="$project_root/installer/linux/patches/avalonia-atspi-focus.patch"

if ! grep -Fq 'PackageReference Include="Avalonia" Version="12.1.3"' \
    "$project_root/src/AccessibilityModManager.LinuxApp/AccessibilityModManager.LinuxApp.csproj"; then
    printf 'The AT-SPI focus patch is pinned to Avalonia 12.1.3. Review it before changing versions.\n' >&2
    exit 1
fi

source_tree=$(mktemp -d "$project_root/dist/linux/.avalonia-atspi-source-XXXXXXXX")
trap 'rm -rf -- "$source_tree"' EXIT
git clone --quiet --depth 1 --branch 12.1.3 --recurse-submodules \
    https://github.com/AvaloniaUI/Avalonia.git "$source_tree/avalonia"
if [[ $(git -C "$source_tree/avalonia" rev-parse HEAD) != "$source_commit" ]] ||
   [[ $(git -C "$source_tree/avalonia/external/Avalonia.DBus" rev-parse HEAD) != "$dbus_commit" ]]; then
    printf 'The pinned Avalonia source or D-Bus submodule changed. The package was not built.\n' >&2
    exit 1
fi

git -C "$source_tree/avalonia" apply --check "$patch"
git -C "$source_tree/avalonia" apply "$patch"
"$dotnet_bin" build \
    "$source_tree/avalonia/src/Avalonia.FreeDesktop.AtSpi/Avalonia.FreeDesktop.AtSpi.csproj" \
    --configuration Release --framework net10.0 --verbosity quiet

cp -- "$source_tree/avalonia/src/Avalonia.FreeDesktop.AtSpi/bin/Release/net10.0/Avalonia.FreeDesktop.AtSpi.dll" \
    "$destination/Avalonia.FreeDesktop.AtSpi.dll"
mkdir -p -- "$destination/third-party"
cp -- "$source_tree/avalonia/licence.md" "$destination/third-party/Avalonia-LICENSE.md"
cp -- "$source_tree/avalonia/NOTICE.md" "$destination/third-party/Avalonia-NOTICE.md"
printf 'Patched Avalonia AT-SPI focus bridge from %s: %s\n' \
    "$source_commit" "$destination/Avalonia.FreeDesktop.AtSpi.dll"
