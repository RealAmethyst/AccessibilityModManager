#!/usr/bin/env bash
set -euo pipefail
project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
package="$project_root/publish/linux/AccessibilityModManager-linux-x64"
"$package/manager/install.sh"
printf 'Author CLI: %s/author/amm-author. Run it directly or add that directory to PATH.\n' "$package"
