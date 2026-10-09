# Command reference

## Help

`amm-author help` lists command groups. `amm-author help game update` and `amm-author game update --help` show the same command-specific help. Running with no arguments also shows help. `amm-author --version` reports the CLI version.

Commands produce plain text without terminal color codes. Lists and show commands include their values. Add `--json` for structured output.

## Shared options

- `--project PATH`: select a project folder containing `index.json`. Without it, resolution uses the working directory or the last-opened project. Use it explicitly for automation.
- `--json`: write a JSON result with `status`, `value`, `messages`, and, on errors, `errorKind`. Publication results can include `completedPhases`.
- `--quiet`: suppress ancillary human status output. Combine it with `--json` for automation; it does not hide errors.
- `--dry-run`: preview the operation without durable catalog edits or publication. Read-only network calls, Git fetches, logs, and temporary package validation may still occur. It does not simulate the game or install scripts.
- `--yes`: confirm operations that require confirmation. Validation and trust checks still apply.
- `--verbose`: include exception details in failures.
- `--help`: show the command's arguments and options.

JSON inputs use camelCase field names. `--input FILE` reads a document; `--input -` reads it from standard input. Inputs replace the indicated object, rather than merging arbitrary omitted properties. Relative paths are relative to the process's working directory.

## Project commands

- `project create`: create a public repository and local starter catalog. Arguments: `OWNER/NAME --plugin-id ID --project NEW-FOLDER`. Requires `--yes`; supports `--dry-run`.
- `project init`: create a local starter index. Arguments: `PLUGIN-ID --project FOLDER`. Refuses an existing index.
- `project recent`: list remembered projects.
- `project open`: open and remember the selected project.
- `project clone`: clone `OWNER/NAME` or its GitHub HTTPS URL into `--project FOLDER`. An existing checkout must belong to the requested repository; updates use a fast-forward-only pull.
- `project pull`: fast-forward-only pull in the selected project.
- `project repos`: list repositories available to the authenticated GitHub account.
- `project status`: show project identity, branch, origin, and local changes.

## Author commands

- `author show`: display the author profile.
- `author set`: replace it with `--input FILE`.

## Game commands

- `game repo`: show or set the saved release repository. Arguments: `GAME-ID [--repo OWNER/NAME]`.
- `game list`: list games.
- `game show`: show `GAME-ID` and its configuration.
- `game add`: add a game using `--input FILE`, or `--id ID --display-name NAME` and optional fields.
- `game update`: update `GAME-ID` using `--input FILE` or field options. Renaming a game with releases requires `--rewrite-release-game-id` to update those identities too.
- `game remove`: remove `GAME-ID` and its release bucket. Requires `--yes`.

Game field options: `--id`, `--display-name`, `--mod-name`, `--description`, `--steam-app-id`, `--exe-name`, repeatable `--tag`, and repeatable `--language`. Use complete JSON for additional model fields such as Linux detection, registry probes, and dependency lists. Consult `game update --help` before combining JSON and flags.

## Dependency commands

- `dependency list`: list dependencies for `GAME-ID`.
- `dependency show`: show `GAME-ID DEPENDENCY-ID`.
- `dependency set`: add or replace a dependency with `GAME-ID --input FILE`.
- `dependency remove`: remove `GAME-ID DEPENDENCY-ID`.
- `dependency presets`: list built-in presets and project presets; works without a project for built-ins.
- `dependency apply-preset`: copy `GAME-ID PRESET-ID` into the game's dependencies.

See [Dependencies](dependencies.md) for installation methods, pinning, and bundling tradeoffs.

## Script commands

- `script show`: inspect `GAME-ID SLOT`.
- `script set`: replace `GAME-ID SLOT --input FILE`.
- `script clear`: remove `GAME-ID SLOT`.

Slots are `pre-install`, `post-install`, and `post-uninstall`. See [Lifecycle scripts](scripts.md) for every field and bundling requirements.

## Package commands

- `package build`: build and validate a wrapped ZIP. Required: `--source FOLDER --game ID --version VERSION`. Optional: `--output ZIP`, `--target-platform TARGET`, `--proton-config FILE`, and `--xivlauncher-config FILE`. Uses the selected project's dependencies and script defaults. An omitted output uses the author builds directory.
- `package validate`: validate an existing ZIP with `--zip ZIP --plugin ID --game ID --version VERSION`. Does not require a project.
- `package hash`: compute SHA-256 using `--file FILE`. Does not require a project.

