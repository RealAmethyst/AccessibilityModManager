# Full Accessibility Mod Manager Author CLI Design

## Purpose

Build a local, screen-reader-friendly command-line counterpart to RealAmethyst's current Accessibility Mod Manager AuthorTool. The CLI must provide feature parity with the AuthorTool while using the same models, validation rules, trust checks, packaging code, and publishing behavior. It is intended to let Codex and the user maintain Blind Soldier and other accessibility-mod catalogs without operating the WPF interface.

Development starts from upstream commit `9e2d223762aa21a2fc765bae55380699a2532746`, where the AuthorTool is version 0.28.0. The older local `PluginIndexAuthor-0.25.1.exe` was inspected only to identify the user's existing workflow and is not an implementation base.

This is a local personal-use modification. Nothing will be forked, pushed, submitted upstream, or redistributed until the user has spoken with RealAmethyst and obtained any permission that is required. No protected security or integrity mechanism will be weakened, bypassed, or replaced.

## Chosen Approach

Add a console application to the current Accessibility Mod Manager solution and move reusable non-UI author workflows into a shared authoring library. Both the existing WPF AuthorTool and the CLI will call those shared workflows.

This is preferred over the alternatives:

- Referencing the WPF executable directly would pull UI state and dialog callbacks into console operations and would be fragile as the GUI changes.
- Automating the AuthorTool window would retain the accessibility and focus problems that motivated the CLI.
- Independently reimplementing the JSON and publishing rules would allow the GUI and CLI to disagree and could bypass later safety fixes.

## Project Structure

Add these projects:

- `AccessibilityModManager.Authoring`: a non-UI class library containing author workflow orchestration, typed command inputs and results, and reusable services currently embedded in the AuthorTool project or its view models.
- `AccessibilityModManager.AuthorCli`: a `net10.0-windows` console application published as `amm-author.exe`.

Keep these existing projects:

- `AccessibilityModManager.Core`: public models and interfaces.
- `AccessibilityModManager.Infrastructure`: installer, validation, security, network, and persistence implementation.
- `AccessibilityModManager.AuthorTool`: the WPF interface. It will consume the shared authoring library without losing existing behavior.

The shared library will expose focused workflows rather than UI abstractions:

- `ProjectWorkflow`
- `GameWorkflow`
- `DependencyWorkflow`
- `LifecycleScriptWorkflow`
- `PackageWorkflow`
- `ReleaseWorkflow`
- `IndexWorkflow`
- `PatreonWorkflow`
- `ServerWorkflow`
- `SigningWorkflow`
- `RegistryAdminWorkflow`

Each workflow accepts typed input records and returns typed result records. Decisions that require consent return a preview describing the exact action. The WPF caller presents its existing dialog; the CLI caller presents a text prompt or requires `--yes` in noninteractive mode.

## Command Surface

Use `System.CommandLine` 2.0.10 for parsing, help, validation, and completion support. Commands are grouped by the AuthorTool feature they represent:

```text
amm-author project ...
amm-author author ...
amm-author game ...
amm-author dependency ...
amm-author script ...
amm-author package ...
amm-author release ...
amm-author index ...
amm-author github ...
amm-author patreon ...
amm-author server ...
amm-author signing ...
amm-author registry ...
```

The command families cover:

- Project creation, recent projects, opening local projects, listing writable GitHub repositories, cloning, pulling, and project status.
- Author profile display and editing.
- Game creation, editing, removal, and listing.
- Tags, languages, filters, executable and Steam detection fields, dependencies, dependency presets, check rules, and auto-install metadata.
- Pre-install, post-install, and post-uninstall lifecycle scripts, including external source paths and install-to-game-folder behavior.
- Wrapped package building, manifest generation, package validation, and SHA256 calculation.
- Release listing, creation, editing, removal, URL-only releases, GitHub uploads, server uploads, channels, notes, changelog links, and Patreon gates.
- Index loading, formatting, validation, reconciliation with the live catalog, publish-destination selection, saving, publishing, membership checks, and status.
- GitHub CLI availability, authentication, repository listing, release creation or update, asset replacement, index commit, and push.
- Patreon sign-in, sign-out, status, tier listing, post validation, attachment selection, and gated-release metadata.
- SFTP settings, host-key pinning, connection tests, public and gated asset upload, server self-test, and publish-lock inspection or removal.
- Catalog signing-key creation, import, export, backup, passphrase change, status, claim signing, head recovery, and reconciliation.
- Registry-admin project handling, JSON editing and validation, signing, registry-pair publication, release publication, and commit/push.

Registry-admin commands are compiled only when the existing `RegistryAdmin` build property is enabled. A standard build will explain that the command is unavailable instead of silently omitting the reason. The CLI will not add an option that bypasses this build gate.

## Project Resolution

Commands that operate on a plugin catalog resolve the project in this order:

1. The explicit `--project <path>` option.
2. The current directory when it contains `index.json`.
3. The last-opened project in `%LocalAppData%\AccessibilityModManager-Author\config.json`.

The resolved absolute path and plugin id are printed before a mutating operation. The CLI shares the AuthorTool's existing configuration, recent-project list, source-repository mappings, script paths, server settings, DPAPI-protected credentials, signing keys, and publishing records.

## Complete Release Flow

Granular commands remain available, but `amm-author release publish` provides the AuthorTool's complete normal release workflow:

