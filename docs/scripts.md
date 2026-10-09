# Lifecycle scripts

## When to use a script

Most mods only need file-copy actions and dependencies. A script is useful when setup requires a configuration change, registration, migration, or cleanup that ordinary file actions cannot express.

Scripts run code on the user's machine. Explain their actual behavior precisely. The manager shows the executable, what it does, why it is required, what it modifies, and whether it needs administrator rights before asking the user to consent.

## Lifecycle slots

- `pre-install`: setup before the package's normal installation actions.
- `post-install`: setup after the files are installed.
- `post-uninstall`: cleanup after uninstalling.

Each game has one default script per slot. The builder copies the chosen defaults into that release's manifest. Editing the defaults later does not change an already-published ZIP.

## Script fields

- `executable`: path inside the package, such as `files/scripts/setup.ps1`. Supported extensions are `.exe`, `.ps1`, `.cmd`, and `.bat`.
- `what`: the action the script performs.
- `why`: why normal installation is insufficient.
- `modifies`: the files, settings, registrations, or other state it changes.
- `needsAdmin`: requests elevation when required; avoid requesting it for ordinary game-file operations.
- `failureFatal`: whether a script failure aborts the operation. Fatal installation failures enter the manager's rollback path.
- `runOnUpdate`: whether the script should also run for a mod update.
- `installToGameFolder`: copy the script into the game folder so it remains available when needed later.
- `runFromGameFolder`: run with the game folder as the working directory.

Do not assume file rollback reverses arbitrary registry or external changes made by your script. Make setup repeatable, implement the appropriate cleanup, and test failure recovery.

## Configure and bundle a script

Copy and edit `docs/examples/script.json`, and place the actual executable under your package source folder, omitting the manifest’s `files/` prefix. For `files/scripts/setup.ps1`, that is `mod-files/scripts/setup.ps1`.

```sh
amm-author script set example-game post-install --input ./script.json --project ./catalog
amm-author script show example-game post-install --project ./catalog
amm-author package build --source ./mod-files --game example-game --version 1.0.0 --output ./packages/example-game-1.0.0.zip --project ./catalog
```

The builder checks that each declared executable exists and puts it at the path the manifest declares. Missing files, unsupported extensions, unsafe paths, and absent explanations prevent publication. The CLI builds and inspects scripts; it does not execute them as an authoring test.

Remove a default with `script clear example-game post-install --project ./catalog` and rebuild affected packages. A script-only package is allowed when it satisfies the manager's validation.

## Testing

Test first installation, update, uninstall, refusal of the script consent prompt, and a controlled script failure. Check the actual changes against `what`, `why`, and `modifies`. Test the intended platform: Windows scripts cannot be assumed to work under Proton or native Linux.