Valid targets are `windows`, `proton`, `linux`, `windows-linux`, and `xivlauncher`. Output ZIPs must be outside the source folder and must not already exist.

## Release commands

- `release list`: list releases for `GAME-ID`.
- `release show`: show `GAME-ID VERSION CHANNEL`.
- `release add`: add or replace a local release record with `GAME-ID --input FILE`.
- `release edit`: replace `GAME-ID CURRENT-VERSION CURRENT-CHANNEL --input FILE`.
- `release remove`: remove `GAME-ID VERSION CHANNEL`. Requires `--yes`.
- `release upload`: upload a validated public GitHub asset, leaving the catalog unchanged. Requires `--game ID --version VERSION --channel CHANNEL --zip ZIP` and `--yes`.
- `release publish`: upload/register the package and publish the GitHub catalog. Uses the same required arguments as `release upload` and requires `--yes`.

Both upload commands accept `--repo OWNER/NAME`, `--asset-name FILENAME`, `--notes TEXT`, and `--changelog-url HTTPS-URL`. Without `--repo`, the saved per-game repository is used. GitHub releases use tag `vVERSION`.

`release publish` additionally accepts `--index-message TEXT` and `--asset-destination github|patreon-post`. Patreon-post publication requires `--patreon-gate FILE` with campaign, tier, post ID, and filename metadata. The ZIP is uploaded to Patreon separately in the browser. No server destination exists.

## Index commands

- `index show`: display the current catalog.
- `index validate`: check the catalog with the manager's validation.
- `index reconcile`: inspect and, when permitted, adopt the live catalog. Use `--dry-run` first. `--yes` explicitly accepts replacing unpublished local changes when adoption requires consent.
- `index save`: validate and save a complete candidate supplied with `--input FILE`.
- `index membership`: report whether this plugin appears in the signed registry. It does not request or create a listing.
- `index publish`: validate, commit, and push the catalog to its GitHub origin and current branch. Optional `--message TEXT`; requires `--yes`.

There is no destination selector, server lock command, or registry administration command in this CLI.

## Directory commands

- `directory submit`: submit the already-published public catalog for discovery. Uses `--project PATH`, requires `--yes`, and supports `--dry-run`. This does not commit a catalog or upload a release.

`index publish` and `release publish` submit automatically after GitHub publication and verification. A successful submission adds `directorySubmitted` to the completed phases. If the directory step fails, the result retains `indexPublished` and `liveVerified`; retry this command without re-uploading your package. The server checks the repository's default branch.

## GitHub commands

- `github status`: check GitHub CLI availability and authentication.
- `github repos`: list repositories the account can publish to.
- `github releases`: list releases for `--repo OWNER/NAME`.

Run the separate `gh auth login` command to sign in. The author CLI delegates GitHub authentication to `gh` and does not ask you to paste access tokens into commands.

## Patreon commands

- `patreon status`: show author sign-in status and campaign identity.
- `patreon login`: open the creator OAuth sign-in flow.
- `patreon logout`: revoke/remove the local creator session.
- `patreon tiers`: list the signed-in creator's campaign tiers.
- `patreon post`: group for post-link commands.
- `patreon post validate`: validate `--url HTTPS-POST-URL` syntax and return its numeric ID. This is offline syntax validation, not attachment verification.

See [Patreon releases](patreon.md) for the browser-download workflow.

## Exit codes and automation

- `0`: success.
- `2`: command usage error.
- `3`: validation failure.
- `4`: authentication or trust failure.
- `5`: conflict, confirmation required, or interrupted publication.
- `130`: cancelled.

Successful JSON goes to standard output; error JSON goes to standard error. Check the exit code and read both streams. Do not treat an uploaded asset as proof that the catalog was also published: inspect `completedPhases`.

Example automation instruction: "Use amm-author with --project and --json. Inspect the existing game and dependencies. Build and validate a new package, then show release publish --dry-run. Publish only the requested version to the selected repository. Preserve existing releases and report any partial result."

For isolated jobs or tests, set `AMM_AUTHOR_CONFIG_DIR` to a dedicated author configuration directory and `AMM_AUTHOR_LOG_DIR` to a dedicated log directory. These do not replace GitHub CLI authentication configuration. The defaults use the user's local application data directory under `AccessibilityModManager-Author`.
