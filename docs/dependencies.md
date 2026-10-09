# Dependencies

## What a dependency is

A dependency is software your mod needs but does not itself provide: for example BepInEx, MelonLoader, a .NET runtime, or a portable emulator. A dependency declaration tells the manager how to recognize it and, where supported, how to install a specific verified download.

## Why declare it separately

Keeping a loader or runtime separate avoids putting the same large files in every mod ZIP. It makes the requirement visible before installation and allows the manager to check whether it is already present. The manager can detect published dependency URL/hash changes and offer supported dependency updates independently of the mod version.

Separate downloads also make the upstream version and source explicit. Pin the exact artifact and its SHA-256; a moving "latest" link alone is not enough for automatic installation. Do not silently change the bytes behind an existing pinned URL.

Bundling can still be appropriate when your mod requires a modified component, an inseparable matched version, or an upstream download cannot be redistributed separately. Verify its license and compatibility. If it is simply part of your mod's files, package it normally; do not declare an automatic download that conflicts with the bundled copy.

## Main fields

- `id`: stable dependency identifier, such as `bepinex`.
- `type`: `framework` for loaders or `system` for runtimes and similar prerequisites.
- `required`: whether installation depends on satisfying it.
- `targetPlatforms`: optional list of `windows`, `proton`, or `linux` targets. Legacy entries without it keep the manager's legacy targeting behavior.
- `minVersion`: optional minimum version requirement.
- `check`: a game-relative file check or supported Windows registry check.
- `fix.downloadUrl`: HTTPS location for the prerequisite. Automatic installation needs the direct artifact URL.
- `fix.autoInstall`: supported installation method plus the downloaded artifact's `sha256`.
- `isGameInstaller`: marks a dependency that installs the game/application itself. Use only with the manager's corresponding detection and installation workflow.
- `versionDiscovery`: authoring metadata describing an upstream GitHub repository or release-asset pattern. It does not authorize the manager to install arbitrary latest releases.

## Installation methods

- `extractZip`: extract a pinned archive into a game-relative `targetDir`. An optional blocklist excludes unwanted entries.
- `copyFile`: download one pinned file to a chosen game-relative directory and filename.
- `runInstaller`: run a pinned installer with declared arguments and elevation needs. Windows installers do not grant Linux host privileges.
- `extractApp`: install a portable application such as an emulator. The game definition must describe how it is found and launched.

The manager also has specialized Linux XIVLauncher dependency metadata under `fix.xlm`; it is not a generic installer escape hatch.

## Add a dependency

```sh
amm-author dependency presets --json
amm-author dependency apply-preset example-game bepinex --project ./catalog
amm-author dependency show example-game bepinex --project ./catalog --json
```

Preset identifiers are listed by `dependency presets`; use the returned identifier. Presets are starting points. Some require a real URL and SHA-256 before they can pass publication checks.

For a complete definition, copy and edit `docs/examples/dependency.json`:

```sh
amm-author package hash --file ./upstream-loader.zip
amm-author dependency set example-game --input ./dependency.json --project ./catalog
```

The example uses a deliberately invalid placeholder hash and URL. Replace both with your verified upstream download. Editing commands can save incomplete dependency drafts; `index validate` and package validation reject incomplete automatic downloads. `dependency set` replaces the dependency with the same ID; preserve any fields you still need. `dependency remove GAME ID` removes it from future catalog/package authoring.

Rebuild the package after changing dependencies, then validate both the package and catalog.
