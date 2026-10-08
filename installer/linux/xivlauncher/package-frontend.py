#!/usr/bin/env python3
"""Package the pinned frontend's sources, notices and file-integrity manifest."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import tarfile

source, destination = map(Path, sys.argv[1:])
(destination / "versiondata").write_text("1.4.0")
shutil.copyfile(source / "LICENSE", destination / "XIVLauncher-LICENSE")
(destination / "ACCESSIBLE-FRONTEND.txt").write_text('''XIVLauncher 1.4.0 with Accessibility Mod Manager frontend, revision 3.
Upstream: https://github.com/goatcorp/XIVLauncher.Core
Commit: 06c32980a75447fe0f15cdac30f8931f70d687e9
Shared launcher: 40ed6e93e7eb73e1c18f4d4871e05f32ab5fd2c6
Modified UI and integration code: Accessibility Mod Manager contributors, including Codex.
License: GPL-3.0-or-later; see XIVLauncher-LICENSE.

The frontend uses the original login, patching, Wine, Dalamud and game-lifetime code.
Account metadata, preferences and keyring passwords use XIVLauncher's native storage.
XLM's upstream frontend auto-update is disabled while this frontend is installed.
Frontend updates come with manager releases; game and Dalamud updates remain native.
Open with --login-settings to edit saved login choices without starting the game.

Corresponding source is included in accessible-launcher-source.tar.gz.
Build with .NET 10: dotnet publish src/XIVLauncher.Core/XIVLauncher.Core.csproj
  -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=false
The manager also supplies its patched Avalonia 12.1.3 AT-SPI bridge; its build and
patch are included under accessibility-build/ in the source archive.
To rebuild that bridge separately, clone AvaloniaUI/Avalonia at
8eeda4f6f546165b3f72e63c9f42247abb306905 with its submodules, apply
accessibility-build/installer/linux/patches/avalonia-atspi-focus.patch, then run:
dotnet build src/Avalonia.FreeDesktop.AtSpi/Avalonia.FreeDesktop.AtSpi.csproj
  -c Release -f net10.0
Copy the resulting Avalonia.FreeDesktop.AtSpi.dll beside XIVLauncher.Core.
The included manager integration script also runs manager-specific tests and
therefore requires the complete AccessibilityModManager repository.
''')
project = Path(__file__).resolve().parents[3]
with tarfile.open(destination / "accessible-launcher-source.tar.gz", "w:gz") as archive:
    # Only source-controlled upstream inputs and our added frontend; exclude build output.
    for repo, prefix in ((source, ""), (source / "lib/FFXIVQuickLauncher", "lib/FFXIVQuickLauncher/")):
        names = subprocess.check_output(["git", "-C", str(repo), "ls-files", "-z"])
        for name in names.decode().split("\0"):
            path = repo / name
            if name and path.is_file(): archive.add(path, arcname=prefix + name)
    for path in sorted((source / "src/XIVLauncher.Core/Accessible").glob("*.cs")):
        archive.add(path, arcname=str(path.relative_to(source)))
    for relative in ("installer/linux/build-avalonia-atspi.sh", "installer/linux/patches/avalonia-atspi-focus.patch"):
        archive.add(project / relative, arcname="accessibility-build/" + relative)
files = {}
for path in sorted(destination.rglob("*")):
    if path.is_file() and path.name != "amm-frontend.json":
        files[path.relative_to(destination).as_posix()] = hashlib.sha256(path.read_bytes()).hexdigest()
(destination / "amm-frontend.json").write_text(json.dumps({"Version": "1.4.0-amm3", "Files": files}, indent=2) + "\n")
print("Accessible XIVLauncher bundle verified and indexed:", len(files), "files")
