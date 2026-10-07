#!/usr/bin/env bash
set -euo pipefail
project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
package="$project_root/publish/linux/AccessibilityModManager-linux-x64"
"$package/manager/install.sh"
"$package/author/install.sh"
