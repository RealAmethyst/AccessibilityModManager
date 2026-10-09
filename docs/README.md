# Author CLI

`amm-author` creates and maintains the GitHub plugin catalogs read by Accessibility Mod Manager. It replaces the Windows and Linux desktop AuthorTool. You can run commands yourself or have an AI assistant run them in your project folder.

## Start here

1. Extract the complete CLI ZIP. Windows includes `amm-author.exe`; Linux includes `amm-author`. The .NET runtime is included. On Linux, run `chmod +x amm-author` if needed.
2. Install [Git](https://git-scm.com/) and [GitHub CLI](https://cli.github.com/). Run `gh auth login` once. Git must also have your commit name and email configured. Existing Git signing settings are respected.
3. Open a terminal in the extracted folder. Run `./amm-author help` on Linux or `./amm-author.exe help` in PowerShell. Examples below use `amm-author`; use the local executable path or put its directory on PATH.
4. Follow [the publishing walkthrough](publishing.md).

You do not need a custom server, SSH upload settings, a registry administrator key, or access to Amethyst's dashboard. Public catalogs and public release ZIPs are hosted on GitHub. Patreon posts remain available for gated releases.

## Documentation

- [Publishing walkthrough](publishing.md): create or clone a repository, add games, build packages, and publish.
- [Catalog and package concepts](catalog.md): identifiers, games, releases, platform support, and validation.
- [Dependencies](dependencies.md): loaders, runtimes, automatic installation, pinned downloads, and when bundling makes sense.
- [Lifecycle scripts](scripts.md): what scripts can do, when they run, and what users consent to.
- [Patreon releases](patreon.md): post links, tiers, manual attachments, and verification limits.
- [Command reference](commands.md): every command, options, automation, and exit codes.

The complete `docs` directory is included in each CLI ZIP, including JSON examples in `docs/examples`. Paths in examples are relative to your terminal's current folder unless stated otherwise.

## Help and automation

Run `amm-author help` for command groups, `amm-author help release publish` for a specific command, or append `--help` to any command.

Use `--project PATH` to select the catalog explicitly. Use `--json` for machine-readable results and `--dry-run` to preview supported changes. Repository creation, release uploading, and index publication require `--yes`. It confirms that operation but never bypasses validation.

## Existing authors

Your `index.json` remains the project format. Open its folder with `project open --project PATH`. The CLI retains the previous author configuration location, recent projects, and per-game GitHub repository choices. Catalog publication now targets the GitHub repository's current branch; saved server destination choices are not used.

Keep your old application folder as a backup when moving to the CLI. Extract future CLI versions into a new folder. Projects and author configuration are separate from the executable.

Amethyst's web dashboard and the manager's ability to install existing server-hosted releases remain separate from this CLI.

## Finding third-party plugins

After successful GitHub catalog publication, the CLI submits the public catalog to the manager's plugin directory. Publish on the repository's default branch. Users choose Sources in the manager, select your plugin, and review the source before adding it. Sources already added offer Remove instead of Add.

A directory submission is discovery metadata, not an endorsement. It sends the repository, index path, plugin ID, and public index URL; it does not upload your mod files or GitHub credentials. If submission fails after publication, your GitHub catalog remains published. Retry with `amm-author directory submit --project PATH --yes`.

The manager announces newly added games for sources users have added and authors whose mods they have installed. The first successful refresh establishes a baseline without announcing all existing games.

## Credits

The CLI builds on [buu420's Author CLI contribution, PR 1](https://github.com/RealAmethyst/AccessibilityModManager/pull/1), adapted to the current manager and the GitHub-focused authoring workflow.
