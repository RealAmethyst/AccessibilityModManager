#!/usr/bin/env bash
# Rebuilds the Prism 0.7.1 ABI adapter with llvm-mingw 20261006 (ucrt, ubuntu-22.04 x86_64)
# and checks byte identity with the embedded asset.
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
mingw_cc=${MINGW_CC:-x86_64-w64-mingw32-clang}
asset="$project_root/src/AccessibilityModManager.Infrastructure/Assets/Prism071Compat/prism_compat.dll"
source="$project_root/src/AccessibilityModManager.Infrastructure/Assets/Prism071Compat/prism_compat.c"
expected=43581ed862e787cf1ff4b3f67e3076022e63090f8116138ab136e21effefa475
stage=$(mktemp -d "${TMPDIR:-/tmp}/amm-prism-compat-XXXXXXXX")
trap 'rm -rf -- "$stage"' EXIT

"$mingw_cc" -std=c11 -O2 -Wall -Wextra -Werror -shared \
    -Wl,--no-insert-timestamp -o "$stage/amm-prism-071-compat.dll" "$source"
printf '%s  %s\n' "$expected" "$stage/amm-prism-071-compat.dll" | sha256sum -c --status
cmp -- "$stage/amm-prism-071-compat.dll" "$asset"
printf 'Prism 0.7.1 compatibility DLL matches its source and pinned hash.\n'
