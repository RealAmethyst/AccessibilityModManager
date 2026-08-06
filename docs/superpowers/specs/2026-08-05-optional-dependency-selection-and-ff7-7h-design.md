# Optional Dependency Selection and Final Fantasy VII 7th Heaven Design

## Summary

Accessibility Mod Manager currently prompts only for missing required dependencies. The live Blind Soldier catalog therefore marked a package named `seventhheaven` as required even though the package was actually FFNx. That combination causes three user-visible failures: FFVII 2013 and FFVII 2026 can be mistaken for one another, FFNx is installed into the wrong edition layout, and a user who does not want 7th Heaven cannot decline it while still installing Blind Soldier.

The fix has two coordinated parts:

1. Accessibility Mod Manager will offer missing auto-installable optional dependencies in its existing dependency consent dialog.
2. The Blind Soldier catalog will identify each FFVII edition using edition-specific files and will offer the official 7th Heaven installer as an optional, unchecked component for both editions.

FFNx will not be installed or configured directly by Blind Soldier. If the user chooses 7th Heaven, the official 7th Heaven installer remains responsible for its own FFNx integration.

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

## FFVII Edition Detection

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

## Official 7th Heaven Component

Both game entries will declare the same optional `seventh-heaven` framework dependency:

- Required: false
- Minimum version: `4.5.2.0`
- Detection: per-user uninstall registry entry for 7th Heaven, reading `DisplayVersion`
- Installer: official 7th Heaven 4.5.2 release executable
- SHA-256: `1a6cb7b3da0788e5fdc4174fd75367cb81a0825fec92e2817a8e95ef8f455c55`
- Elevation: not requested by the manager; the signed/packaged installer may handle its own requirements

The catalog will no longer label an FFNx archive as 7th Heaven, require `FFNx.toml`, or extract FFNx directly into the detected game directory.

## Compatibility

Older Accessibility Mod Manager builds ignore optional dependencies during resolution, so Blind Soldier remains installable after the catalog correction. Updated manager builds add the optional selection experience. This lets the catalog safety fix ship immediately without making the mod dependent on the manager PR being merged first.

## Verification

Manager tests will cover:

- optional auto dependencies being offered but unchecked;
- unselected optional dependencies not downloading;
- selected optional dependencies installing;
- selected optional failures not blocking the mod;
- required dependencies remaining unavoidable and fatal on failure;
- manifest order and accessible selection state.

Catalog verification will parse the published JSON and exercise the edition probes against controlled 2013, 2026, cross-edition, and incomplete fixtures. It will also verify that both editions reference the official optional 7th Heaven installer and that no dependency named for 7th Heaven points to FFNx.

