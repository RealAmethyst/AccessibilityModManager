# Optional Dependency Selection and Final Fantasy VII 7th Heaven Design

## Summary

Accessibility Mod Manager currently prompts only for missing required dependencies. The live Blind Soldier catalog therefore marked a package named `seventhheaven` as required even though the package was actually FFNx. That combination causes three user-visible failures: FFVII 2013 and FFVII 2026 can be mistaken for one another, FFNx is installed into the wrong edition layout, and a user who does not want 7th Heaven cannot decline it while still installing Blind Soldier.

The fix has two coordinated parts:

1. Accessibility Mod Manager will offer missing auto-installable optional dependencies in its existing dependency consent dialog.
2. The Blind Soldier catalog will identify the native 2013 and 2026 runtimes using edition-specific files, plus expose a separate 2013 compatibility-runtime entry for people who want 7th Heaven with the Steam 2026 installation.

The native 2026 entry never offers or installs 7th Heaven or FFNx. A real 2013 install offers both components as optional choices. The 2013 compatibility entry installs both before the x86 Blind Soldier payload, placing FFNx in `ff7/workingdir` where the embedded runtime expects it.

## User Experience

When the user installs Blind Soldier:

- Missing required dependencies remain selected and cannot be unchecked.
- Missing optional dependencies that have an automatic installer appear in the same dialog, unchecked by default.
- Each dependency is a native WPF checkbox exposed to screen readers as required or optional and selected or not selected.
- The primary action is `Continue`, with the accessible name `Continue with selected dependencies`.
- Continuing with no optional boxes checked installs Blind Soldier normally.
- Canceling the dialog cancels the entire mod installation.
- Already-installed optional dependencies are not offered again.

If a selected optional dependency fails or the user cancels its third-party installer, Accessibility Mod Manager announces and logs the failure but continues installing the core mod. Required dependency failures remain fatal.

## Manager Data Contract

`IDependencyHost.ConfirmDependencyInstallAsync` will return a decision object rather than a Boolean. The decision contains:

- whether the user accepted the dialog; and
- the IDs of optional dependencies the user selected.

The prompt item explicitly reports whether a dependency is required. Required selections are enforced by the engine, not trusted to the UI. This prevents an alternate host implementation from omitting a required dependency.

The engine builds one manifest-ordered prompt from:

- every missing required dependency with `AutoInstall`; and
- every missing optional dependency with `AutoInstall`.

Manual-only optional dependencies are not offered in this iteration. Existing required manual-dependency behavior is unchanged.

After acceptance, the engine processes dependencies in manifest order:

- required auto dependencies always run;
- optional auto dependencies run only when selected;
- required manual dependencies retain the existing browser-and-pause flow;
- unselected optional dependencies are skipped;
- selected optional failures are reported and skipped without creating an acquisition receipt;
- required failures roll back acquisitions and abort.

## FFVII Runtime Detection

The catalog will use the same concrete edition markers already validated by Blind Soldier's installer module.

FFVII 2013 requires:

- `ff7_en.exe` as the primary executable;
- `FF7_Launcher.exe`; and
- the `data` directory.

FFVII 2026 requires:

- `FFVII.exe` as the primary executable;
- `FFVII_LAUNCHER.exe`;
- `steam_api64.dll`;
- `ff7/resources/ff7_1.02/ff7_en`; and
- `ff7/workingdir/data`.

These rules intentionally reject a stale 2013 override that points to the 2026 root, and reject incomplete converted folders that do not contain a runnable 2013 installation.

The separate `ffviioldsteam2026` entry uses the 2026 root probes, but installs the x86 package into the embedded 2013 runtime. Keeping a distinct game ID prevents the native x64 package and compatibility x86 package from replacing one another in manager state.

## 7th Heaven and FFNx Components

The real 2013 entry declares both `seventh-heaven` and `ffnx-game-driver` as optional. The compatibility-runtime entry declares both as required because that entry exists specifically to make 7th Heaven work with the embedded 2013 runtime. The native 2026 entry declares neither dependency.

The official `seventh-heaven` dependency uses:

- Minimum version: `4.5.2.0`
- Detection: per-user uninstall registry entry for 7th Heaven, reading `DisplayVersion`
- Installer: official 7th Heaven 4.5.2 release executable
- SHA-256: `1a6cb7b3da0788e5fdc4174fd75367cb81a0825fec92e2817a8e95ef8f455c55`
- Elevation: not requested by the manager; the signed/packaged installer may handle its own requirements

The pinned `ffnx-game-driver` dependency uses FFNx Steam 1.24.3 with SHA-256 `2be45f486974f0979b849d0525eb66427df62483ec99e9339e9773e9e52afc0d`. It checks and extracts at the game root for a real 2013 install, but checks and extracts under `ff7/workingdir` for the Steam 2026 compatibility runtime.

## Compatibility

Older Accessibility Mod Manager builds ignore optional dependencies during resolution, so the native Blind Soldier packages remain installable after the catalog correction. Updated manager builds add the optional selection experience for real 2013 installs. The compatibility entry uses required dependencies and therefore works through the existing dependency flow as well.

## Verification

Manager tests will cover:

- optional auto dependencies being offered but unchecked;
- unselected optional dependencies not downloading;
- selected optional dependencies installing;
- selected optional failures not blocking the mod;
- required dependencies remaining unavoidable and fatal on failure;
- manifest order and accessible selection state.

Catalog verification will parse the published JSON and exercise the runtime probes against controlled 2013, 2026, cross-edition, and incomplete fixtures. It will verify the three-entry dependency split, the official 7th Heaven installer, the pinned FFNx archive and target folders, and the absence of both components from native 2026.
