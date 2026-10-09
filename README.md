# Accessibility Mod Manager

A Windows and Linux app that installs and updates accessibility mods for games. Pick a game, pick a mod, click Install — the manager downloads the package, verifies it, and applies it. One click to update; one click to uninstall (with full restore from backup).

It's built around a community plugin system: each plugin author runs their own GitHub-hosted index of releases, and the manager talks to all of them through a signed, central registry of trusted plugins.

## Install on Windows

Grab the latest installer from the [Releases page](https://github.com/RealAmethyst/AccessibilityModManager/releases) — `AccessibilityModManager-{version}-Setup.exe`. Requires Windows 10/11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0/runtime) (the installer points you there if you don't have it). Auto-update is built in: when a new version ships, the manager prompts you on launch.

## Install on Linux

Extract `AccessibilityModManager-{version}-linux-x64.tar.gz`, then run `./install.sh`
from the extracted folder without sudo. Open **Accessibility Mod Manager** from
your application menu (Meta key). The x86-64 Linux package includes its .NET runtime.
Updates are offered on startup and through **Settings, Check for manager updates**.
Installing an update preserves your data, backs up the previous build and restarts
the manager. Windows and Linux select only their own update package.

The authoring tool is a separate command-line download: `amm-author-{version}-linux-x64.zip` or `amm-author-{version}-win-x64.zip`. Each ZIP includes its runtime and full `docs` folder.

## How it works

The Linux manager has a self-contained download with a per-user application-menu launcher. It supports the signed catalog, user-added sources, Patreon through Secret Service, native Linux packages, and reversible Steam Proton setup. Verified Windows file-copy packages can be adapted when their loader and speech dependencies have a supported Proton path. Windows elevation and lifecycle installers do not become Linux host privileges. See [Linux port](LINUX_PORT.md) and [Steam Proton loader backend](PROTON_LOADER_BACKEND.md) for compatibility limits.

Third-party authors use the cross-platform CLI described below. Amethyst maintains her own catalog through the separate web dashboard.

- **Browse mods** by game, language, or accessibility tag (screen-reader support, controller-only, completable, etc.)
- **Detect installs** automatically through Steam — or browse to a folder if you installed elsewhere
- **Install / update / uninstall** with one click. Files removed at uninstall come back from a per-install backup; replaced files are restored to their original bytes.
- **Dependency updates on Play**: Windows and Linux compare the published dependency URL and SHA-256 with the installed record and prompt before downloading an update, even if the mod version is unchanged. The network check has a five-second limit. Portable emulator updates preserve unrelated files and back up replacements. Older emulator installations need one update to establish their download record.
- **Dependency checks** before install: the manager automatically installs dependencies when a mod needs MelonLoader, BepInEx ETC. Developers must specify this

---

# For developers

If you want to publish accessibility mods through the manager, this section is for you.

## How verification works

The manager refuses to do anything that isn't verifiable end-to-end:

1. **Registry signature** — the central plugin registry (a JSON list of trusted plugin repos) is signed with RSA-PSS/SHA256 using a key whose public half ships inside the manager binary. The manager verifies the registry's signature on every fetch; an unsigned or tampered registry is rejected outright.
2. **Plugin index over HTTPS** — every plugin URL must be `https://`. Plain HTTP is refused.
3. **Per-release SHA256** — every mod ZIP referenced in a plugin index has a SHA256 in the index. After download the manager rehashes the file; mismatches abort the install. This gate is not skippable.
4. **ZIP extraction is zip-slip safe** — entries that would resolve outside the staging directory are rejected before any file is written.
5. **Manifest actions are allowlisted** — only `copyFile`, `copyFolder`, and `replaceFile` are recognized. The manifest itself can't run code.
6. **Lifecycle scripts (optional)** — pre-install, post-install, and post-uninstall scripts are supported, but the user must explicitly confirm them on a warning dialog that lists each script's path, what it does, why it's needed, what it modifies, and whether it needs admin. Failures roll back the install.
7. **Receipts are tamper-checked** — every install writes a JSON receipt with a SHA256 hash file alongside it. If a receipt is edited after the fact, the manager refuses to use it for uninstall.

## The author CLI

`amm-author` replaces both desktop AuthorTool interfaces. It manages public GitHub plugin repositories, games, releases, dependencies, lifecycle scripts, and manager-format ZIPs. It is designed for direct use and for AI assistants working through commands in the background.

Install Git and [GitHub CLI](https://cli.github.com/), then run `gh auth login` once. Extract the complete CLI ZIP; its .NET runtime and documentation are included. Run `./amm-author help` on Linux or `./amm-author.exe help` in PowerShell. Use `--json` for automation, `--dry-run` to preview operations, and command-specific `--help` for arguments.

A typical workflow, using `amm-author` from PATH:

```sh
amm-author project create YOUR-NAME/accessibility-mods --plugin-id your-plugin --project ./catalog --yes
amm-author game add --id example-game --display-name "Example Game" --project ./catalog
amm-author game repo example-game --repo YOUR-NAME/example-game-mod --project ./catalog
amm-author package build --source ./mod-files --game example-game --version 1.0.0 --output ./mod-1.0.0.zip --project ./catalog
amm-author release publish --game example-game --version 1.0.0 --channel stable --zip ./mod-1.0.0.zip --project ./catalog --dry-run
amm-author release publish --game example-game --version 1.0.0 --channel stable --zip ./mod-1.0.0.zip --project ./catalog --yes
```

Use `project clone OWNER/REPO --project PATH` for an existing catalog. Each game can use its own GitHub release repository; the catalog repository receives the updated `index.json` as a normal commit and push. Package hashes and manager validation remain mandatory. The tool reports partial publication failures rather than calling them complete.

Patreon releases use post links and manually uploaded attachments. Users download in their browser and select the ZIP for the manager to verify and install. The CLI does not promise automatic attachment downloads. Custom-server publishing and registry administration are absent from third-party authoring; Amethyst's dashboard and existing manager downloads remain supported.

## Author documentation

Start with [the author guide](docs/README.md) and [publishing walkthrough](docs/publishing.md). The documentation explains [catalogs and packages](docs/catalog.md), [dependencies and why to keep them separate](docs/dependencies.md), [lifecycle scripts](docs/scripts.md), and [Patreon](docs/patreon.md). The [command reference](docs/commands.md) lists all commands, options, JSON behavior, and exit codes. The complete `docs` folder and editable examples ship in each CLI ZIP.

## Sharing your plugin

Publishing through the CLI submits your public default-branch catalog to the manager's plugin directory. Users open Sources in the Authors tab and choose Add after reviewing the source notice. Already-added sources offer Remove, including sources added before the directory existed. A listing is not an endorsement.

The manager announces newly added games for sources you added and authors whose mods you have installed. Your first refresh establishes a baseline; repeat refreshes do not repeat announcements. Directory-only authors do not generate game notifications.

## Building from source

```
dotnet build AccessibilityModManager.slnx
dotnet test AccessibilityModManager.slnx
powershell -ExecutionPolicy Bypass -File installer\build.ps1            # manager + Inno installer
python3 installer/build-author-cli.py --runtime win-x64                # CLI ZIP with docs
```

The Windows applications target `net10.0-windows` and require the .NET 10 SDK; the installer also requires [Inno Setup 6](https://jrsoftware.org/isdl.php). On Linux, build the cross-platform projects and package the Linux manager and author CLI with:

```bash
dotnet build AccessibilityModManager.slnx -p:EnableWindowsTargeting=true
installer/linux/build-linux.sh
installer/linux/install-user.sh
```

Linux packages are self-contained and install for the current user. Building them needs the .NET 10 SDK, a C compiler, Python 3, Git, `curl`, `tar`, and `sha256sum`. See [release packaging](installer/README.md) for the `dist/` artifacts and platform-specific update naming. For CLI-only builds, run `python3 installer/build-author-cli.py --runtime linux-x64` or `--runtime win-x64`. This produces self-contained ZIPs with the entire documentation folder and checksums, without publishing them. Git and GitHub CLI are prerequisites for repository operations.

Run the portable suites on Linux:

```sh
dotnet test tests/AccessibilityModManager.AuthorCli.Tests
dotnet test tests/AccessibilityModManager.PortableTests
```

The Windows manager test project remains in the solution and requires Windows to execute. The CLI test project runs on both platforms.

## License

Source-available — see [LICENSE](LICENSE). The security mechanisms (signature verification, SHA256 gates, zip-slip prevention, manifest allowlisting, receipt tamper detection) are protected; plugin authors retain rights to their own mod content.
