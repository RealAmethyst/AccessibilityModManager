#!/usr/bin/env python3
"""Build a self-contained author CLI ZIP with its complete documentation. Never uploads."""
import argparse
from datetime import datetime, timezone
import hashlib
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--runtime", choices=["win-x64", "linux-x64"], required=True)
    parser.add_argument("--configuration", default="Release")
    parser.add_argument("--version")
    parser.add_argument("--prepared", type=Path, help="Package an existing publish directory.")
    parser.add_argument("--dist", type=Path)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    project = root / "src/AccessibilityModManager.AuthorCli/AccessibilityModManager.AuthorCli.csproj"
    version = args.version or ET.parse(project).findtext(".//Version")
    if not version or any(c not in "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ.-" for c in version):
        parser.error("Version must contain only letters, numbers, dots, or hyphens.")
    dist = args.dist or root / "dist"
    dist.mkdir(parents=True, exist_ok=True)
    publish = root / "publish"
    publish.mkdir(exist_ok=True)
    stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%S%fZ")
    with tempfile.TemporaryDirectory(prefix="author-cli-", dir=publish) as temporary:
        stage = Path(temporary) / "package"
        if args.prepared:
            shutil.copytree(args.prepared, stage)
        else:
            subprocess.run([
                os.environ.get("DOTNET_BIN", "dotnet"), "publish", str(project),
                "-c", args.configuration, "-r", args.runtime, "--self-contained", "true",
                "-p:PublishSingleFile=true", "-p:IncludeNativeLibrariesForSelfExtract=true",
                "-p:DebugType=none", "-p:DebugSymbols=false", f"-p:Version={version}",
                "-o", str(stage), "--verbosity", "quiet",
            ], check=True)
        executable = stage / ("amm-author.exe" if args.runtime == "win-x64" else "amm-author")
        if not executable.is_file():
            raise RuntimeError(f"Missing CLI executable: {executable}")
        shutil.copytree(root / "docs", stage / "docs", dirs_exist_ok=True)
        shutil.copy2(root / "LICENSE", stage / "LICENSE")
        (stage / "README.md").write_text(
            f"# Author CLI {version}\n\n"
            "Create and maintain Accessibility Mod Manager catalogs on GitHub.\n\n"
            "## Getting started\n\n"
            "Read [the author guide](docs/README.md) for setup and publishing instructions.\n\n"
            "Run `amm-author help` (`./amm-author help` on Linux) to list commands.\n"
            "The complete documentation and JSON examples are in the `docs` folder.\n",
            encoding="utf-8",
        )
        (stage / "INSTALL.txt").write_text(
            f"Author CLI {version} ({args.runtime})\n\n"
            "Extract the complete ZIP into a new folder. The .NET runtime is included.\n"
            "Install Git and GitHub CLI, then run gh auth login once for GitHub publishing.\n"
            "Run amm-author help (./amm-author help on Linux).\n"
            "On Linux, run chmod +x amm-author if your ZIP extractor did not preserve permissions.\n"
            "Start with docs/README.md. Keep docs beside the executable.\n"
            "To upgrade, keep your previous extracted folder as a backup and extract the new ZIP separately.\n"
            "Author projects and configuration are stored separately from this folder.\n",
            encoding="utf-8",
        )
        name = f"amm-author-{version}-{args.runtime}"
        archive = Path(temporary) / (name + ".zip")
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as output:
            for source in sorted(stage.rglob("*")):
                if source.is_file():
                    output.write(source, Path(name) / source.relative_to(stage))
        destination = dist / archive.name
        checksum = destination.with_suffix(".zip.sha256")
        if destination.exists() or checksum.exists():
            backup = publish / "release-backups" / stamp
            backup.mkdir(parents=True)
            for old in (destination, checksum):
                if old.exists():
                    shutil.copy2(old, backup / old.name)
        # Move a complete archive into place only after packaging succeeds.
        os.replace(archive, destination)
        with destination.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        checksum.write_text(f"{digest}  {destination.name}\n", encoding="utf-8")
        print(f"Package: {destination}\nSHA-256: {digest}")


if __name__ == "__main__":
    main()
