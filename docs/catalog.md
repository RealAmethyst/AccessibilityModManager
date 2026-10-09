# Catalogs, games, and packages

## Plugin catalog

`index.json` describes your plugin's games, author profile, dependency presets, and releases. `pluginId` is your stable identifier; changing it creates a different identity for the manager. `repoVersion` is the catalog format version, not your mod's release number. `generatedAt` records when the catalog was updated.

Author profile fields include `displayName`, `bio`, `websiteUrl`, `discordUrl`, `patreonUrl`, `gitHubUrl`, and `donationUrl`. They are optional links and text shown to users, not download destinations.

## Game

Each game has a stable `gameId` and a human-readable `displayName`. `modName` and `description` describe your mod. `steamAppId` and `exeName` help the manager find and launch the game. `probeRules` check game-relative files or directories; `registryProbe` supports Windows games whose installers register their location.

`linuxSteamAppId`, `linuxExeName`, and `linuxProbeRules` describe Linux-specific detection. `tags` describe accessibility features and `languages` list supported language codes. Use only claims your mod actually supports.

Dependencies describe required external software. Default lifecycle scripts are authoring templates copied into newly built manifests. The manager does not substitute current catalog script defaults into an old package.

Renaming a game ID also needs its release records to agree. Use `game update --help` for the explicit release-ID rewrite option; changing only a label does not require changing the ID.

## Release

A release records `gameId`, `pluginId`, `version`, `channel`, `sha256`, optional notes and compatibility, and either a public `packageUrl` or Patreon metadata. The version in the ZIP's manifest must match the release entry. The SHA-256 identifies the actual ZIP bytes, not the extracted files or source folder.

Use `stable` for ordinary releases and `beta` for test builds. Release records are stored by game. `release add` and `release edit` take complete JSON records and only change the local catalog; they do not upload a ZIP. `release upload` uploads a validated GitHub asset without changing the catalog. `release publish` performs the combined workflow.

Advanced JSON records can retain `compatibility` information and a distinct `linuxPackage` for the same release. When editing complete JSON, preserve existing package variants. The combined publisher creates a single-package release; use explicit release editing to manage a separate Linux variant.

## Package

A manager package has `manifest.json` at its root. Ordinary mod files live under `files/`. Manifest actions copy individual files or folders, or replace files with backup behavior. The manager applies those actions against the chosen game installation, maintains receipts and backups, and uses them for updates and uninstalling.

The author CLI calls the same manifest parser and package validator as the manager. Checks include identity, safe paths, duplicate archive entries, missing action sources, script files and descriptions, platform launch assets, and verification rules. Catalog checks catch invalid metadata before publication. Public GitHub downloads are checked against the package hash before the combined publisher advertises them.

Offline validation cannot inspect a user's actual game installation, determine conflicts with every installed mod, or prove the mod's code and speech work. Those still require testing in the manager and game.

## Platform support

`package build` accepts `--target-platform windows`, `proton`, `linux`, `windows-linux`, or `xivlauncher`. Omitting it preserves the legacy Windows default. Dependencies can select `targetPlatforms` so a Windows runtime is not accidentally installed as a native Linux dependency.

Explicit Proton launch configuration can be supplied as `--proton-config FILE`, using the manager's `ProtonLaunchConfig` model. Specialized XIVLauncher configuration uses `--target-platform xivlauncher --xivlauncher-config FILE`. The builder verifies the configuration through the current manager parser and checks required package assets.

The release uploader derives its target platform from the validated manifest. A Windows script does not become a Linux host script. The current lifecycle script format supports `.exe`, `.ps1`, `.cmd`, and `.bat`; native Linux and Proton packages must satisfy the manager's platform rules.

## Trust and listing

The manager keeps its existing HTTPS, registry-signature, catalog-trust, package-hash, extraction, script-consent, and receipt checks. The CLI cannot publish an unsigned replacement over a catalog anchored to a signing key. Amethyst's signed server catalog remains managed through her dashboard.

Third-party catalog publication checks the signed registry for identity conflicts even when the plugin is not listed. Registry outages or invalid signatures can therefore block publication. This protects existing identities; it is separate from the future public plugin directory.
