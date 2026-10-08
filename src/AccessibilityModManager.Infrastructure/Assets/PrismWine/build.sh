#!/usr/bin/env bash
# Prism 0.18.3, commit 94329ebeccbaee7f70b3494a290c9dfaac0184ee.
# source.tar.gz contains the modified MPL-2.0 source and vendored dependencies.
# Built by Accessibility Mod Manager contributors, including Codex.
set -euo pipefail
asset_root=$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)
toolchain=${1:?Pass extracted llvm-mingw-20261006-ucrt-ubuntu-22.04-x86_64 directory}
work=${2:?Pass a new build directory}
mkdir -- "$work"
mkdir -- "$work/source"
tar -xzf "$asset_root/source.tar.gz" -C "$work/source"
cat > "$work/toolchain.cmake" <<EOF
set(CMAKE_SYSTEM_NAME Windows)
set(CMAKE_SYSTEM_PROCESSOR x86_64)
set(CMAKE_C_COMPILER "$toolchain/bin/x86_64-w64-mingw32-clang")
set(CMAKE_CXX_COMPILER "$toolchain/bin/x86_64-w64-mingw32-clang++")
set(CMAKE_RC_COMPILER "$toolchain/bin/x86_64-w64-mingw32-windres")
EOF
# Linux speech only. Host services own suspend/resume; no Windows power callbacks.
cmake -S "$work/source" -B "$work/build" -G Ninja \
  -DCMAKE_TOOLCHAIN_FILE="$work/toolchain.cmake" -DCMAKE_BUILD_TYPE=Release \
  -DCMAKE_CXX_FLAGS='-D_WIN32_WINNT=0x0A00 -DNTDDI_VERSION=0x0A000000' \
  -DBUILD_SHARED_LIBS=ON -DPRISM_BACKEND_DEFAULT=OFF \
  -DPRISM_ENABLE_ORCA_BACKEND=ON -DPRISM_ENABLE_SPEECH_DISPATCHER_BACKEND=ON \
  -DPRISM_ENABLE_SPIEL_BACKEND=ON -DPRISM_ENABLE_POWER_MANAGEMENT=OFF \
  -DPRISM_USE_IPO=OFF -DCMAKE_SHARED_LINKER_FLAGS=-static
cmake --build "$work/build" -j4
sha256sum "$work/build/libprism.dll"
