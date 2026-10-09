# Publishing walkthrough

## Create or open a catalog

A plugin catalog is a public GitHub repository containing `index.json`. One catalog can list several games. The repositories containing the actual mod releases can be different for each game.

Create a new public repository and a local starter catalog:

```sh
amm-author project create YOUR-NAME/accessibility-mods --plugin-id your-plugin --project ./catalog --dry-run
amm-author project create YOUR-NAME/accessibility-mods --plugin-id your-plugin --project ./catalog --yes
```

Choose an unused repository name and an empty local folder. This creates the repository and local index; it does not publish the index yet. If GitHub creation succeeds but cloning fails, the error reports that partial result. Use `project clone` to recover instead of creating another repository.

For an existing GitHub catalog:

```sh
amm-author project clone YOUR-NAME/accessibility-mods --project ./catalog
amm-author project status --project ./catalog
```

For an existing local checkout:

```sh
amm-author project open --project ./catalog
```

If a checkout does not yet contain an index, run `project init your-plugin --project ./catalog`. It refuses to overwrite an existing index. For offline preparation, `project init` also works in a new local folder, but publishing later requires a Git repository with a GitHub `origin` remote and a branch.

## Add your profile and game

Edit a copy of `docs/examples/author.json`, then run:

```sh
amm-author author set --input ./author.json --project ./catalog
amm-author game add --id example-game --display-name "Example Game" --exe-name Game.exe --steam-app-id 12345 --project ./catalog
amm-author game repo example-game --repo YOUR-NAME/example-game-mod --project ./catalog
```

Replace the illustrative executable and Steam app ID with the real values. `game repo` remembers where that game's ZIP releases belong. The repository must exist and be public. Omit `--repo` to inspect the saved choice.

`game add` and `game update` also accept `--input` with a complete camelCase game document. This is useful for Linux detection, registry probes, tags, languages, and other detailed settings. A JSON replacement is a complete replacement; include the values you want to keep. Flag-based updates preserve unspecified fields.

## Set dependencies and scripts

Use [dependencies](dependencies.md) for loaders and runtimes. Use [scripts](scripts.md) only when file-copy actions and dependency installation cannot do the required setup.

```sh
amm-author dependency presets --json
amm-author dependency set example-game --input ./dependency.json --project ./catalog
amm-author script set example-game post-install --input ./script.json --project ./catalog
```

These commands edit defaults in the catalog. Build a new ZIP after changing them: an already-built ZIP retains the manifest it was built with.

## Build and check the package

Put your mod files in a source folder laid out as they should appear in the game folder. Do not include the entire game or your development checkout.

```sh
amm-author package build --source ./mod-files --game example-game --version 1.0.0 --output ./packages/example-game-1.0.0.zip --project ./catalog
amm-author package validate --zip ./packages/example-game-1.0.0.zip --plugin your-plugin --game example-game --version 1.0.0
amm-author index validate --project ./catalog
```

The builder adds a root `manifest.json`, the `files/` payload, dependency declarations, and configured scripts. It uses the manager's package parser and validation. It refuses an output path that already exists; choose a new filename to preserve previous builds.

For native Linux or explicit Proton packages, see [platform support](catalog.md#platform-support). Package validation cannot prove that a mod works in the real game. Test installation, updating, uninstalling, and accessibility behavior with the manager before distributing widely.

## Publish to GitHub

```sh
amm-author release publish --game example-game --version 1.0.0 --channel stable --zip ./packages/example-game-1.0.0.zip --notes "First release" --project ./catalog --dry-run
amm-author release publish --game example-game --version 1.0.0 --channel stable --zip ./packages/example-game-1.0.0.zip --notes "First release" --project ./catalog --yes
```

The command uses the saved game repository; `--repo OWNER/NAME` overrides it and remembers the successful choice. It uploads the ZIP to GitHub tag `v1.0.0`, checks the public download's SHA-256, saves the release URL and hash into the catalog, then commits and pushes the index repository. After public verification, it submits the catalog to the manager directory. The catalog must be on the repository’s default branch. It reports completed phases if a later step fails. A release asset may already be uploaded when an index push fails; read the result before retrying.

An existing asset with different bytes requires explicit confirmation and is identified in the preview. Prefer a new version for changed packages so existing download URLs continue to identify the same bytes.

The catalog repository gets a normal index commit. It does not get a GitHub release merely because it hosts the catalog. The publisher stages only `index.json`, refuses unrelated staged changes, respects Git signing, and never force-pushes.

If you only changed game descriptions, dependencies, or author information:

```sh
amm-author index publish --message "Update catalog details" --project ./catalog --dry-run
amm-author index publish --message "Update catalog details" --project ./catalog --yes
```

Changing catalog dependencies does not rebuild an existing ZIP. Keep its manifest consistent when preparing the next release.

## Directory submission

Publication automatically submits your catalog for discovery. If GitHub publication succeeds but the directory request fails, the CLI reports both facts and returns a nonzero exit code. Retry only the directory step:

```sh
amm-author directory submit --project ./catalog --dry-run
amm-author directory submit --project ./catalog --yes
```

This checks the existing public catalog; it does not upload the ZIP again. Users still choose which third-party sources to add.

## Recover from conflicts

Use `project status` to inspect the checkout. `project pull` performs a fast-forward-only pull. `index reconcile --dry-run` checks the live catalog; if adopting it would discard unpublished edits, the CLI refuses unless you explicitly accept adoption with `index reconcile --yes`. Save your edits before doing so.

Network, signature, private-repository, hash, or validation failures are errors, not successful publications. Read `completedPhases` in JSON output to distinguish an uploaded asset from a fully published catalog. Do not respond to a signing failure by disabling Git signing.
