#!/usr/bin/env bash
set -euo pipefail

project_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/../.." && pwd)
work=$(mktemp -d)
trap 'rm -rf -- "$work"' EXIT
cc -std=c11 -O2 -Wall -Wextra -Werror -o "$work/steam-proton-launch" "$project_root/installer/linux/steam-proton-launch.c"
touch "$work/Game.exe" "$work/Launcher.exe"
mkdir "$work/bridge"
cat > "$work/capture" <<'CAPTURE'
#!/usr/bin/env bash
printf 'DLLPATH=%s\nOVERRIDES=%s\nRELOADED=%s\n' "${WINEDLLPATH-}" "${WINEDLLOVERRIDES-}" "${AMM_RELOADED_CONFIG_READY-}"
printf 'ARG=%s\n' "$@"
CAPTURE
chmod +x "$work/capture"

WINEDLLPATH=/existing WINEDLLOVERRIDES='dinput8=n,b' \
    "$work/steam-proton-launch" --amm-v1 direct "$work/Game.exe" - "$work/bridge" \
    'winhttp.dll=n,b' 0 -- "$work/capture" before "$work/Game.exe" after > "$work/direct.txt"
rg -Fx "DLLPATH=$work/bridge:/existing" "$work/direct.txt"
rg -Fx 'OVERRIDES=winhttp.dll=n,b;dinput8=n,b' "$work/direct.txt"
rg -Fx 'RELOADED=' "$work/direct.txt"
rg -Fx "ARG=$work/Game.exe" "$work/direct.txt"
if rg -Fq "ARG=$work/Launcher.exe" "$work/direct.txt"; then exit 1; fi

env -u WINEDLLPATH -u WINEDLLOVERRIDES \
    "$work/steam-proton-launch" --amm-v1 direct "$work/Game.exe" - - \
    'version=n,b' 0 -- "$work/capture" "$work/Game.exe" > "$work/no-bridge.txt"
rg -Fx 'DLLPATH=' "$work/no-bridge.txt"
rg -Fx 'OVERRIDES=version=n,b' "$work/no-bridge.txt"
rg -Fx 'RELOADED=' "$work/no-bridge.txt"

"$work/steam-proton-launch" --amm-v1 replaceExecutable "$work/Game.exe" \
    "$work/Launcher.exe" - - 1 -- "$work/capture" "$work/Game.exe" > "$work/replace.txt"
rg -Fx 'RELOADED=1' "$work/replace.txt"
rg -Fx "ARG=$work/Launcher.exe" "$work/replace.txt"

"$work/steam-proton-launch" "$work/Game.exe" "$work/Launcher.exe" "$work/bridge" \
    -- "$work/capture" "$work/Game.exe" after > "$work/legacy.txt"
rg -Fx 'RELOADED=1' "$work/legacy.txt"
rg -Fx "ARG=$work/Launcher.exe" "$work/legacy.txt"
rg -Fx "ARG=$work/Game.exe" "$work/legacy.txt"

WINEDLLOVERRIDES='winhttp=n' \
    "$work/steam-proton-launch" --amm-v1 direct "$work/Game.exe" - - \
    'winhttp.dll=n,b' 0 -- "$work/capture" "$work/Game.exe" > "$work/conflict.txt" 2>&1 && exit 1
rg -F 'conflicting Wine DLL override' "$work/conflict.txt"

"$work/steam-proton-launch" --amm-v1 direct "$work/Game.exe" - - - 0 \
    -- "$work/capture" "$work/Launcher.exe" > "$work/wrong-game.txt" 2>&1 && exit 1
rg -F 'does not contain the expected game executable' "$work/wrong-game.txt"
printf 'Steam Proton wrapper checks passed.\n'
