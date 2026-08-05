# Accessibility Mod Manager

A Windows app that installs and updates accessibility mods for games. Pick a game, pick a mod, click Install — the manager downloads the package, verifies it, and applies it. One click to update; one click to uninstall (with full restore from backup).

It's built around a community plugin system: each plugin author runs their own GitHub-hosted index of releases, and the manager talks to all of them through a signed, central registry of trusted plugins.

## Install

Grab the latest installer from the [Releases page](https://github.com/RealAmethyst/AccessibilityModManager/releases) — `AccessibilityModManager-{version}-Setup.exe`. Requires Windows 10/11 and the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0/runtime) (the installer points you there if you don't have it). Auto-update is built in: when a new version ships, the manager prompts you on launch.

## How it works

- **Browse mods** by game, language, or accessibility tag (screen-reader support, controller-only, completable, etc.)
- **Detect installs** automatically through Steam — or browse to a folder if you installed elsewhere
- **Install / update / uninstall** with one click. Files removed at uninstall come back from a per-install backup; replaced files are restored to their original bytes.
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

## Getting your plugin listed

1. Make a dedicated GitHub repo for your plugin index (one repo per plugin author works well — it can be separate from the repos that hold your actual mod code).
2. Open the AuthorTool on that project. The tool checks the public registry on load and shows a banner with your status: listed, not listed, or unreachable. If you're not listed yet, the **Request listing** button opens a pre-filled GitHub issue on the registry repo with your plugin id, display name, and repo URL ready to submit — no manual issue-writing needed.
3. Once the registry maintainer signs your entry into the registry, the manager picks it up automatically on the next refresh, and the AuthorTool's banner flips to "listed".

## The AuthorTool

`PluginIndexAuthor-{version}.exe` (next to the manager installer on the [Releases page](https://github.com/RealAmethyst/AccessibilityModManager/releases)) is a small WPF app that handles the entire publishing workflow for you. It uses the `gh` CLI under the hood for all GitHub interaction; install [GitHub CLI](https://cli.github.com/) and run `gh auth login` once before using it. This now also supports placing tester builds behind your own Patreon community, meaning people will need to have access to your Patreon tier that you select before the mod release shows up in the manager for them.

What the tool gives you:

- **Edit your plugin index** — add games, fill in display names, descriptions, tags, languages, dependencies. The tool writes a valid `index.json` for you so you never have to hand-edit JSON.
- **Build wrapped ZIPs** — point the tool at a folder containing your mod's files. It generates the manager's `manifest.json`, validates lifecycle scripts, and produces a SHA256-stable ZIP ready to upload.
- **Upload releases to your mod's own GitHub repo** — the tool uses `gh` to create a GitHub release on your mod's repo and attach the wrapped ZIP as an asset, then writes the resulting public URL + SHA256 back into your plugin index. This is intentional: your mod stays on its own repo (where your users already look for it), and your plugin index simply points at those release assets. The plugin index repo itself is *not* released — it's just a regular `git commit` + `git push` of the updated `index.json`. One click does the asset upload, the index commit, and the index push together, so the SHA256 in your plugin index always matches the asset that's live on GitHub.
- **Lifecycle script editor** — fill in the executable path, the what / why / modifies descriptions, and whether the script needs admin. The tool validates that each declared script is actually bundled in your source folder before producing the ZIP.

## Author CLI (local tooling)

This branch also contains `amm-author`, a command-line counterpart to the WPF AuthorTool. It uses the same authoring services and validation rules, but it does not launch the graphical app. The CLI is local tooling in this branch, not an official binary published by the upstream project.

Build self-contained Windows x64 executables with:

```powershell
powershell -ExecutionPolicy Bypass -File installer\build-author-cli.ps1 -SelfContained
powershell -ExecutionPolicy Bypass -File installer\build-author-cli.ps1 -SelfContained -Admin
```

The standard executable is written to `dist-author-cli\amm-author.exe`. The registry-admin build is written separately to `dist-author-cli-admin\amm-author-admin.exe`. Each folder also receives a `.sha256` file. The build script does not create a GitHub release or upload anything.

Copy the executables and hash files to a folder on your user `PATH`, then run `amm-author --help` or `amm-author-admin --help`. A self-contained build does not require the .NET Desktop Runtime on the destination machine.

### Projects and output

Commands that need an author project resolve it in this order:

1. The folder supplied with `--project`.
2. The current directory, if it contains an `index.json` project.
3. The last project opened by the AuthorTool or `project open`.

The global options can appear before or after a subcommand:

- `--json` writes machine-readable JSON.
- `--quiet` suppresses ordinary human status lines, but not warnings or errors.
- `--dry-run` validates and previews without making durable changes.
- `--yes` confirms an operation after validation; it does not bypass trust or safety checks.
- `--verbose` includes exception details when a command fails.

Human output is plain text with no ANSI control sequences, so it remains predictable in screen readers and redirected logs. JSON mode keeps standard output parseable for scripts.

Passphrases are never accepted as ordinary command-line values. Interactive prompts conceal them. For automation, redirect standard input and use the command's explicit `--passphrase-stdin` or `--passphrases-stdin` option. Do not put a secret in a JSON input file, shell history, or process argument.

The process exit codes are `0` for success, `2` for command usage, `3` for validation failure, `4` for authentication or an unavailable privileged operation, `5` for a conflict or missing confirmation, and `130` for cancellation.

### Command groups

- `project` creates, opens, clones, pulls, and inspects author projects.
- `author` reads or changes the author block in `index.json`.
- `game` reads or changes game entries.
- `dependency` reads or changes a game's dependencies.
- `script` reads or changes default lifecycle scripts.
- `package` builds, validates, and hashes wrapped mod packages.
- `release` reads, edits, uploads, and completes release publication.
- `index` inspects, reconciles, saves, publishes, and manages index locks.
- `github` checks GitHub CLI authentication and lists repositories or releases.
- `patreon` manages the local Patreon session and reads creator posts or tiers.
- `server` configures and operates the SFTP publishing destination.
- `signing` manages catalog signing keys, claims, and publisher-head recovery.
- `registry` maintains the signed global registry. Every registry operation requires `amm-author-admin`.

Use `--help` at any level for the exact arguments and a concrete example, such as `amm-author release publish --help`.

### Examples

Inspect a project as JSON:

```powershell
amm-author project status --project "C:\Mods\Sample" --json --quiet
```

Build a wrapped package:

```powershell
amm-author package build --source "C:\Mods\Sample\Files" --game sample-game --version 1.0.0 --output "C:\Packages\sample.zip" --project "C:\Mods\Sample"
```

Validate that package without changing the project:

```powershell
amm-author package validate --file "C:\Packages\sample.zip" --json
```

Preview an index publication without committing or pushing:

```powershell
amm-author index publish --project "C:\Mods\Sample" --dry-run
```

Publish a release after reviewing its destination:

```powershell
amm-author release publish --game sample-game --version 1.0.0 --channel stable --repo owner/sample-mod --zip "C:\Packages\sample.zip" --project "C:\Mods\Sample" --yes
```

The repository is source-available under [LICENSE](LICENSE). Building these programs for local use does not grant redistribution rights beyond that license.

## Releasing a new version

1. Open the AuthorTool, open your plugin project (the folder with `index.json`).
2. Pick the game, click **Add release**.
3. Type the version, pick the GitHub repo for the mod, click **Build…**
4. Point at the source folder containing your mod's files; the tool wraps it into `{game}-v{version}-amm.zip`.
5. Click **Upload and save**. The tool creates / updates the GitHub release on the mod's repo, attaches the wrapped ZIP as an asset, and stages the new entry in your plugin index.
6. Confirm the **commit and push** prompt — your `index.json` gets committed and pushed to your plugin-index repo (a normal commit, not a GitHub release).
7. Users see the update on their next manager refresh.

## Building from source

```
dotnet build AccessibilityModManager.slnx
dotnet test AccessibilityModManager.slnx
powershell -ExecutionPolicy Bypass -File installer\build.ps1            # manager + Inno installer
powershell -ExecutionPolicy Bypass -File installer\build-author-tool.ps1 # AuthorTool single-file exe
powershell -ExecutionPolicy Bypass -File installer\build-author-cli.ps1 -SelfContained # local Author CLI
```

Targets `net10.0-windows`. Requires .NET 10 SDK and (for the installer) [Inno Setup 6](https://jrsoftware.org/isdl.php).

## License

Source-available — see [LICENSE](LICENSE). The security mechanisms (signature verification, SHA256 gates, zip-slip prevention, manifest allowlisting, receipt tamper detection) are protected; plugin authors retain rights to their own mod content.
