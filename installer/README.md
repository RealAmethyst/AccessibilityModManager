# Build release downloads

Release artifacts belong directly in `dist/`. Intermediate publish folders, tool
caches and previous build backups belong outside `dist/`. Building does not upload
or publish a GitHub release.

## Windows

Run `installer/build.ps1` with the .NET 10 SDK and Inno Setup 6 installed. It builds
the manager installer and its SHA-256 file. Windows needs the .NET 10 Desktop
Runtime; the installer checks for it before installing.

Run `installer/build-author-tool.ps1 -SelfContained` for the standalone AuthorTool
executable and SHA-256 file. Both the executable version and filename use the
requested version, or the version from the AuthorTool project when omitted.

## Linux

Run `installer/linux/build-linux.sh`. Set `DOTNET_BIN` if the .NET 10 SDK is not on
PATH. The build also uses a C compiler, Python 3, Git, curl and tar. It verifies the
pinned Avalonia accessibility bridge source and bundled GitHub CLI.

The build produces two separate self-contained x86-64 Linux archives and their
SHA-256 files: `AccessibilityModManager-VERSION-linux-x64.tar.gz` and
`PluginIndexAuthor-VERSION-linux-x64.tar.gz`. No .NET installation is needed on the
user's machine. Each archive contains `install.sh`, `INSTALL.txt`, the license,
release metadata and the application files.

Extract the appropriate archive and run `./install.sh` without sudo. It installs
below the user's XDG data directory and registers a desktop application entry.
The default location is `~/.local/share`. Upgrades back up the old application,
replace only its program directory and shortcut, and preserve settings and mod
ownership records. Close the application before a manual upgrade. The manager's
in-app updater handles the close and restart itself.

For a development-machine install of both freshly built apps, run
`installer/linux/install-user.sh`. Build trees are under `publish/linux`. Rebuilding
an existing release filename preserves the previous archive under
`publish/release-backups` before replacement; do not silently replace already
published GitHub assets with different contents.

## GitHub update assets

Keep the Windows and Linux manager project versions equal. Publish both manager
packages and their matching checksum files in the same stable GitHub release,
with tag `vVERSION`:

- `AccessibilityModManager-VERSION-Setup.exe`
- `AccessibilityModManager-VERSION-Setup.exe.sha256`
- `AccessibilityModManager-VERSION-linux-x64.tar.gz`
- `AccessibilityModManager-VERSION-linux-x64.tar.gz.sha256`

The manager checks the latest stable release in
`RealAmethyst/AccessibilityModManager`. Each platform requires its exact product,
version and runtime filename plus exactly one matching checksum. It never falls
back to the other platform or the AuthorTool. Missing platform assets mean no
compatible update is offered. Preview and draft releases are not installed.
Checksums are verified before installation; update discovery trusts the official
GitHub HTTPS release endpoints, as the existing Windows updater does.

Linux offers updates at startup and from Settings. Installation requires the
user's confirmation, keeps the old build as a backup, and restarts the manager.
The Linux AuthorTool uses its separate manual installer; it is not replaced by a
manager update.

The menu shortcut follows the
[Desktop Entry Specification](https://specifications.freedesktop.org/desktop-entry/latest/exec-variables.html),
including quoting for installation paths with spaces and shell-special characters.

The Linux packaging helper also accepts `--product manager` or `--product author` when
packaging an already prepared build tree, so a manager-only release does not replace
unchanged AuthorTool artifacts.
