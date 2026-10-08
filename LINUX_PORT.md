# Linux port

## Current implementation

The Linux manager and AuthorTool use Avalonia 12.1.3 and share the portable .NET 10 core, installer, catalog and authoring services with Windows. The manager has Mods, Authors and Settings tabs, mod details, release channels and versions, user-added sources, Patreon sign-in, entitlement-gated releases, native Linux packages, and Steam Proton setup. The AuthorTool opens local projects or GitHub repositories, edits games and releases, builds Windows/Proton/Linux packages, and uses the same signing and publishing services as Windows. The Linux UI names its AT-SPI application and controls. The selected mod row is focused after it loads; returning from details focuses that row again.

Avalonia 12.1.3's AT-SPI bridge drops a focus event if the newly focused peer is deeper than the root's immediate children and no accessibility client has traversed to it yet. The AT-SPI tree then shows the correct focused state without an event for Orca to announce. The pinned source patch at `installer/linux/patches/avalonia-atspi-focus.patch` attaches the peer's ancestor path before emitting that event. `build-avalonia-atspi.sh` builds the patched bridge from the exact Avalonia 12.1.3 Git commit and D-Bus submodule used by the package, then `build-linux.sh` installs it into both Linux applications with the Avalonia license and notice. The AT-SPI event monitor observed a focused event for the initially selected mod, Back on a newly opened details page, and the selected mod on return. Amethyst confirmed that the installed manager's focus and category tabs work with Orca.

The 2.0 bridge patch also exposes a closed combo box's unrealized selection peer as
an AT-SPI child, and invalidates that child list when selection or expansion changes.
Previously `NSelectedChildren` was one while `GetSelectedChild` returned a null reference.
Orca's combo-box value reader requires a child before it asks for the selection, so
fixing only the accessible name does not repair the selection contract. The bridge
build runs the Linux UI suite with `AtSpiBridgePath` set to the patched DLL, including
regression checks for closed selection, empty selection, popup transitions and ordinary lists.

Linux native dependency installation now uses the same `DependencyUpdates.UpdatePortableAsync`
path as Windows. It installs directly into the selected folder, unwraps the archive's
optional outer directory, verifies the executable, backs up replaced files and records
only package-owned files. Existing game-ID subfolders are left in place; their recorded
paths remain valid.

