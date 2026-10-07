#!/usr/bin/env python3
"""Create separately installable, self-contained manager and AuthorTool downloads."""
from datetime import datetime, timezone
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import tarfile
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("project", type=Path)
parser.add_argument("build", type=Path)
parser.add_argument("dist", type=Path)
parser.add_argument("--product", choices=["manager", "author"])
args = parser.parse_args()
project, build, dist = args.project, args.build, args.dist
for folder, project_name, product, filename, executable in [
    ("manager", "LinuxApp", "AccessibilityModManager", "AccessibilityModManager", "AccessibilityModManager.LinuxApp"),
    ("author", "LinuxAuthorTool", "AccessibilityModManager-Author", "PluginIndexAuthor", "AccessibilityModManager.LinuxAuthorTool"),
]:
    if args.product is not None and folder != args.product:
        continue
    version = ET.parse(project / "src" / f"AccessibilityModManager.{project_name}" /
                       f"AccessibilityModManager.{project_name}.csproj").findtext(".//Version")
    if not version:
        raise SystemExit("Missing release version")
    source = build / folder
    (source / "release.json").write_text(json.dumps({"Product": product, "Version": version, "RuntimeIdentifier": "linux-x64"}) + "\n")
    (source / "install.sh").write_text(f'''#!/bin/sh
set -eu
cd -P -- "$(dirname -- "$0")"
exec "./{executable}" --install
''')
    (source / "install.sh").chmod(0o755)
    (source / "INSTALL.txt").write_text(f'''{filename} {version} for x86-64 Linux

Extract this archive and run ./install.sh from its folder. Do not use sudo.
Then open {"Accessibility Mod Manager" if folder == "manager" else "Plugin Index Author"} from your application menu (Meta key).
The .NET runtime is included. A graphical Linux desktop is required to run the app.

The installer adds an application-menu shortcut and installs below
$XDG_DATA_HOME/{product}/linux-x64, or ~/.local/share/{product}/linux-x64.
It preserves your settings, sign-ins, mod receipts and installed game files.
An existing app build is backed up beside the installation before replacement.
Close the application before running install.sh again to upgrade manually.

{"The manager checks for updates at startup. You can also use Settings, Check for manager updates. Installation requires your confirmation." if folder == "manager" else "For an AuthorTool update, download and extract its new archive and run install.sh again."}

To remove the application, delete only its linux-x64 installation directory and
$XDG_DATA_HOME/applications/{"accessibility-mod-manager.desktop" if folder == "manager" else "accessibility-mod-author.desktop"}
(or the same path under ~/.local/share/applications).
Keep the application's other data folders: they contain settings and mod ownership records.
''')
    shutil.copyfile(project / "LICENSE", source / "LICENSE")
    archive_name = f"{filename}-{version}-linux-x64"
    destination = dist / (archive_name + ".tar.gz")
    temporary = destination.with_suffix(destination.suffix + ".partial")
    try:
        with tarfile.open(temporary, "w:gz", format=tarfile.PAX_FORMAT) as archive:
            archive.add(source, arcname=archive_name)
        if destination.exists():
            backup = project / "publish" / "release-backups" / datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
            backup.mkdir(parents=True)
            destination.rename(backup / destination.name)
            old_hash = destination.with_suffix(destination.suffix + ".sha256")
            if old_hash.exists():
                old_hash.rename(backup / old_hash.name)
        temporary.rename(destination)
    finally:
        temporary.unlink(missing_ok=True)
    with destination.open("rb") as content:
        digest = hashlib.file_digest(content, "sha256").hexdigest()
    destination.with_suffix(destination.suffix + ".sha256").write_text(digest + "  " + destination.name + "\n")
    print(f"Release: {destination}\nSHA-256: {digest}")
