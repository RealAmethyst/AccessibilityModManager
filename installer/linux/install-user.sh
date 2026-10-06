#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
package="$project_root/dist/linux/AccessibilityModManager-linux-x64"
data_root="${XDG_DATA_HOME:-$HOME/.local/share}"
app_root="$data_root/AccessibilityModManager"
author_root="$data_root/AccessibilityModManager-Author"
desktop_root="$data_root/applications"
stamp=$(date -u +%Y%m%dT%H%M%SZ)

[[ -x "$package/manager/AccessibilityModManager.LinuxApp" ]] || {
    printf 'Manager build missing. Run installer/linux/build-linux.sh first.\n' >&2
    exit 1
}
[[ -x "$package/author/AccessibilityModManager.LinuxAuthorTool" ]] || {
    printf 'AuthorTool build missing. Run installer/linux/build-linux.sh first.\n' >&2
    exit 1
}
mkdir -p -- "$app_root" "$author_root" "$desktop_root"

install_tree() {
    local source=$1 destination=$2
    local staged="${destination}.new-${stamp}"
    cp -a -- "$source" "$staged"
    if [[ -e "$destination" ]]; then
        mv -- "$destination" "${destination}.backup-${stamp}"
    fi
    mv -- "$staged" "$destination"
}

install_tree "$package/manager" "$app_root/linux-x64"
install_tree "$package/author" "$author_root/linux-x64"

manager_desktop="$desktop_root/accessibility-mod-manager.desktop"
author_desktop="$desktop_root/accessibility-mod-author.desktop"
for desktop in "$manager_desktop" "$author_desktop"; do
    if [[ -e "$desktop" ]]; then cp -a -- "$desktop" "${desktop}.backup-${stamp}"; fi
done
cat > "$manager_desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Accessibility Mod Manager
Comment=Install and manage accessible game mods
Exec="$app_root/linux-x64/AccessibilityModManager.LinuxApp"
Terminal=false
Categories=Game;
EOF
cat > "$author_desktop" <<EOF
[Desktop Entry]
Type=Application
Name=Plugin Index Author
Comment=Build and publish accessible game mods
Exec="$author_root/linux-x64/AccessibilityModManager.LinuxAuthorTool"
Terminal=false
Categories=Development;
EOF
printf 'Manager installed: %s\n' "$app_root/linux-x64"
printf 'AuthorTool installed: %s\n' "$author_root/linux-x64"
printf 'Previous installed builds and shortcuts, if any, were backed up with stamp %s.\n' "$stamp"