1. Resolve and lock the project.
2. Load the author configuration and `index.json`.
3. Reconcile local and live catalog state using the existing trust rules.
4. Build or select a wrapped ZIP.
5. Copy the package into a private staging directory and hold it read-only.
6. Validate the manifest through `PluginPackageValidation` using the requested plugin id, game id, and version.
7. Hash the exact staged bytes.
8. Check the chosen GitHub, server, or Patreon destination.
9. Upload the same staged bytes that were validated and hashed.
10. Add or replace the release record in memory.
11. Validate the complete plugin index through `PluginIndexValidation`.
12. Save `index.json` durably with an updated `generatedAt` value.
13. Publish through the existing GitHub or signed-server coordinator.
14. Read back the live result and update the local publishing record.
15. Apply any deferred Patreon gate change only after the catalog describes it.

If a later phase fails after an earlier remote phase succeeded, the CLI reports each completed and incomplete phase. It never converts partial completion into generic success.

## Interaction and Accessibility

Default output is concise plain text with no animated spinners, cursor rewriting, decorative tables, color-only distinctions, or unlabeled symbols. Progress is emitted as complete lines suitable for Prism, NVDA, Narrator, and terminal review.

Human-readable status and warnings go to standard error. Requested data goes to standard output. `--json` writes one final structured result object to standard output while retaining progress on standard error. `--quiet` suppresses nonessential progress but never warnings or errors.

Interactive mode asks one direct question at a time. Secret values use concealed console input. Automation can provide a secret through standard input using a purpose-specific `--passphrase-stdin` option. Passphrases, access tokens, private keys, and DPAPI plaintext are never accepted as ordinary command-line values and never written to logs.

Every command has useful `--help` text and examples. Missing required input in noninteractive mode produces an immediate usage error instead of waiting for a prompt.

## Confirmation and Dry Run

Read-only commands never prompt. Ordinary local additions and edits may prompt for missing values but do not require redundant confirmation after all values have been displayed.

Commands that delete metadata, replace an existing release asset, publish a catalog, change a Patreon gate, remove a signing key, break a publish lock, or alter the registry show one exact action summary and require confirmation. `--yes` supplies that confirmation for automation. It does not bypass validation, trust, authentication, or build gates.

`--dry-run` performs parsing, project resolution, local/live reconciliation, package and catalog validation, and action planning without writing files, committing, uploading, signing, changing gates, or removing locks.

## Security and Concurrency

The CLI uses the same implementations as the current manager and AuthorTool for:

- SHA256 package verification
- ZIP and path-safety checks
- Manifest action allowlisting
- HTTPS enforcement
- Plugin, game, and release identity binding
- Dependency uniqueness validation
- Registry signature and trust-anchor verification
- Catalog claim signing and replay protection
- Publisher-head tracking and reconciliation
- GitHub destination and repository-visibility checks
- SFTP host-key pinning
- Publish locks and compare-before-break behavior
- Patreon gate sequencing

No `--force`, environment variable, debug mode, or admin mode may weaken these checks.

A project-level cross-process lock protects `index.json` and author configuration from concurrent GUI and CLI writes. Existing remote publish locks continue to protect server operations. When another process owns a lock, the CLI identifies the lock and exits without changing state.

## Exit Codes

- `0`: completed successfully
- `2`: invalid command, missing input, or noninteractive prompt required
- `3`: package, manifest, index, or trust validation failed
- `4`: authentication, authorization, or configuration problem
- `5`: local or remote conflict, publishing failure, or partial completion
- `130`: cancelled by the user or Ctrl+C

Each failure result contains a stable machine-readable error category in JSON mode in addition to the human explanation.

## Testing

Add tests at three levels:

1. Parser and handler tests verify every command family, required options, project resolution, output routing, secret-input rules, confirmation behavior, JSON shape, and exit code.
2. Workflow tests use disposable projects to compare GUI-facing and CLI-facing operations against the same shared services. They verify equivalent games, dependencies, scripts, manifests, release records, formatted indexes, validation reports, and publish previews.
3. Integration tests use local bare Git repositories, controlled HTTP handlers, fake `git` and `gh` process results, and controlled SFTP boundaries. They verify successful publication, refusal paths, partial completion reporting, lock conflicts, cancellation, and live read-back behavior without touching the user's real repositories or server.

All existing solution tests must remain green. New tests must cover both the standard build and the `RegistryAdmin=true` build. A disposable end-to-end catalog and package exercise must succeed before local installation.

## Build and Local Delivery

Add `installer/build-author-cli.ps1` with framework-dependent and self-contained Windows x64 modes matching the existing AuthorTool build conventions. Produce:

- `amm-author.exe`
- `amm-author-admin.exe` when built with `RegistryAdmin=true`

Install the local self-contained build under `C:\Users\buu42\Tools\AccessibilityModManager`. Add that directory to the current user's PATH only if it is not already present. Verify invocation from outside the source tree with `amm-author --version`, `amm-author --help`, and a read-only command against a disposable project.

The local Git branch, commits, executable, and test artifacts remain on this machine. There will be no GitHub fork, push, pull request, release asset, or redistribution until the user explicitly authorizes it after speaking with RealAmethyst.

## Non-Goals

- Replacing or removing the WPF AuthorTool.
- Changing the plugin-index or manifest schema merely for CLI convenience.
- Adding security bypasses or alternative unsigned publishing paths.
- Automating the WPF interface.
- Publishing the local build or source changes.
- Modifying Blind Soldier as part of this work; the CLI is tooling for later releases.
