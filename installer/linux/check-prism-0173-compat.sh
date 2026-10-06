#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
mingw_cc=${MINGW_CC:-x86_64-w64-mingw32-clang}
asset="$project_root/src/AccessibilityModManager.Infrastructure/Assets/Prism0173Compat/prism_compat.dll"
source="$project_root/src/AccessibilityModManager.Infrastructure/Assets/Prism0173Compat/prism_compat.c"
expected=f2f234b8ec3b0f6c0df5f4e501232e8e9c20a1299c885ffc7e19ef90559ab6ec
stage=$(mktemp -d "${TMPDIR:-/tmp}/amm-prism-compat-XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT

"$mingw_cc" -std=c11 -O2 -Wall -Wextra -Werror -shared \
    -Wl,--no-insert-timestamp -o "$stage/amm-prism-0173-compat.dll" "$source"
printf '%s  %s\n' "$expected" "$stage/amm-prism-0173-compat.dll" | sha256sum -c --status
cmp -- "$stage/amm-prism-0173-compat.dll" "$asset"
printf 'Prism 0.17.3 compatibility DLL matches its source and pinned hash.\n'