Steam launch-option editing still requires Steam to close. Valve's open
[programmatic launch-options request](https://github.com/ValveSoftware/steam-for-linux/issues/6443)
records that the running client does not reread `localconfig.vdf`.
The [documented Steamworks apps API](https://partner.steamgames.com/doc/api/ISteamApps)
provides launch-option readers, but no supported setter for another game's saved options.
No supported live-edit mechanism was found during the 2.0 investigation; the manager
keeps its existing guarded, atomic edit and restore flow.

Patreon OAuth uses a local loopback callback. Amethyst confirmed that sign-in now completes in the manager. `secret-tool` operations have a ten-second limit; if the login collection does not respond, the manager saves to the Secret Service session collection and says that the account lasts only until Linux logout. A nonsecret sign-out marker prevents an inaccessible older login item from restoring a signed-out account. The browser page says authorization was received and asks the user to return to the app to finish sign-in; it does not claim the token was saved yet.

Proton is the default path for Windows game builds. A hash-verified Windows file-copy package is converted automatically when it uses a recognized BepInEx `winhttp.dll`, MelonLoader `version.dll`, or DSCSModLoader `freetype.dll` proxy, bundled, already present beside the game, or supplied with a matching loader DLL by a pinned ZIP dependency. Supported Prism 0.18.3 x64 builds gain their matching Wine bridges and placeholder DLLs automatically. The exact published Cyber Sleuth plugin uses Prism 0.17.3, whose Windows DLL omits Orca; its audited adapter preserves the older configuration ABI while loading pinned Prism 0.18.3 and its matching bridge. Other Prism 0.17.3 consumers fail closed. For the exact legacy Tolk x64 DLL verified in the published Next Order and Master Duel packages, the manager replaces that DLL in the temporary Proton package with Prism 0.18.3's official Tolk compatibility shim and adds the same Orca bridge. Other Tolk builds, direct NVDA-only speech, unknown loaders, Windows lifecycle scripts, Windows registry dependencies, and unsupported Prism builds require an explicit Proton package. Reloaded II packages need their loader, launcher and prefix setup declared. Native Linux releases use a declared Linux executable and Linux-targeted dependencies; the manager can detect them through Steam or a manually chosen game folder. Switching an installed mod between native and Proton targets requires uninstall first.

Steam launch options and Reloaded II prefix files are backed up with ownership records. The manager requires Steam to be closed before changing its local configuration and refuses an unrelated existing setup for the same game. It preserves the original game arguments and lets users press Play in Steam after install. A Windows UAC prompt inside Proton never grants Unix administrator rights; the Linux manager does not run Windows installers as host programs or invoke `sudo` for games.

For native Windows files inside a verified Proton package, the manager detects Visual C++ v14 imports and checks the matching x64 or x86 redistributable in that game's prefix. It installs Microsoft's SHA-512-pinned 14.51.36247.0 redistributable quietly when needed, then verifies the prefix version before installing or updating the mod. This works for explicit Proton packages and adapted Windows releases. It does not alter already installed mods until their next update or reinstall. The shared runtime remains in the prefix after uninstall. Imports loaded only dynamically are not detected.

The AuthorTool bundles a pinned GitHub CLI 2.102.0 binary with its license. GitHub publishing still requires the author to sign in using the bundled `tools/gh auth login`; no account was accessed during this port. Native file and confirmation dialogs use `zenity` on GNOME. The installed applications are self-contained and need no system .NET runtime.

## Shared packages and native game dependencies

Shared mod packages declare `targetPlatform: "windows-linux"`. The archive and
signed release are shared; each dependency has its own `targetPlatforms` checkboxes.
The installer resolves the package to Windows or native Linux and records that
runtime in its receipt. Proton remains separate. Both platforms' dependencies are
included in the manifest, but only matching entries are installed. Platform-specific
lifecycle scripts require a separate package. Older managers skip this unfamiliar
target, so distribute the updated manager before publishing shared releases.

For a native game/emulator dependency, declare its Linux executable and a
Linux-targeted `extractApp` ZIP or tar.gz with an HTTPS URL and SHA-256. The manager offers
“Install game or emulator” when the game is missing. Installation stages and checks
the archive, accepts a single wrapping directory, retains owner executable bits,
and creates a new game-ID folder under the chosen parent without replacing existing
files. It records the path for detection and reuse. No game is launched. Windows
installers are not run on Linux. XIVLauncher's provider and Windows-specific mod
scripts need separate Linux integration; TCG Live's special setup remains deferred.

Manager 1.19.1 accepts gzip-compressed tar portable dependencies as well as ZIPs,
identified from the verified download bytes. It rejects traversal, duplicate paths,
links, special files and excessive archive sizes, and retains owner execute bits.
The published BizHawk v1.2 Linux tar.gz passed a disposable installation and
GameVerifier detection check with `linuxExeName: "EmuHawkMono.sh"`; all 490 files
matched the archive afterward. No emulator or game was launched. Its SHA-256 is
`45a211845351de4c1ad0311402951631c568708c24e0e374859a3a66e7d7e503`.
Mono and native runtime libraries are system prerequisites, not installed by this
portable-app extraction flow. Actual Pokémon play remains a manual check.

The section tabs now use one selected header in the Tab order; arrows still change
sections. Filter headers support Tab, Space/Enter, and Left/Right and expose their
expand/collapse state. Headless UI tests exercise focus transitions and automation
peers. Actual Orca speech remains a manual check.

## Manager updates and release packaging

The manager checks the latest stable GitHub release at startup and from Settings.
Each platform's project version identifies its packaged build. Each platform
requires its exact versioned manager asset and matching SHA-256 file; no fallback
to another platform or to the AuthorTool is allowed. The existing Windows updater's
Setup.exe suffix already excluded Linux archives; the new selection also prevents
mixing unrelated installers and checksum files from one release.

After confirmation, Linux downloads and verifies the archive, rejects unsafe tar
paths and links, and starts its installer from the extracted package. The helper
waits for the old manager to exit, backs up and replaces the application, updates
the menu entry, then restarts it without a success popup. Failed replacements roll back
and report an error. Settings, sign-ins,
mod receipts and game folders remain outside the replaced directory. Portable
copies need one install.sh installation before in-app updates can be applied.
The AuthorTool has a separate manual installation archive.

Offline checks cover mixed-platform releases, wrong or missing hashes, duplicate
assets, download corruption, fresh installation, preserved data, backup and rollback,
archive traversal and links, and the wait/install/relaunch handoff. Both actual
release archives passed first-install and upgrade checks in isolated user directories;
their desktop entries pass desktop-file-validate. Actual Orca announcements and the
Windows installer/UI still need manual checks. Amethyst confirmed the Linux auto-update
to 2.0.2 on 2026-10-08; the popup removal in 2.0.3 still needs her UI check.
See [release packaging](installer/README.md).

## Build and install on CachyOS GNOME

A temporary SDK at `/tmp/amm-dotnet-sdk/dotnet` was used for this work. The local package database currently offers `extra/dotnet-sdk` 10.0.12, but the system SDK is not installed. For a durable development machine, run `sudo pacman -Syu dotnet-sdk gcc zenity curl tar git` in a terminal. This does the required full upgrade and installs the SDK and build tools; gcc, zenity, curl, tar and git are already present here. Steam and Proton are installed. The Linux build script downloads a pinned GitHub CLI archive and checks its SHA-256 before bundling it. After installing the SDK, omit `DOTNET_BIN=...` from the build command below.

```bash
DOTNET_BIN=/tmp/amm-dotnet-sdk/dotnet installer/linux/build-linux.sh
installer/linux/install-user.sh
```

The first command produces separately installable manager and AuthorTool archives with SHA-256 files directly in `dist/`. The intermediate `manager` and `author` build trees are in `publish/linux/AccessibilityModManager-linux-x64`. Each archive includes `install.sh` for installation without the source checkout. The second installs them under the current user's local data directory, backs up any installed build and shortcuts, and creates **Accessibility Mod Manager** and **Plugin Index Author** GNOME launchers. The previously installed Time Stranger Steam wrapper remains in its old directory because the existing Steam launch option points there.

Installed on this machine at `/home/amethyst/.local/share/AccessibilityModManager/linux-x64` and `/home/amethyst/.local/share/AccessibilityModManager-Author/linux-x64`. Each installed directory contains `release.json` with its product, version and architecture; previous builds are retained beside it with a timestamped backup name. The working Time Stranger wrapper hash remains `73753f60160aa48ff5222899e0de6d07fc074a8b5cf4e55f4e226aedd2437151`.

## Orca application recognition fix in 2.0.1

The installed Orca rejected focus events from the manager because Avalonia's
`ApplicationAccessibleHandler.Parent` always returned the null reference. Its
`AtSpiServer.EmbedApplicationAsync` discarded the desktop reference returned by
AT-SPI's `Socket.Embed`. Orca's `AXUtilitiesApplication.is_application_in_desktop`
checks that parent against the accessibility desktop before accepting focus events.
The old and new combo-box bridges both failed this check; no update dialog was open.

The pinned bridge patch now saves the registry's actual Embed reply, exposes it as
the application's parent and emits an `accessible-parent` property change so a
client that queried during registration can refresh its cache. The live AT-SPI
parent changed from null to the desktop, and the installed Orca's validation
changed from false to true. Amethyst confirmed navigation speech was restored.
Orca was not restarted. The stable/beta selection patch remains included.

All 7 Linux UI tests passed against the packaged bridge, including the desktop-parent
regression and a modal update offer that focuses its notes and restores the previous
control after declining. Both 2.0.1 manager packages and checksums were built.
The Linux archive is `AccessibilityModManager-2.0.1-linux-x64.tar.gz`, SHA-256
`04b7889e52d026b7caa27235e33697f4920e9866b771b2dd785573f680ead220`.
The installed 2.0.1 files were verified against the package tree and the running
installed application passed Orca's desktop-parent validation. The prior installation
is backed up at `~/.local/share/AccessibilityModManager/linux-x64.backup-20261008T145238Z-26242e13`.
The Windows installer compiled successfully under Wine; Windows UI execution was not tested.

The public 2.0.0 packages were released under `v2.0`. Existing updater clients
require a three-component tag and reject that tag before opening an update dialog.
Publish the corrected packages as a new release tagged `v2.0.1`; do not replace
already published 2.0.0 downloads with different contents.

## Verification and limits

The 2.0 dependency-path changes passed all 130 portable tests. The final 2.0.1
bridge passed all 7 Linux UI tests and live Orca desktop-parent validation;
Amethyst confirmed audible navigation. The selection regressions failed against
the previous bridge and passed against the patched bridge. Linux and Windows
manager builds completed without warnings. Windows UI execution remains untested.

- The Linux applications build with zero warnings; 130 portable tests and 7 headless Linux UI tests pass. Offline tests cover native package install/uninstall, Windows package adaptation with bundled, already-installed and pinned dependency loaders, same-author Proton identity import, nested executable detection and DSCSModLoader's subfolder proxy, automatic matching Prism and Tolk shim bridge packaging and removal, Steam launch-option ownership and restoration, and Proton prefix setup. BepInEx's `winhttp.dll=n,b` rule and MelonLoader's `version=n,b` rule now pass complete disposable install and uninstall checks when the loader comes from either the package or a pinned catalog dependency; an already-installed BepInEx loader remains outside mod ownership. The exact Cyber Sleuth 1.0-beta24 ZIP was adapted and installed/uninstalled in a disposable game folder; the original `freetype.dll` and Steam launch option were restored. That ZIP is detected as needing x64 Visual C++ v14. Both pinned redistributables installed and passed version checks in a disposable Proton prefix. The updated adapter packages the verified Prism configuration compatibility DLL and Prism 0.18.3 core for that exact signed release. A separate fake-keyring check covered Patreon save timeout, session fallback, reload and sign-out. The wrapper binary is unchanged from the build Amethyst confirmed in Time Stranger.
- AT-SPI in the GNOME session exposes the manager as Accessibility Mod Manager instead of the generic Avalonia Application, and the selected release name is accessible. The startup focus transition is tied to window activation and catalog readiness. Amethyst confirmed Orca focus and category navigation in the installed manager. The AuthorTool's project editor, release dialog, build dialog and validation dialog were opened against a disposable project in an earlier check.
- Time Stranger has been confirmed by Amethyst to work as on Windows, including input, custom audio, speech and navigation. Cyber Sleuth's published 1.0-beta24 release has also been confirmed with speech, audio, navigation and controller input under Proton. No game was launched by Codex. Next Order, Survive, native Linux game packages and XIVLauncher still need Amethyst's in-game checks. XIVLauncher uses its own Wine route and needs an explicit release/provider; it is not an ordinary Steam Proton game.
- This port cannot infer arbitrary Windows installer behavior or an unknown loader from a Windows release. The manager gives a specific refusal instead of claiming the mod loaded. Publishing a real release still needs author interaction.

## Verified mod families and Linux implications

- **Digimon Story Time Stranger (Reloaded II):** `DSTS.Installer/installer.cpp` requests Windows elevation because it writes the IFEO `Debugger` value under HKLM for the game executable. `DSTS.Launcher/launcher.cpp` then creates the game suspended with `DEBUG_ONLY_THIS_PROCESS`, injects the Reloaded II bootstrapper, and resumes it. Its config pointer swap is also Windows AppData specific. In Wine, Windows token elevation does not elevate the Unix user; a Proton prefix has its own Windows registry. Do not run the Windows installer or Steam with `sudo`. A disposable Proton prefix test showed that a Windows IFEO `Debugger` value did not redirect `cmd.exe` under `runinprefix`, so the existing installer cannot be relied on for Linux. The old Windows `package.cmd` copied dependencies from a developer installation; the Proton `package-linux.sh` now stages pinned inputs and builds the tested Linux package.
- **Digimon World: Next Order (MelonLoader):** the public 1.2.0 Windows ZIP declares a SHA-256-pinned MelonLoader 0.7.2 x64 ZIP with `version.dll` and `MelonLoader/net6/MelonLoader.dll`. Its Tolk DLL has SHA-256 `c4fb11d3ed236f27532c7ab8370ebde75133f322a069450f17e38d7548197225` and the same 13 exports as Prism's Tolk shim. A disposable offline run adapted this exact verified ZIP, installed its MelonLoader dependency, replaced Tolk in the adapted package with the official shim, installed the Orca bridge, set the DLL override, then uninstalled and restored Steam's previous option. A separate executable probe under installed CachyOS Proton loaded the shim and detected Orca with speech support. The actual game, its .NET runtime and audible mod speech still need Amethyst's in-game check.
- **Yu-Gi-Oh! Master Duel:** the public 1.8 Windows ZIP was verified against its catalog SHA-256 `8a77f0cfa70b4ad479ef9a1f2cefeeb9bdd5db4565ec7dfe7076996e7ebf12b1`. It copies a `mods` folder plus the same Tolk DLL, NVDA and SDL3 DLLs. Its package manifest omits the MelonLoader dependency declared by the author index; the adapter now reads the pinned catalog dependency that the installer actually uses. A disposable offline run of this exact ZIP installed MelonLoader 0.7.3, swapped in the Tolk shim, installed the Orca bridge, set Steam's DLL override, and restored everything on uninstall. The real game still needs a Steam and Orca check.
- **Digimon Story Cyber Sleuth:** the downloaded Patreon ZIP `dscs-v1.0-beta24-amm.zip` matches the signed catalog hash `fc96dce6eaaf01c63dce6d7b41d8e4d174d471bd404e3747f7a83430f9cf92e6`. Its game EXE is x64 and imports `freetype.dll`; the pinned DSCSModLoader dependency supplies an x64 proxy and loader. The package bundles SDL3, navigation and audio assets, the mod DLL, and Prism 0.17.3. Its Proton prefix originally had Visual C++ runtime 14.10.25008.0, older than the plugin's Visual Studio 2026 build toolset. After a prefix backup and upgrade to Microsoft's 14.51.36247.0 runtime, Amethyst confirmed the game starts with mod audio, navigation and controller input. The fresh plugin log found no Orca or Speech Dispatcher backend in the published Prism 0.17.3 Windows DLL, despite bridge files being present. The manager replaces that DLL only in a temporary Proton package for the exact verified plugin with a small Prism 0.17.3 configuration ABI adapter. It adds pinned Prism 0.18.3 as `prism-core.dll` and its matching bridge. An offline Proton probe using the old header selected Orca through the adapter; a conversion test checked the exact signed ZIP's resulting DLL hashes. After reinstalling the same release, Amethyst confirmed audible speech, audio, navigation and controller input in game. The published ZIP and mod DLL are unchanged.
- **Digimon Survive (BepInEx):** the manager verified the signed Windows 1.0-beta03 release hash `0dbf5093af81af0e6f7a470b6b76ada866885e1e1888ce8b287e57065637f9fa`, installed its pinned BepInEx 5.4.23.5 dependency and converted its Prism bridge, but the old final proxy check compared `winhttp.dll` with `winhttp.dll.dll`. The failed install rolled back before Steam setup. Wine override names are now normalized once across package conversion, validation, authoring and installation. Disposable tests cover BepInEx from a package, from a pinned dependency and already present in the game, with Steam restoration and ownership checks. The Survive project uses the older Prism version-one config; a headless Wine probe with the staged Prism 0.18.3 DLL and bridges accepted that 16-byte config, created Orca and initialized it with result zero. The probe did not emit speech or launch the game. Amethyst retried installation and reported no issues; in-game speech, controls, audio and uninstall remain to be checked.
- **Final Fantasy XIV (Dalamud):** XIVLauncher has a native Linux launcher, but it runs the Windows game with a tuned Wine environment. The existing XivAccess installer and uninstall hooks target Windows `%AppData%` and Windows XIVLauncher registration. A Linux package needs to locate XIVLauncher's own user data and register the plugin there, without pretending FFXIV is a native Linux game. This path is separate from Steam's ordinary Proton prefix.

For Time Stranger, the two pinned loader DLLs in `libs/reloaded-ii/Loader/X64` match the publisher's `Reloaded-II` 1.30.1 `Release.zip` byte for byte (bootstrapper SHA-256 `7a10d484ff9bcd579d676be8733f4e8692f7e9da0e589b07aee07b1157f11cc2`; loader SHA-256 `2bf119a29b62ffb638687d1196578bfc0edf22726fc3ee459f1f3ebb5cf52dfb`). The official 1.30.1 archive, SHA-256 `1680086693dd4bee0037c18f14fcfd7b19fcb4f388c0838399926a5bf286c548`, contains `ASILoader64.dll`. Reloaded II's generic `AsiLoaderDeployer` would prefer `winmm.dll` for this game's imports, and its bootstrapper reads `ReloadedII.json` from the prefix's Roaming AppData. But the Time Stranger project's own `CLAUDE.md` records a prior Ultimate ASI Loader loader-lock deadlock and a later proxy that crashed after missing the DX11 hook timing. Therefore **do not deploy ASI for this game based on generic Reloaded guidance**. The per-game prefix can isolate the loader pointer, but only after a launch route and the correct mod package are prepared.

`installer/linux/steam-proton-launch.c` keeps Steam's `%command%` and locates the exact game executable argument by file identity. It now supports direct game launch with declared Wine DLL overrides, executable substitution for a bundled Windows launcher, and optional Prism `WINEDLLPATH`. The existing Time Stranger command retains its legacy substitution behavior. It refuses a missing or ambiguous game argument. Offline wrapper checks exercise both modes, the legacy command, environment preservation and conflicting override refusal. Amethyst confirmed the replacement wrapper still launches Time Stranger through normal Steam Play with the mod working as on Windows.

The release and manifest models now have an optional `targetPlatform` value (`windows`, `proton`, or `linux`). Existing releases and packages default to Windows; unknown values fail. The installer refuses a Windows package on Linux before installing dependencies, and checks that the package manifest target matches the selected release. Proton packages can declare their Steam game, launcher, bridge directory, Windows runtime, and Reloaded II configuration. The Linux manager reads the signed registry, verified author indexes, and user-added sources. It lists Windows, Proton, and native Linux releases, subject to Patreon entitlement. A compatible Windows release can be adapted for Proton after hash and loader checks.

The current DSTS and Next Order `prism.dll` files have the same SHA-256, `3b02d1d9b2d44066604be1a6acf751c8c3c7f109c19035646aab21542083b02f`; DSTS's pinned documentation identifies this generation as Prism 0.7.1, and its managed interop expects config version 1. FFXIV's DLL is different, `287cd79e5d6cac5204605ba23e73bd043f48192ade54ddadf50ac4fcd9b94aae`, and its source identifies Prism 0.17.3/config version 3. The inspected current Prism source is 0.18.3/config version 4 and documents Orca and Speech Dispatcher Wine bridges. Its config ABI must not be assumed compatible with either older mod: DSTS calls `prism_config_init` by value, whereas FFXIV already avoids that return-buffer hazard. These mods need individually verified Prism upgrades and matching bridge builds before Linux release approval.

A shared manager cache can store bridge builds by Prism release and architecture, but cannot install one arbitrary bridge across every mod. `PrismBridgeProvisioner` now stages the official 0.18.3 x64 Windows DLL, all three Linux Wine bridge pairs, and notices from publisher archives pinned to their published SHA-256 digests. It succeeded offline against the real archives. Its Orca and Speech Dispatcher host modules resolve their Linux library dependencies here. A native Linux Prism 0.18.3 probe found both backends available and initialized both successfully against this logged-in session. A separate Windows API probe, compiled with `winegcc` and run with the staged Windows Prism DLL, placeholders, and `WINEDLLPATH`, found Orca and Speech Dispatcher runtime-available and initialized both with result 0 inside a disposable Wine prefix. Neither probe emitted speech. Each game still needs the matching Windows Prism DLL and bridge placeholder DLLs in its Windows search path, plus the matching host `.dll.so` directory in `WINEDLLPATH`. Prism's guide explicitly says to preserve an existing `WINEDLLPATH` and to test whether Orca or Speech Dispatcher is reachable from inside the runtime. The DSTS binding and package have been updated to this version; the other mods have not.

The Wine probe initially timed out while native Wayland Wine initialized its disposable prefix. With `DISPLAY=:0` and `WAYLAND_DISPLAY=` to use this session's XWayland server, Wine initialized and the bridge probe succeeded. This is a diagnostic finding, not a reason to force the same environment onto Steam or the game before testing it. That probe did not touch a Steam game prefix.

The same Windows Prism probe also ran under the installed CachyOS Proton 11.0 `runinprefix` command in a separate disposable compatibility directory. With the staged Windows DLL and placeholders beside the probe and the staged host modules in `WINEDLLPATH`, Orca and Speech Dispatcher each reported runtime availability and initialization result 0. This checks the bridge inside this Proton build, but not the Steam game container or audible speech. A separate IFEO experiment registered `cmd.exe`'s `Debugger` value to `whoami.exe` only in that disposable Proton prefix; `proton runinprefix cmd /c echo ...` still ran `cmd.exe` and printed the marker. Thus the Windows IFEO registration was not a working launcher substitution in this tested Proton path. Do not reuse Time Stranger's IFEO installer for the Linux package.

The verified 0.18.3 x64 bundle is installed in `/home/amethyst/.local/share/AccessibilityModManager/prism/prism-0.18.3-x64`. This shared cache does not modify any game or Steam launch option and is not itself a working mod installation.

The Time Stranger managed Prism binding no longer marshals `PrismConfig` by value. Both its old 0.7.1 DLL and the new 0.18.3 DLL accepted `prism_init(NULL)` under this installed Proton build. A native Windows x64 probe compiled against Prism 0.18.3's exact published header confirmed `PrismConfig` is 56 bytes, version 4, and `prism_registry_create_best` selected Orca inside Proton. The mod's current source builds with this binding; its installed Windows build was not replaced. The current source launcher also cross-compiled to a real x64 Windows GUI PE using a verified portable LLVM MinGW toolchain. This avoids using the stale April launcher binary.

The DSTS project has `package-linux.sh`, which builds a Proton test archive from current source, local user-supplied sounds, and pinned upstream archives: Reloaded II 1.30.1, shared hooks 1.16.3, SDL3 3.4.0, Steam Audio 4.8.1, and Prism 0.18.3 with matching Wine bridges. The first pilot omitted 11 WAVs; the script now requires all 14 in the expected 44.1 kHz PCM format. Amethyst has since confirmed controller input fully works. An earlier live log showed WAV loading while field audio and R3 navigation remained unavailable; the later fix resolved both. The installed package is now `dist/linux/DSTS.Accessibility-0.1.0-proton-20261005T205943Z-397489-amm.zip`, SHA-256 `3494087483279ed6a886d6f6f3e5e3eabc184c5e5f4312571642d118be368555`, with 99 payload files. Amethyst subsequently confirmed that the installed build plays and works exactly as it does on Windows, including custom audio and navigation.

The field failure was traced to five duplicated native-memory protection checks in the DSTS mod. The live log rejected the field scene and current-map globals before building audio sources or navigation entries. The game executable MD5 `c7193e6d3b9a256855a09e7dfe795e1c` matches the current Windows RE baseline, and both globals sit in its zero-filled `.data` section. An offline Wine 11.19 probe loaded that executable as an image without starting it: `VirtualQuery` returned committed `PAGE_WRITECOPY` (`0x08`) for the singleton slot at RVA `0x1b9a5d0` and field global at RVA `0x1ede9c0`. Microsoft documents `PAGE_WRITECOPY` and `PAGE_EXECUTE_WRITECOPY` as readable. Four guards accepted only `0x02`, `0x04`, `0x20` and `0x40`; the ladder reader also accepted `0x80`. All five would reject a page with that `0x08` protection. The guards now share one readable-protection predicate that accepts both copy-on-write values and still rejects guard, no-access and execute-only pages. The offline native fixture passed with zero failures. This explains the logged rejection; Amethyst confirmed the corrected build works in the live game.

The pinned Reloaded II loader declares both `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` 9.0.8 or later in its x64 runtime config. Microsoft's official 9.0.20 Windows Desktop Runtime installer has SHA-512 `5c7caacef8bd65a7631ff0be6cd1059b523eb9ab4e1021078921926da0bdc05b468bcbdf1f95fc12cb984bc4e97df9fbc40f942a618c716902c7c99150c4de7e`. Running it silently through the installed CachyOS Proton in a disposable prefix installed both 9.0.20 frameworks, confirmed by that prefix's Windows `dotnet.exe --list-runtimes`. The manager installed it as the ordinary Unix user in the actual Time Stranger prefix during the pilot. Runtime setup now accepts any `major.minor.patch` Windows Desktop Runtime with an official installer SHA-512 in the package metadata, while retaining the old 9.0.20 pin for the deployed package. The selected prefix's two frameworks are verified before mod files are installed.

`ProtonSteamInstallCoordinator` joins the manager's file receipt with a separate owner record for Steam launch options and Reloaded II config. It requires Steam to be closed, verifies the package hash and manifest before runtime setup, installs the exact package through `InstallerEngine`, saves the owner record before changing Steam, and can finish an interrupted setup or restore the prior launch option and config on uninstall. The Reloaded II pointer lives inside the game's own Proton prefix; an existing different loader config is refused. The DSTS launcher uses these managed files when the Steam wrapper marks a Proton launch, while its Windows path keeps the existing transient pointer behavior. The first live Steam launch loaded the mod and spoke through Orca. The manager restored the previous Steam option during the first uninstall/reinstall. The latest package was applied through `InstallerEngine.UpdateAsync` without changing the owned Steam option or Reloaded II pointer. The update reported 99 files and the installed DLL hash matches the new build; settings and WAV hashes stayed unchanged. A new Proton package is needed to check the manager's user-facing Update action.

A disposable Proton `winepath -w` probe converted the installed Time Stranger executable's Unix path to the same `Z:\home\...` form used by the managed Reloaded II config. A separate disposable Proton `cmd.exe` probe received the wrapper's `AMM_RELOADED_CONFIG_READY=1` environment flag. The live game log and Orca speech subsequently confirmed the installed Reloaded II path starts the mod.

## Reusable Steam loader setup

A Proton package declares `direct` or `replaceExecutable` launch, Wine DLL overrides, optional Prism bridge, optional Reloaded II prefix config, and optional pinned Windows Desktop Runtime. The manager validates these assets and saves Steam's prior launch option. Dependencies can target Windows, Proton or Linux. Legacy unmarked dependencies are tried under Proton only when their checks and install actions are supported. The Windows and Linux AuthorTools use the same package builder and release editor model. See [Steam Proton loader backend](PROTON_LOADER_BACKEND.md) for authoring details.

The generic adapter accepts a verified Windows file-copy release with a recognized BepInEx, MelonLoader or DSCSModLoader proxy, including one supplied by a pinned ZIP dependency from the verified author catalog. The dependency uses the normal consent and receipt flow; the manager verifies that the proxy is installed before writing Steam launch options. If the loader is already installed, it checks the actual proxy and marker DLLs against the game architecture and does not claim ownership of them. For pinned Prism 0.18.3 builds, it places bridge placeholders beside the Prism DLL, installs matching Wine host modules and sets `WINEDLLPATH` through the wrapper. The exact Cyber Sleuth 0.17.3 consumer receives its audited configuration ABI adapter and the 0.18.3 core; other 0.17.3 consumers need an explicit Proton package. For the verified legacy Tolk build, the adapter replaces Tolk with Prism's official Tolk compatibility shim. Its receipt removes or restores those files on uninstall. It rejects a package that replaces only half of an existing loader, Windows lifecycle scripts without a verified Proton equivalent, installer dependencies, unknown Tolk or Prism builds, direct NVDA-only speech, and Reloaded II without explicit setup. The earlier local Time Stranger record used a different game ID from its public catalog entry; both records were backed up and imported under `dsts` without changing its working Steam setup. Its public Windows 1.0 and 1.1 packages passed disposable Proton install and uninstall checks; their live game behavior still needs Amethyst's test. Cyber Sleuth speech and the other mod features are confirmed working with the published release under Proton.

## Work still open

The local `todo.md` is the current screen-reader-friendly checklist. It is ignored by this repository, so these are the main handoff items preserved in Git:

- Test the published Time Stranger 1.1 release in game; its DLL differs from the working pilot. Check the published Next Order and Master Duel releases in their real games, and check Survive's installed release in game and on uninstall.
- Check Cyber Sleuth's original `freetype.dll`, Steam option and unmodded launch after a later live removal. The disposable uninstall passed, and the installed published mod is confirmed working.
- Test a real native Linux package and XIVLauncher's separate Wine route, and support a reversible Proton equivalent of Final Fantasy X's Windows title chooser and Large Address Aware installer.
- With Orca, check first-focus speech for the mod detail controls and closed version selector, the Authors page and a user-added source, and the Linux AuthorTool's editing and package-building controls. Test another entitled release. With NVDA, check the changed Windows UIs before a public cross-platform manager release.
- When ready to publish a real release, exercise Linux GitHub sign-in, AuthorTool upload, index publication and post-publish download checks. Evaluate the download server's signed-catalog refresh delay without weakening signature verification.
- For each new Windows loader, lifecycle script or Prism DLL, verify its actual behavior and add a targeted Proton adapter or explicit package. Unknown setup behavior must continue to fail closed. Dynamically loaded Visual C++ dependencies need an explicit prerequisite because PE import inspection cannot find them.

## Sources checked

- [Prism Wine bridge guide](https://github.com/ethindp/prism/blob/master/doc/src/wine-bridges.md), source revision `0aca33c26a17219c5a03bea4c41d43d0dcd07743`: matching bridge halves, `WINEDLLPATH`, and live host service requirements.
- [Prism 0.7.1 C header](https://github.com/ethindp/prism/blob/v0.7.1/include/prism.h) and [Prism 0.18.3 C header](https://github.com/ethindp/prism/blob/v0.18.3/include/prism.h): version-one and version-four config layouts and the registry, backend and speech function signatures used by Survive. The installed 0.18.3 Windows DLL accepted Survive's version-one config in a headless Wine probe; source declarations alone were not treated as proof of runtime compatibility.
- [Valve Proton README](https://github.com/ValveSoftware/Proton): per-game Steam launch options and `%command%`.
- [Microsoft memory protection constants](https://learn.microsoft.com/en-us/windows/win32/memory/memory-protection-constants): copy-on-write pages permit reads.
- [Avalonia accessibility guide](https://docs.avaloniaui.net/docs/app-development/accessibility): standard Avalonia AT-SPI support; [XPF Linux guide](https://docs.avaloniaui.net/xpf/platforms/linux): XPF limitation.
- [Orca](https://orca.gnome.org/): uses AT-SPI to read Linux desktop applications; [GTK accessibility](https://gnome.pages.gitlab.gnome.org/gtk/gtk4/section-accessibility.html): native GNOME dialogs expose standard controls through AT-SPI.
- [GitHub CLI 2.102.0 release](https://github.com/cli/cli/releases/tag/v2.102.0): official Linux amd64 archive with SHA-256 `bb766f710eef8ede859c18578c72c327597cd4c8a85b06001b1f3843c6019386`.
- [Arch .NET 10 SDK package](https://archlinux.org/packages/extra/x86_64/dotnet-sdk-10.0/).
- [Wine FAQ on root and Windows elevation](https://gitlab.winehq.org/wine/wine/-/wikis/FAQ#should-i-run-wine-as-root): Windows administrator checks do not grant Unix root.
- [Reloaded II Linux setup](https://github.com/Reloaded-Project/Reloaded-II/blob/master/docs/LinuxSetupGuide.md): per-application ASI loader and DLL override.
- [Reloaded II 1.30.1 ASI deployer](https://github.com/Reloaded-Project/Reloaded-II/blob/1.30.1/source/Reloaded.Mod.Launcher.Lib/Utility/AsiLoaderDeployer.cs): import selection and bootstrapper placement; [bootstrapper configuration reader](https://github.com/Reloaded-Project/Reloaded-II/blob/1.30.1/source/Reloaded.Mod.Loader.Bootstrapper/LoaderConfig.cpp): prefix AppData pointer.
- [MelonLoader Linux setup](https://github.com/LavaGang/MelonWiki/blob/master/docs/gettingstarted.md): `version.dll` override and IL2CPP runtime.
- [BepInEx Steam and Proton setup](https://docs.bepinex.dev/master/articles/advanced/steam_interop.html?tabs=tabid-1): `winhttp.dll` native override for its documented proxy route.
- [DSCSModLoader source](https://github.com/SydMontague/DSCSModLoader): custom `freetype.dll` loader under `app_digister`; the installed game imports that DLL, and Amethyst confirmed the published mod runs through it under Proton.
- [XIVLauncher Linux](https://goatcorp.github.io/): native launcher using Wine for the game.
- [Microsoft .NET 9 release metadata](https://dotnetcli.blob.core.windows.net/dotnet/release-metadata/9.0/releases.json): official Windows Desktop Runtime 9.0.20 URL and SHA-512.
- [Reloaded shared hooks 1.16.3](https://github.com/Sewer56/Reloaded.SharedLib.Hooks.ReloadedII/releases/tag/1.16.3), [SDL3 3.4.0](https://github.com/libsdl-org/SDL/releases/tag/release-3.4.0), and [Steam Audio 4.8.1](https://github.com/ValveSoftware/steam-audio/releases/tag/v4.8.1): pinned package inputs.

Design answers are in `questions_linux_port.md` and `questions_linux_loader_backend.md`.

## Dependency updates before launching

Play refreshes the selected author's verified catalog with a five-second network deadline,
then compares platform-specific dependency URLs and hashes with local installation records.
It prompts before updating and launches only after a successful update. Cancellation or
an update failure keeps the game closed. If the live catalog is unavailable, Play uses the
existing installation and reports that dependency updates could not be checked.

Portable ZIP and tar.gz updates stage and verify the archive, back up replaced files,
and leave unrelated saves, ROMs and mod files untouched. Obsolete archive files are removed
only if they still match the previously installed copy. Interrupted portable updates restore
the prior files and receipt before launch. Loader updates retain a recovery record and backup;
an interrupted loader transaction blocks launch until recovery. The Windows manager uses the
same updater and comparison logic. Initial installs from older managers lack emulator download
records and therefore need a one-time update to establish them.


## XIVLauncher through native Steam

The `xivlauncher` release target uses XLM rather than the ordinary Proton launch wrapper.
It currently supports native Steam on Linux x64, with the full game (39210) or free trial
(312060). Flatpak Steam is refused explicitly until its sandbox paths and speech access
have been verified. Installing this setup never starts the launcher or game.

The game's optional `linuxSteamAppId` overrides `steamAppId` for Linux detection and
Play. Windows ignores it. Set 39210 for Amethyst's full-game entry, keeping the Windows
XIVLauncher executable and registry detection. Add the **XIVLauncher on Linux (XLM)**
dependency preset. Its executable is pinned to XLM 0.4.0, SHA-256
`b839f633b5ae4cea65346e51a0d89d5c2cf6e93e3fc1f46de75d3e3081628691`.
The manager runs the documented `install-steam-tool` command with captured output,
checks its result, and displays ordinary progress messages and errors. A complete existing
XLM installation is reused. XLM's shared installation remains after removing the mod.

Detection accepts Steam's initial launcher installation: `boot/ffxivboot.exe` and the
`game` directory must exist, but `game/ffxiv_dx11.exe` is downloaded later by XIVLauncher
and is not required to configure the mod. Steam's installed-state check still applies.

While Steam and XIVLauncher are closed, setup records the selected account's prior launch
options and the game's prior `CompatToolMapping` entry, then selects `xlm`. It installs
the plugin under `~/.xlcore/devPlugins/amm/<author>/<game>` and registers its Windows
path in Dalamud's dev-plugin locations, settings, and default profile. Existing unrelated
settings are preserved. A new launcher profile receives the required typed JSON sections;
an existing profile with an unsupported layout is refused. Existing Wine prefixes must
have a verified Z-drive mapping to `/`. Steam launch options supply the package's Wine
bridge directory while preserving any inherited `WINEDLLPATH`.

A checksummed recovery journal is written before installation. Updates retain the same
plugin identity and paths. Uninstall removes only the managed registration, restores the
previous Steam choices, and uses the normal file receipt to remove or restore the payload.
Concurrent settings edits are refused; backup files and interrupted-operation records are
kept for recovery. Another managed owner of the same Steam app is refused.

### Authoring an XIVLauncher package

Use the built **plugin payload folder**, such as the `XivAccess` folder inside its normal
release archive, rather than the folder containing the Windows installer. In the web dashboard
at `https://accessibilitymods.com/owner/`, open the game, set its Linux Steam app ID and add
the XLM dependency using its preset link. Edit the existing release, keep its Windows
package, and enable **Use a separate Linux package for this version**. Choose
**XIVLauncher on Linux (Steam)** as the Linux installation type and upload the plugin
folder (or a ZIP with those files at its root) in the Linux package section. The XivAccess defaults are `XivAccess.dll`, internal name `XivAccess`,
and stable plugin GUID `507b48de-3362-4471-86f4-baa7e56d9387`. The builder excludes the
Windows lifecycle-script defaults for this target. The dashboard builds
and hashes the package during Save draft; review and publish through the normal workflow.
The Windows and Linux packages share one version/channel, release notes and Patreon
audience. Existing Windows files stay unchanged when only the Linux upload is edited.
A prepared ZIP must match the catalog game ID, author ID, and release version.

Prism detection is automatic. A package with the verified original 0.17.3 DLL gets the
existing version-three ABI compatibility DLL plus pinned Prism 0.18.3 and its Wine bridges.
A package already using the verified 0.18.3 DLL gets the matching bridges without that
adapter. The original source folder is unchanged. Other DLL builds fail with an explicit
compatibility error rather than receiving a guessed bridge. Updating the mod itself does
not require a new manager version; moving between these two supported Prism builds is
also automatic. A future Prism ABI still needs compatibility verification.

New manifest metadata is `xivLauncher` with `pluginAssembly`, `internalName`,
`workingPluginId`, and optional `bridgeDirectory`. These paths are relative to the managed
plugin payload, not the game depot. The XLM editor action is saved as optional `fix.xlm` metadata, with a known Linux-only
dependency target. Older Windows readers ignore this metadata and continue installing the
Windows releases. Updated managers apply it only to the `xivlauncher` workflow. An updated
web dashboard build is needed to build and publish the new release target. The actual
pre-change manager assemblies accepted a prospective catalog with this metadata, retained
the Windows releases, and excluded XLM from Windows dependencies.

### Source evidence and verification

- [XLM 0.4.0 installer implementation](https://github.com/Blooym/xlm/blob/v0.4.0/src/commands/install_steam_tool.rs): tool files, `xlm` identity, installer flags and launch environment.
- [XIVLauncher.Core Program](https://github.com/goatcorp/XIVLauncher.Core/blob/43529419497f9ce640669271a8b65b44501d1315/src/XIVLauncher.Core/Program.cs): `xlcore` storage, Steam game IDs, and separate `wineprefix`.
- [UnixDalamudRunner](https://github.com/goatcorp/XIVLauncher.Core/blob/43529419497f9ce640669271a8b65b44501d1315/src/XIVLauncher.Common.Unix/UnixDalamudRunner.cs): converts configuration and plugin paths for the launcher-owned Wine prefix.
- [Dalamud configuration](https://github.com/goatcorp/Dalamud/blob/67e514cac8e0861522acc4853f087024fd6d5d0e/Dalamud/Configuration/Internal/DalamudConfiguration.cs), dev-plugin settings and profile models were checked for the JSON registration fields and type names.
- XivAccess `Speech/ScreenReader.cs` constructs a zeroed 256-byte configuration with version byte 3. `Speech/PrismInterop.cs` imports only entry points exported by the existing compatibility DLL. Its distribution setup defines the stable plugin GUID above.

`XivLauncherTests` covers platform ID selection, Steam detection, typed registration,
settings ownership, package integrity, automatic Prism recognition, install/update/removal,
and interrupted setup recovery. Real game loading, controller behavior, audio, and speech
through Steam remain manual tests; offline success does not establish those behaviors.


The actual web folder-upload service built and validated a package from the existing XivAccess 0.258.0.0 release payload for
catalog identity `amethyst/ffxiv`, with no mod-source or original-payload changes:
`dist/xivlauncher/ffxiv-v0.258.0.0-xivlauncher-amm.zip`, SHA-256
`c974074a2c5dff347bfa2800f09d72b0b862f21511a6fb31b417eb049db892d2`.
A disposable Wine probe loaded that package's actual adapter and bridge using the
XivAccess version-three configuration layout. It selected Orca; initialization returned
`AlreadyInitialized` (15), an accepted success result in XivAccess's `SelectBackend`.
The probe emitted no speech and did not start XIVLauncher or FFXIV. XLM 0.4.0's actual
pinned executable also generated its four expected tool files in a temporary folder
with exit code zero. Steam launch and in-game behavior remain untested.

### Separate platform packages in one release

`ModRelease.linuxPackage` optionally holds a Linux target, HTTPS download URL and SHA-256.
The primary package remains readable by old Windows managers. `ReleasePackages.ForPlatform`
selects the Linux payload before the Linux catalog presents versions; it carries over the
same release identity, notes and access, and converts the Linux URL to a gated server URL
when the parent is Patreon-restricted. It never exposes a public URL for a gated selection.
The signed release claim includes both payloads under one version/channel identity.

The dashboard edits the Linux payload independently, and the publisher/backup service
iterate both physical packages. The download server lists only their exact signed URLs,
inherits the common access, and renders Windows/Linux links under the same release.
Removing the optional Linux package leaves the primary release intact and makes the old
Linux URL unlisted. Existing `windows-linux` packages with identical files retain their
single-package behavior. Adding a distinct Linux package requires the updated Linux manager;
pre-change Windows assemblies verified a disposable signed two-package release and retained
the original Windows URL/hash/version. Browser tests cover keyboard selection, independent
uploads and notes-only saves; publishing tests cover signed identity, backup and shared gates.

### Accessible XIVLauncher frontend

Linux manager 2.0.4 bundles an Avalonia frontend for XIVLauncher.Core 1.4.0,
pinned to `06c32980a75447fe0f15cdac30f8931f70d687e9` and shared-launcher commit
`40ed6e93e7eb73e1c18f4d4871e05f32ab5fd2c6`. The original login, patching, Wine,
Dalamud and game-lifetime controller remains in use. The ImGui window is replaced,
not launched alongside it. Account metadata and automatic-login preferences use
the native account/configuration files; remembered passwords use the native desktop
keyring. There is no manager credential copy or end-of-session settings restoration.
The frontend refuses a concurrent launcher/game session and holds a session lock.

`installer/linux/build-xivlauncher.sh` builds and tests the pinned sources, includes
corresponding source and notices, and reuses the manager's patched AT-SPI bridge.
The bundle includes XLM's pinned aria2c downloader. Installation verifies each file,
preserves the previous `xlcore` directory under XLM's `amm-backups`, and installs an
owned prelaunch hook. The hook disables upstream frontend replacement; frontend
updates arrive with manager builds. Native game, Wine and Dalamud updating remains
enabled. XLM 0.4.0 still requests release metadata even with its update bypass set.

Steam Play opens the accessible form, or skips it for remembered automatic login.
One-time passwords use an accessible dialog. The manager's **XIVLauncher login settings**
button opens settings without authenticating or starting the game. Dalamud readiness
is required before game launch because the accessibility plugin needs it.

The original bundled SDL 3.2.12 failed to create a Wayland Vulkan swapchain on this
machine (`VK_ERROR_SURFACE_LOST_KHR`). An isolated graphics probe reproduced this
with the bundled library and passed with X11. Amethyst confirmed `SDL_VIDEO_DRIVER=x11`
restored the original launcher window. Manager 2.0.5 removes this workaround from
the owned hook because the accessible frontend uses Avalonia rather than SDL.
Offline tests cover credentials, automatic login, labeled controls, OTP, bundle
integrity and rollback. Real Orca interaction, authentication and in-game loading
still require Amethyst's testing.

### XIV Linux speech and password corrections (2.0.5)

Amethyst confirmed native account login and game downloading work. The first game
session loaded XivAccess 0.258.0.0 successfully, but its journal selected UIA rather
than Orca. Dalamud's developer-plugin registration was valid. Wine-XIV 10.8 hides
Wine exports by default (`make-HideWineExports-opt-out.mypatch`), whereas Prism
0.18.3 requires `ntdll!wine_get_version` before enabling Linux speech bridges.

An isolated PE probe using the installed DLLs and the actual managed Wine build
failed to find a speech backend with its defaults. Setting `HideWineExports` to
REG_SZ `N` at `HKCU\Software\Wine` in the disposable prefix made the same probe
select Orca without emitting speech. Wine-staging 10.8 initializes this option
using ntdll's filename, so the per-game AppDefaults key does not solve it. The
2.0.5 frontend backed up that Wine registry key and applied this setting in
XIVLauncher's own prefix. This workaround is superseded by the 2.0.6 correction
below because exposing the version can affect the game's platform detection.

The password textbox was visually masked, but Avalonia's AT-SPI bridge exposed its
unmasked IValueProvider value and reported a normal entry role. The patched bridge
now reports PasswordText and masks both text queries and accessible-value signals.
A regression test covers the role, text, individual characters and changed values.
This bridge is shared by the manager and accessible launcher (frontend revision 2).

Source anchors: Wine-XIV repository commit
`ae094d7ebe4088bcb3bc376504ad085ba0bee61e`,
`wine-tkg-git/wine-tkg-userpatches/make-HideWineExports-opt-out.mypatch`;
Wine-staging tag `v10.8`,
`patches/ntdll-Hide_Wine_Exports/0001-ntdll-Add-support-for-hiding-wine-version-informatio.patch`;
Prism tag `v0.18.3` commit `94329ebeccbaee7f70b3494a290c9dfaac0184ee`,
`source/winelib_bridge.h` and `source/backends/orca.cpp`.

### Platform-license regression investigation after 2.0.5

Amethyst confirmed speech now works, but the game reports error 3109 at data-centre
selection. Mog Station shows Standard (Steam), an active recurring subscription
through October 26, 2026, for FINAL FANTASY XIV 1. Login logs show Steam app 39210,
Steam service account true, fresh authentication (cache false), and playable true.
Do not treat this as an expired subscription or require re-registering Steam keys.

The 2.0.5 speech workaround sets HideWineExports=N. This is a strong regression
candidate: Wine-XIV intentionally defaults to hiding exports, and historical FFXIV
reports connect exposed Wine identity to Mac-platform detection and license errors.
The isolated speech probe verified Orca discovery only; it did not verify game
platform detection. The subsequent 2.0.6 test below confirmed the correction resolved this error.
The correction should retain hidden Wine exports for the game and teach the speech
integration to detect Wine without requiring the hidden version export. Simply
reverting the registry setting would restore the original speech failure.

Primary reports:
https://github.com/ValveSoftware/Proton/issues/580#issuecomment-486224443
https://github.com/ValveSoftware/Proton/issues/580#issuecomment-2007540163
The 2024 official-launcher incident was later reported fixed upstream; these are
historical evidence for the mechanism, not proof of a current upstream outage.
Research compared the unmodified XLM launch environment and the original
XIVLauncher Steam initialization/login flow; no omitted Steam initialization step
was found. No live configuration was changed during this investigation.

### Hidden Wine exports with working speech (2.0.6)

The manager supplies a patched Prism 0.18.3 core that recognizes Wine through
`kernel32!wine_get_unix_file_name` when `ntdll!wine_get_version` is hidden. The
Wine-staging hiding patch filters only wine_get_version, wine_get_build_id and
wine_get_host_version. Orca now uses the same detection as the other bridges.
The pinned source, patch, licenses, build recipe and binary live under
`Infrastructure/Assets/PrismWine`; corresponding source ships in third-party/prism-wine.
The DLL uses statically linked C++ runtime support and only Linux speech backends.
Windows suspend/resume callbacks are disabled in this Wine-only build.

An isolated PE test against the actual Wine-XIV 10.8 and existing 0.17.3 adapter
verified that wine_get_version is unavailable, wine_get_unix_file_name is available,
and the patched core still selects Orca, without starting the game or emitting speech.
Amethyst confirmed the installed 2.0.6 build works in-game without issues: speech
remains functional and error 3109 is resolved. No mod release was republished.

XivLauncherSpeechRepair updates only the known stock 0.18.3 core (standalone or
behind the manager's exact 0.17.3 adapter), requires receipt ownership, rejects
symlinks/unknown builds/running launchers, backs up the previous file, and replaces
it atomically. It runs after mod installation/update and before Play or account
settings. It does not require republishing an existing mod release. Frontend
1.4.0-amm3 backs up Wine settings and sets HideWineExports=Y before game launch.
The login-settings button follows Play immediately in the game-actions panel.

The tested release is `dist/AccessibilityModManager-2.0.6-linux-x64.tar.gz`, SHA-256
`6093597e1f807c6ffef6561d84bb0c31f10bde390963b97f4f552e9ea065dd9e`.
Automated validation passed 166 portable, 8 Linux UI/AT-SPI, and 15 launcher tests.
The installed frontend is 1.4.0-amm3; the previous manager, frontend and Prism core
were retained in local backups.
