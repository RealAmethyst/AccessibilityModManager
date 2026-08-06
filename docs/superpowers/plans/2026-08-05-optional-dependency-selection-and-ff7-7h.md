# Optional Dependency Selection and FFVII 7th Heaven Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan.

**Goal:** Let users optionally install the official 7th Heaven package while making FFVII 2013 and FFVII 2026 detection edition-safe.

**Architecture:** Extend the dependency-host consent contract with an explicit selection result, keep required-dependency enforcement in `InstallerEngine`, and expose required/optional state through native WPF checkboxes. Independently harden the Blind Soldier catalog with concrete executable/file/folder probes and an optional registry-detected official 7th Heaven installer.

**Tech Stack:** .NET 9, C#, WPF, xUnit, PowerShell, JSON, GitHub CLI.

## Global Constraints

- Preserve existing required dependency and required manual dependency behavior.
- Optional dependencies must never block installation of the core mod.
- Required selections are enforced by the engine even if a host returns a malformed selection.
- Keep all prompt items in catalog manifest order.
- Do not install, configure, or overwrite FFNx directly.
- Do not modify either repository's main branch until verification succeeds.
- Apply source edits with `apply_patch`.

---

## Task 1: Lock the manager behavior with failing tests

**Files:**

- Modify: `tests/AccessibilityModManager.Tests/Installer/EngineIntegrityTests.cs`
- Create or modify: the appropriate App view-model test file under `tests/AccessibilityModManager.Tests/`

- [ ] Add a host test double that records the real prompt and returns accepted plus selected optional IDs.
- [ ] Add a test proving a missing optional auto dependency is offered, defaults to unselected at the UI boundary, is not downloaded when unselected, and does not block core installation.
- [ ] Add a test proving a selected optional auto dependency installs before the core mod.
- [ ] Add a test proving a selected optional installer failure is reported but the core mod still installs.
- [ ] Add a test proving a required auto dependency cannot be omitted by the returned optional selection and remains fatal on failure.
- [ ] Add view-model tests proving required items are checked/locked and announced as required, while optional items are unchecked/toggleable and announced as optional.
- [ ] Run only the new tests and confirm they fail for the missing production behavior.

Verification command:

```powershell
dotnet test tests\AccessibilityModManager.Tests\AccessibilityModManager.Tests.csproj --configuration Release --filter "FullyQualifiedName~Dependency"
```

## Task 2: Implement the manager selection contract

**Files:**

- Modify: `src/AccessibilityModManager.Core/Interfaces/IDependencyHost.cs`
- Modify: `src/AccessibilityModManager.Infrastructure/Installer/InstallerEngine.cs`
- Modify: `tests/AccessibilityModManager.Tests/Installer/EngineIntegrityTests.cs`

- [ ] Introduce `DependencyInstallDecision` with `Accepted` and selected optional dependency IDs.
- [ ] Add explicit `IsRequired` to `DependencyInstallPromptItem`.
- [ ] Change `ConfirmDependencyInstallAsync` to return the decision.
- [ ] Build the prompt from missing required auto dependencies plus missing optional auto dependencies, preserving manifest order.
- [ ] Always process required dependencies, process only selected optional dependencies, and ignore unknown selected IDs.
- [ ] On selected optional failure or failed recheck, log and continue without an acquisition; retain fatal/rollback behavior for required failures.
- [ ] Update the existing test hosts to the new contract.
- [ ] Run focused engine tests until green.

Verification command:

```powershell
dotnet test tests\AccessibilityModManager.Tests\AccessibilityModManager.Tests.csproj --configuration Release --filter "FullyQualifiedName~EngineIntegrityTests"
```

## Task 3: Implement the accessible dependency selection dialog

**Files:**

- Modify: `src/AccessibilityModManager.App/ViewModels/DependencyWarningDialogViewModel.cs`
- Modify: `src/AccessibilityModManager.App/Views/DependencyWarningDialog.xaml`
- Modify: `src/AccessibilityModManager.App/Views/DependencyWarningDialog.xaml.cs`
- Modify: `src/AccessibilityModManager.App/Services/DialogScriptHost.cs`
- Modify: App view-model tests from Task 1

- [ ] Represent each prompt item with mutable `IsSelected`, `CanChangeSelection`, and an announcement that updates with selection state.
- [ ] Render each item as a native WPF checkbox, binding `IsEnabled` so required entries cannot be cleared.
- [ ] Label the action `Continue` and expose `Continue with selected dependencies` through UI Automation.
- [ ] Return an accepted decision containing only selected optional IDs; return a declined decision on cancellation.
- [ ] Keep focus behavior accessible and do not auto-start a download on dialog load.
- [ ] Run view-model tests and build the WPF application.

Verification commands:

```powershell
dotnet test tests\AccessibilityModManager.Tests\AccessibilityModManager.Tests.csproj --configuration Release --filter "FullyQualifiedName~DependencyWarning"
dotnet build src\AccessibilityModManager.App\AccessibilityModManager.App.csproj --configuration Release
```

## Task 4: Lock and correct the Blind Soldier catalog

**Files:**

- Modify: `C:/Users/buu42/Documents/buu-s-mods-author-live/.worktrees/ff7-correct-detection/index.json`
- Create: `C:/Users/buu42/Documents/buu-s-mods-author-live/.worktrees/ff7-correct-detection/tests/Verify-Ff7Catalog.ps1`

- [ ] First create a runnable catalog contract test that fails against the current JSON.
- [ ] Assert the 2013 fixture passes only the 2013 probes and the 2026 fixture passes only the 2026 probes.
- [ ] Assert incomplete and cross-edition roots fail.
- [ ] Assert both entries offer `seventh-heaven` as optional, registry-detected version `4.5.2.0`, using the official installer URL and verified SHA-256.
- [ ] Assert no dependency with a 7th Heaven ID downloads an FFNx asset or checks `FFNx.toml`.
- [ ] Run the test and observe failure.
- [ ] Update both game definitions and rerun until green.

Verification command:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-Ff7Catalog.ps1
```

## Task 5: Full verification and local integration checks

**Files:** No planned source changes.

- [ ] Run the complete manager test suite.
- [ ] Run the catalog contract test.
- [ ] Validate the catalog using the author CLI.
- [ ] Verify the official 7th Heaven installer hash independently.
- [ ] Verify current FFVII 2026 is detected only as 2026 and the stale 2013 override is rejected.
- [ ] Inspect final diffs for accidental package/release changes.

Verification commands:

```powershell
dotnet test AccessibilityModManager.slnx --configuration Release
powershell -NoProfile -ExecutionPolicy Bypass -File tests\Verify-Ff7Catalog.ps1
```

## Task 6: Publish the catalog and prepare the manager contribution

**Files:** No additional planned source changes.

- [ ] Commit manager changes on `agent/ff7-optional-7h`.
- [ ] Push the manager branch to `buu420/AccessibilityModManager` and open a draft PR to `RealAmethyst/AccessibilityModManager`.
- [ ] Commit catalog changes on `agent/ff7-correct-detection`.
- [ ] Fast-forward the owned catalog main branch only after tests pass, then push it.
- [ ] Fetch the raw published `index.json` and verify probes, optional dependency metadata, and current releases.
- [ ] Report the PR and publication links plus exact verification results.

