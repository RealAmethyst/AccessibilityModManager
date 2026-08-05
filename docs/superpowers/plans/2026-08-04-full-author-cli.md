# Full Accessibility Mod Manager Author CLI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a local `amm-author.exe` with feature parity with Accessibility Mod Manager AuthorTool 0.28.0 while preserving the exact package, catalog, trust, signing, and publishing safeguards.

**Architecture:** Move the AuthorTool's UI-independent services unchanged into a shared `AccessibilityModManager.Authoring` assembly, then add typed workflows that both the WPF application and a new `AccessibilityModManager.AuthorCli` console application can call. The CLI uses `System.CommandLine` 2.0.10, screen-reader-safe line output, JSON payloads for lossless complex model editing, existing AuthorTool configuration, and the existing security services.

**Tech Stack:** C# 14, .NET 10 Windows, WPF, System.CommandLine 2.0.10, Microsoft.Extensions.DependencyInjection 10.0.3, Serilog 4.3.1, SSH.NET 2024.1.0, xUnit 2.9.3, Git and GitHub CLI.

## Global Constraints

- Start from upstream commit `9e2d223762aa21a2fc765bae55380699a2532746`, AuthorTool version 0.28.0.
- Keep the work local on branch `local/full-author-cli`; do not fork, push, open a pull request, create a release, or redistribute a binary.
- Do not weaken, bypass, replace, or add override switches for any package, path, manifest, HTTPS, identity, registry, signing, replay, host-key, or publish-lock check.
- Preserve `%LocalAppData%\AccessibilityModManager-Author\config.json` compatibility and DPAPI protection.
- Target `net10.0-windows` and publish Windows x64 executables.
- Standard builds exclude registry-admin execution; admin builds enable it only through the existing `RegistryAdmin=true` build property.
- Default output is complete plain-text lines without animation, cursor rewriting, color-only meaning, decorative tables, or unlabeled symbols.
- Passphrases are read through concealed input or `--passphrase-stdin`; they are never accepted as ordinary option values or logged.
- `--yes` confirms an action but never bypasses validation or trust gates.
- `--dry-run` performs reads and validation but never writes, commits, uploads, signs, changes gates, or removes locks.
- Use test-first development for every new behavior and run `dotnet test AccessibilityModManager.slnx` before each task commit.

## File Structure

### Shared authoring assembly

- Create `src/AccessibilityModManager.Authoring/AccessibilityModManager.Authoring.csproj`.
- Move the 22 UI-independent files from `src/AccessibilityModManager.AuthorTool/Services/` to `src/AccessibilityModManager.Authoring/Services/` without changing their namespaces or behavior.
- Create `src/AccessibilityModManager.Authoring/Workflows/WorkflowResult.cs` for stable operation categories and typed results.
- Create `src/AccessibilityModManager.Authoring/Workflows/AuthorProjectContext.cs` for project resolution and locking.
- Create `src/AccessibilityModManager.Authoring/Workflows/JsonPayloadService.cs` for exact model import and export.
- Create `src/AccessibilityModManager.Authoring/Workflows/CatalogWorkflow.cs` for author, game, dependency, script, and release mutations.
- Create `src/AccessibilityModManager.Authoring/Workflows/PackageWorkflow.cs` for build and package validation.
- Create `src/AccessibilityModManager.Authoring/Workflows/ReleaseWorkflow.cs` for staged-byte validation and GitHub/server release publication.
- Create `src/AccessibilityModManager.Authoring/Workflows/IndexWorkflow.cs` for reconciliation, validation, saving, and destination publication.
- Create `src/AccessibilityModManager.Authoring/Workflows/PatreonWorkflow.cs` and `ServerWorkflow.cs`.
- Create `src/AccessibilityModManager.Authoring/Workflows/SigningWorkflow.cs` and `RegistryAdminWorkflow.cs`.

### CLI assembly

- Create `src/AccessibilityModManager.AuthorCli/AccessibilityModManager.AuthorCli.csproj`.
- Create `src/AccessibilityModManager.AuthorCli/Program.cs` and `CliServices.cs`.
- Create `src/AccessibilityModManager.AuthorCli/Console/CliConsole.cs`, `SecretReader.cs`, `OutcomeWriter.cs`, and `ExitCodes.cs`.
- Create one focused command-registration file for each top-level command under `src/AccessibilityModManager.AuthorCli/Commands/`.
- Create `src/AccessibilityModManager.AuthorCli/Properties/PublishProfiles/win-x64.pubxml`.

### Existing WPF application

- Modify `src/AccessibilityModManager.AuthorTool/AccessibilityModManager.AuthorTool.csproj` to reference Authoring.
- Modify `src/AccessibilityModManager.AuthorTool/App.xaml.cs` to register shared workflows.
- Modify author view models only where orchestration moves into a workflow; keep bindings, announcements, and dialog behavior unchanged.

### Tests and delivery

- Add `tests/AccessibilityModManager.Tests/Authoring/` workflow tests.
- Add `tests/AccessibilityModManager.Tests/AuthorCli/` parser, handler, output, and parity tests.
- Add `installer/build-author-cli.ps1`.
- Update `README.md` with local build and command documentation without linking to an unofficial binary.

---

### Task 1: Extract the UI-independent Authoring Assembly

**Files:**
- Create: `src/AccessibilityModManager.Authoring/AccessibilityModManager.Authoring.csproj`
- Move: `src/AccessibilityModManager.AuthorTool/Services/*.cs` to `src/AccessibilityModManager.Authoring/Services/*.cs`
- Modify: `src/AccessibilityModManager.AuthorTool/AccessibilityModManager.AuthorTool.csproj`
- Modify: `tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj`
- Modify: `AccessibilityModManager.slnx`
- Test: `tests/AccessibilityModManager.Tests/Authoring/AuthoringAssemblyTests.cs`

**Interfaces:**
- Consumes: existing service types in namespace `AccessibilityModManager.AuthorTool.Services`.
- Produces: the same public service types from assembly `AccessibilityModManager.Authoring`, with unchanged names and signatures.

- [ ] **Step 1: Record the clean baseline**

Run:

```powershell
dotnet test AccessibilityModManager.slnx --no-restore
```

Expected: all existing tests pass at commit `9e2d223` plus the design commit.

- [ ] **Step 2: Write the failing assembly-boundary test**

Create:

```csharp
using AccessibilityModManager.AuthorTool.Services;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class AuthoringAssemblyTests
{
    [Fact]
    public void UiIndependentServicesLiveInAuthoringAssembly()
    {
        Assert.Equal("AccessibilityModManager.Authoring",
            typeof(ManifestBuilderService).Assembly.GetName().Name);
        Assert.Equal(typeof(ManifestBuilderService).Assembly,
            typeof(IndexPublishCoordinator).Assembly);
        Assert.Equal(typeof(ManifestBuilderService).Assembly,
            typeof(ClaimSigningKeyStore).Assembly);
    }
}
```

- [ ] **Step 3: Run the boundary test and verify failure**

Run:

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter FullyQualifiedName~AuthoringAssemblyTests
```

Expected: failure because each service still lives in `AccessibilityModManager.AuthorTool`.

- [ ] **Step 4: Create the shared project and move services without logic edits**

Use this project definition:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\AccessibilityModManager.Core\AccessibilityModManager.Core.csproj" />
    <ProjectReference Include="..\AccessibilityModManager.Infrastructure\AccessibilityModManager.Infrastructure.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="Serilog" Version="4.3.1" />
    <PackageReference Include="SSH.NET" Version="2024.1.0" />
  </ItemGroup>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AssemblyName>AccessibilityModManager.Authoring</AssemblyName>
  </PropertyGroup>
</Project>
```

Move every service file with its contents and `AccessibilityModManager.AuthorTool.Services` namespace unchanged. Add an Authoring project reference to the WPF and test projects, remove the WPF project's now-unused SSH.NET reference, and add the new project under `/src/` in `AccessibilityModManager.slnx`.

- [ ] **Step 5: Run boundary and regression tests**

Run:

```powershell
dotnet test AccessibilityModManager.slnx
```

Expected: the new boundary test and every existing test pass.

- [ ] **Step 6: Commit the pure extraction**

```powershell
git add AccessibilityModManager.slnx src tests
git commit -m "refactor: share AuthorTool services"
```

---

### Task 2: Add CLI Results, Console I/O, Project Resolution, and Locking

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/WorkflowResult.cs`
- Create: `src/AccessibilityModManager.Authoring/Workflows/AuthorProjectContext.cs`
- Create: `src/AccessibilityModManager.Authoring/Workflows/JsonPayloadService.cs`
- Create: `src/AccessibilityModManager.AuthorCli/AccessibilityModManager.AuthorCli.csproj`
- Create: `src/AccessibilityModManager.AuthorCli/Program.cs`
- Create: `src/AccessibilityModManager.AuthorCli/CliServices.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Console/CliConsole.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Console/SecretReader.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Console/OutcomeWriter.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Console/ExitCodes.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/RootCommands.cs`
- Modify: `AccessibilityModManager.slnx`
- Modify: `tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/ProjectResolutionTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/OutcomeWriterTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/SecretReaderTests.cs`

**Interfaces:**
- Produces: `WorkflowResult<T>`, `WorkflowErrorKind`, `AuthorProjectContext.ResolveAsync`, `JsonPayloadService.ReadAsync<T>`, `ICliConsole`, `CliExitCode`, and a runnable `amm-author --version`.
- Consumes: `AuthorConfigService`, `IndexFileService`, and `CrossProcessFileLock.AcquireAsync`.

- [ ] **Step 1: Write project-resolution tests**

Cover explicit path, current directory, last-opened project, missing project, and a project whose `index.json` is absent. The core assertion is:

```csharp
var resolved = await context.ResolveAsync(explicitPath, currentDirectory, CancellationToken.None);
Assert.Equal(Path.GetFullPath(explicitPath), resolved.ProjectPath);
Assert.Equal("sample", resolved.Index.PluginId);
```

- [ ] **Step 2: Write output and secret-input tests**

Verify human output uses complete lines, JSON output is a single valid object, errors do not enter standard output in JSON mode, and concealed input handles backspace without echoing characters:

```csharp
var result = new WorkflowResult<string>("ok", "value", []);
writer.Write(result, json: true);
Assert.Equal("{\"status\":\"ok\",\"value\":\"value\",\"messages\":[]}" + Environment.NewLine,
    console.Stdout);
Assert.DoesNotContain("secret", console.AllWrittenText);
```

- [ ] **Step 3: Run the new tests and verify compilation failure**

Run:

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~ProjectResolutionTests|FullyQualifiedName~OutcomeWriterTests|FullyQualifiedName~SecretReaderTests"
```

Expected: failure because the workflow and CLI types do not exist.

- [ ] **Step 4: Implement stable result and exit contracts**

Define:

```csharp
public enum WorkflowErrorKind { None, Usage, Validation, Authentication, Conflict, Cancelled }

public sealed record WorkflowResult<T>(
    string Status,
    T? Value,
    IReadOnlyList<string> Messages,
    WorkflowErrorKind ErrorKind = WorkflowErrorKind.None,
    IReadOnlyList<string>? CompletedPhases = null);

public enum CliExitCode
{
    Success = 0,
    Usage = 2,
    Validation = 3,
    Authentication = 4,
    Conflict = 5,
    Cancelled = 130
}

public sealed record ResolvedAuthorProject(string ProjectPath, PluginRepoIndex Index);

public interface ICliConsole
{
    TextReader In { get; }
    TextWriter Out { get; }
    TextWriter Error { get; }
    bool IsInputRedirected { get; }
    void WriteStatus(string message);
}
```

Map `WorkflowErrorKind` to the exact exit codes above.

- [ ] **Step 5: Implement project resolution and a project lease**

`AuthorProjectContext.ResolveAsync(string? explicitPath, string currentDirectory, CancellationToken ct)` returns `ResolvedAuthorProject` and must apply explicit path, current directory, then saved project ordering. `AcquireWriteLeaseAsync(string projectPath, CancellationToken ct)` returns the `FileStream` from a `.amm-author.lock` file under the project folder through `CrossProcessFileLock.AcquireAsync`. Read-only commands and every `--dry-run` path do not acquire the write lease or create a lock file.

- [ ] **Step 6: Implement JSON payload and console primitives**

`JsonPayloadService.ReadAsync<T>(string source, TextReader stdin, CancellationToken ct)` accepts a UTF-8 file or `-` for standard input, uses camelCase JSON options, and rejects null documents. `SecretReader.ReadAsync(ICliConsole console, CancellationToken ct)` reads one key at a time, masks nothing, echoes nothing, supports backspace, ends on Enter, and throws `OperationCanceledException` on Ctrl+C.

- [ ] **Step 7: Create the console host**

Use this CLI project core:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup>
    <ProjectReference Include="..\AccessibilityModManager.Authoring\AccessibilityModManager.Authoring.csproj" AdditionalProperties="RegistryAdmin=$(RegistryAdmin)" />
    <ProjectReference Include="..\AccessibilityModManager.Core\AccessibilityModManager.Core.csproj" />
    <ProjectReference Include="..\AccessibilityModManager.Infrastructure\AccessibilityModManager.Infrastructure.csproj" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="System.CommandLine" Version="2.0.10" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.3" />
    <PackageReference Include="Serilog" Version="4.3.1" />
    <PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
  </ItemGroup>
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>amm-author</AssemblyName>
    <Version>0.28.0</Version>
  </PropertyGroup>
  <PropertyGroup Condition="'$(RegistryAdmin)' == 'true'">
    <DefineConstants>$(DefineConstants);REGISTRY_ADMIN</DefineConstants>
  </PropertyGroup>
</Project>
```

Register `--version`, `--json`, `--quiet`, `--project`, `--dry-run`, and `--yes` at the root. Catch `OperationCanceledException` and return 130; map typed workflow errors without stack traces unless `--verbose` is present.

- [ ] **Step 8: Run focused and full tests**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~AuthorCli"
dotnet test AccessibilityModManager.slnx
dotnet run --project src/AccessibilityModManager.AuthorCli -- --version
```

Expected: focused and full tests pass; version output is `0.28.0`.

- [ ] **Step 9: Commit the CLI foundation**

```powershell
git add AccessibilityModManager.slnx src tests
git commit -m "feat: add Author CLI foundation"
```

---

### Task 3: Implement Project, Author, Game, Dependency, and Script Commands

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/CatalogWorkflow.cs`
- Create: `src/AccessibilityModManager.Authoring/Workflows/DependencyPresetCatalog.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/ProjectCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/AuthorCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/GameCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/DependencyCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/ScriptCommands.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/CatalogWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/CatalogCommandTests.cs`

**Interfaces:**
- Produces: `CatalogWorkflow.CreateProject`, `SetAuthor`, `AddGame`, `UpdateGame`, `RemoveGame`, `UpsertDependency`, `RemoveDependency`, `SetLifecycleScript`, and `ClearLifecycleScript`.
- Consumes: `PluginRepoIndex`, `GameDefinition`, `Dependency`, `LifecycleScript`, `IndexFileService`, `JsonPayloadService`, and `AuthorProjectContext`.

- [ ] **Step 1: Write lossless catalog-mutation tests**

Create complete model fixtures containing tags, languages, probe rules, registry probes, ASCII path shims, dependency checks, fixes, auto-install actions, version discovery, and all lifecycle script fields. Assert round-trip preservation and targeted mutation:

```csharp
var changed = workflow.UpsertDependency(index, "ffviinew", replacement);
Assert.Equal(replacement, changed.Games.Single(g => g.GameId == "ffviinew").Dependencies.Single(d => d.Id == replacement.Id));
Assert.Equal(original.ReleasesByGameId, changed.ReleasesByGameId);
```

Also assert duplicate game ids and duplicate dependency ids are rejected case-insensitively.

- [ ] **Step 2: Write command tests for human and JSON input**

Exercise `project init`, `project status`, `author set --input`, `game add --input`, `game update --input`, `game remove`, `dependency set --input`, `dependency remove`, `script set --input`, and `script clear`. Assert `--dry-run` leaves `index.json` byte-identical.

- [ ] **Step 3: Run focused tests and verify failure**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~CatalogWorkflowTests|FullyQualifiedName~CatalogCommandTests"
```

Expected: failure because catalog workflows and commands are absent.

- [ ] **Step 4: Implement immutable catalog mutations**

Each method returns a new `PluginRepoIndex` and preserves unrelated data. Define exact signatures:

```csharp
public PluginRepoIndex SetAuthor(PluginRepoIndex index, PluginAuthorInfo? author);
public PluginRepoIndex CreateProject(string pluginId);
public PluginRepoIndex AddGame(PluginRepoIndex index, GameDefinition game);
public PluginRepoIndex UpdateGame(PluginRepoIndex index, string currentGameId, GameDefinition replacement);
public PluginRepoIndex RemoveGame(PluginRepoIndex index, string gameId);
public PluginRepoIndex UpsertDependency(PluginRepoIndex index, string gameId, Dependency dependency);
public PluginRepoIndex RemoveDependency(PluginRepoIndex index, string gameId, string dependencyId);
public PluginRepoIndex SetLifecycleScript(PluginRepoIndex index, string gameId, LifecycleSlot slot, LifecycleScript script);
public PluginRepoIndex ClearLifecycleScript(PluginRepoIndex index, string gameId, LifecycleSlot slot);

public enum LifecycleSlot { PreInstall, PostInstall, PostUninstall }
```

On a game-id rename, update the `Games` entry and `ReleasesByGameId` key, but refuse releases whose embedded `GameId` would no longer match unless the caller passes `--rewrite-release-game-id` and confirms the preview.

- [ ] **Step 5: Register project and catalog commands**

Use `--input <file-or-dash>` for complete camelCase models so every current and future field remains expressible. Add `project init`, `recent`, `open`, `clone`, `pull`, `repos`, and `status`; add `author show` and `set`; add complete game, dependency, and script CRUD. Move the current dependency preset definitions from `ViewModels/DependencyPresets.cs` into `DependencyPresetCatalog`, make the WPF view model consume that catalog, and add `dependency presets` plus `dependency apply-preset`. Add concise flags for common game fields: `--id`, `--display-name`, `--mod-name`, `--description`, `--steam-app-id`, `--exe-name`, repeated `--tag`, and repeated `--language`. When both an input document and field flags are supplied, reject the command with exit code 2.

- [ ] **Step 6: Validate before every durable save**

Serialize the candidate index, run `PluginIndexValidation.Validate(candidate.PluginId, json)`, and refuse any `PublishBlockers`. There is no CLI flag that saves a candidate rejected by the shared validator.

- [ ] **Step 7: Run tests and command smoke checks**

```powershell
dotnet test AccessibilityModManager.slnx
dotnet run --project src/AccessibilityModManager.AuthorCli -- project --help
dotnet run --project src/AccessibilityModManager.AuthorCli -- game --help
dotnet run --project src/AccessibilityModManager.AuthorCli -- dependency --help
dotnet run --project src/AccessibilityModManager.AuthorCli -- script --help
```

Expected: all tests pass and each help command lists its complete subcommands.

- [ ] **Step 8: Commit catalog authoring**

```powershell
git add src tests
git commit -m "feat: add catalog authoring commands"
```

---

### Task 4: Implement Wrapped Package Build and Validation Commands

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/PackageWorkflow.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/PackageCommands.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/PackageWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/PackageCommandTests.cs`

**Interfaces:**
- Produces: `PackageBuildRequest`, `PackageInspection`, `PackageWorkflow.BuildAsync`, and `PackageWorkflow.ValidateAsync`.
- Consumes: `ManifestBuilderService`, `PluginPackageValidation`, `Sha256HashService`, catalog dependencies, and lifecycle-script source mappings.

- [ ] **Step 1: Write package behavior tests**

Cover a file-only mod, folder content, an external lifecycle script, a script-only mod, mismatched game/plugin/version identity, a missing script, unsafe script paths, and cancellation. Assert validation reads the staged stream and returns its exact SHA256:

```csharp
var result = await workflow.BuildAsync(request, CancellationToken.None);
Assert.True(result.Validation.IsValid);
Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.ZipPath))), result.Sha256);
```

- [ ] **Step 2: Write package command tests**

Cover `package build`, `package validate`, and `package hash`. Verify `package build --dry-run` validates source and output paths without creating a ZIP.

- [ ] **Step 3: Run focused tests and verify failure**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~PackageWorkflowTests|FullyQualifiedName~PackageCommandTests"
```

Expected: failure because package workflows and commands are absent.

- [ ] **Step 4: Implement package workflow**

Define:

```csharp
public sealed record PackageBuildRequest(
    string SourceFolder,
    string OutputZipPath,
    string PluginId,
    string GameId,
    string Version,
    IReadOnlyList<Dependency> Dependencies,
    LifecycleScriptInputs Scripts);

public sealed record PackageInspection(
    string ZipPath,
    string Sha256,
    int FileCount,
    long TotalBytes,
    PackageValidationReport Validation);
```

Build through `ManifestBuilderService`, reopen the finished ZIP read-only, validate through `PluginPackageValidation`, calculate the digest from that finished file, and delete the output if validation fails.

- [ ] **Step 5: Register package commands**

`package build` accepts `--source`, `--game`, `--version`, optional `--output`, and resolves plugin id, dependencies, and scripts from the project. `package validate` requires `--zip`, `--plugin`, `--game`, and `--version`. `package hash` outputs only the lowercase digest in normal mode and a named property in JSON mode.

- [ ] **Step 6: Run all tests and inspect a disposable ZIP**

```powershell
dotnet test AccessibilityModManager.slnx
dotnet run --project src/AccessibilityModManager.AuthorCli -- package build --project <disposable-project> --source <disposable-source> --game sample --version 1.0.0
```

Expected: the package contains root `manifest.json` and content under `files/`; validation succeeds.

- [ ] **Step 7: Commit package authoring**

```powershell
git add src tests
git commit -m "feat: add package authoring commands"
```

---

### Task 5: Implement GitHub Release and Asset Publication

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/ReleaseWorkflow.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/GitHubCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/ReleaseCommands.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/ReleaseWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/ReleaseCommandTests.cs`

**Interfaces:**
- Produces: `ReleasePublishRequest`, `ReleasePublishPreview`, `ReleasePublishResult`, `ReleaseWorkflow.PrepareAsync`, and `ReleaseWorkflow.PublishAsync`.
- Consumes: `GitHubService`, `PluginPackageValidation`, `Sha256HashService`, `CatalogWorkflow`, `AuthorConfigService`, and a read-locked staged package.

- [ ] **Step 1: Write staged-byte and partial-result tests**

Use a fake GitHub service boundary to prove the bytes hashed are the bytes uploaded, an existing tag edits notes rather than creating a second release, an existing asset is replaced only after confirmation, private repositories are refused, and upload success plus catalog-save failure returns completed phase `githubAssetUploaded` with exit category Conflict.

- [ ] **Step 2: Write release command tests**

Cover `release list`, `release show`, `release add --input`, `release edit --input`, `release remove`, `release upload`, and `release publish`. Verify release identity is `(version, channel)` and edits remove the old identity before inserting the new one.

- [ ] **Step 3: Run focused tests and verify failure**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~ReleaseWorkflowTests|FullyQualifiedName~ReleaseCommandTests"
```

Expected: failure because release workflows and commands are absent.

- [ ] **Step 4: Extract staging from the WPF release view model**

Move the current `StagedPackage` behavior into Authoring as an internal disposable type. Preserve leaf-name validation, private temporary directory creation, write exclusion, digest calculation, and cleanup byte-for-byte. Replace the WPF view model's private implementation with `ReleaseWorkflow.PrepareAsync`.

- [ ] **Step 5: Implement GitHub release workflow**

Define:

```csharp
public sealed record ReleasePublishRequest(
    string ProjectPath,
    string PluginId,
    string GameId,
    string Version,
    string Channel,
    string SourceRepo,
    string LocalZipPath,
    string? AssetFileName,
    string? Notes,
    string? ChangelogUrl,
    PatreonGate? Patreon);

public sealed record ReleasePublishPreview(
    string Repository,
    string Tag,
    string AssetFileName,
    string Sha256,
    bool CreatesRelease,
    bool ReplacesAsset);

public sealed record ReleasePublishResult(
    ModRelease Release,
    string AssetUrl,
    string Sha256,
    IReadOnlyList<string> CompletedPhases);

public sealed class PreparedRelease : IAsyncDisposable
{
    public ReleasePublishPreview Preview { get; }
    public string StagedPath { get; }
    public string Sha256 { get; }
    public ValueTask DisposeAsync();
}

public Task<WorkflowResult<PreparedRelease>> PrepareAsync(
    ReleasePublishRequest request, CancellationToken ct);

public Task<WorkflowResult<ReleasePublishResult>> PublishAsync(
    PreparedRelease prepared, ReleasePublishRequest request, bool confirmed, CancellationToken ct);
```

Preparation validates all metadata and package identity before upload. Publication uses the existing `GitHubService` methods and returns the public asset URL plus exact staged SHA256. It does not save or publish `index.json`; Task 7 composes that transaction.

- [ ] **Step 6: Register GitHub and release commands**

Add `github status`, `github repos`, and `github releases --repo`. Add complete release CRUD plus `release upload`. `release publish` is registered now but reports a clear unavailable-phase result until Task 7 supplies index publication; its parser contract is pinned by tests.

- [ ] **Step 7: Run regression and disposable fake-publish tests**

```powershell
dotnet test AccessibilityModManager.slnx
```

Expected: every test passes and no real GitHub repository was modified.

- [ ] **Step 8: Commit release publication**

```powershell
git add src tests
git commit -m "feat: add GitHub release authoring"
```

---

### Task 6: Implement Index Reconciliation, Validation, and Publication

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/IndexWorkflow.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/IndexCommands.cs`
- Modify: `src/AccessibilityModManager.AuthorCli/Commands/ReleaseCommands.cs`
- Modify: `src/AccessibilityModManager.AuthorTool/ViewModels/IndexEditorViewModel.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/IndexWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/IndexCommandTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/CompleteReleasePublishTests.cs`

**Interfaces:**
- Produces: `IndexPublishRequest`, `IndexPublishPreview`, `IndexPublishResult`, `IndexWorkflow.Validate`, `ReconcileAsync`, `SaveAsync`, `PublishAsync`, `InspectLockAsync`, and `BreakLockAsync`.
- Consumes: `ProjectReconciler`, `IndexPublishCoordinator`, `GitHubIndexPublisher`, `UnsignedPublishGate`, `RegistryMembershipChecker`, `ServerUploadService`, and `AuthorConfigService` publishing records.

- [ ] **Step 1: Write reconciliation and publish tests**

Cover local-equals-last-published, live advanced elsewhere, local unpublished edits, unreadable live state, registered URL mismatch, private GitHub repository, missing destination, signed catalog on an unsigned path, GitHub publish, server publish, lock contention, compare-before-break, and read-back mismatch.

- [ ] **Step 2: Write complete release transaction tests**

Assert this exact phase order:

```csharp
Assert.Equal(new[] {
    "projectLocked", "catalogReconciled", "packageValidated", "assetUploaded",
    "releaseRecorded", "indexValidated", "indexSaved", "indexPublished", "liveVerified"
}, result.CompletedPhases);
```

Simulate failure after each phase and verify the result lists only phases that actually completed.

- [ ] **Step 3: Run focused tests and verify failure**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~IndexWorkflowTests|FullyQualifiedName~IndexCommandTests|FullyQualifiedName~CompleteReleasePublishTests"
```

Expected: failure because index workflow and composed release publication are absent.

- [ ] **Step 4: Extract index orchestration into the shared workflow**

Move non-UI logic from `IndexEditorViewModel` into `IndexWorkflow` while leaving UI messages and prompts in the view model. The workflow must return a preview before mutation and accept a separate confirmed execution call. Preserve the existing second authorization check immediately before GitHub push.

Use these contracts:

```csharp
public sealed record IndexPublishRequest(
    string ProjectPath,
    PluginRepoIndex Candidate,
    PublishDestination Destination,
    string CommitMessage,
    bool DryRun);

public sealed record IndexPublishPreview(
    string PluginId,
    PublishDestination Destination,
    string DestinationDescription,
    string CommitMessage,
    IReadOnlyList<string> CatalogChanges);

public sealed record IndexPublishResult(
    string PluginId,
    string PublishedSha256,
    string DestinationDescription,
    IReadOnlyList<string> CompletedPhases);

public IndexValidationReport Validate(PluginRepoIndex candidate);
public Task<WorkflowResult<PluginRepoIndex>> ReconcileAsync(string projectPath, CancellationToken ct);
public Task<WorkflowResult<string>> SaveAsync(string projectPath, PluginRepoIndex candidate, bool dryRun, CancellationToken ct);
public Task<WorkflowResult<IndexPublishPreview>> PreviewPublishAsync(IndexPublishRequest request, CancellationToken ct);
public Task<WorkflowResult<IndexPublishResult>> PublishAsync(IndexPublishRequest request, bool confirmed, CancellationToken ct);
public Task<WorkflowResult<ServerUploadService.RemoteLock>> InspectLockAsync(string pluginId, CancellationToken ct);
public Task<WorkflowResult<bool>> BreakLockAsync(string pluginId, string expectedFingerprint, bool confirmed, CancellationToken ct);
```

- [ ] **Step 5: Implement index commands**

Add `index show`, `index validate`, `index reconcile`, `index save`, `index destination get`, `index destination set`, `index membership`, `index publish`, `index lock show`, and `index lock break`. `index lock break` requires the displayed lock fingerprint and confirmation; if the lock changes, it refuses.

- [ ] **Step 6: Complete `release publish` composition**

Acquire the project lease, reconcile, prepare and validate the package, publish the asset, add the release in memory, validate and durably save the index, publish the selected destination, verify the live index, and record the published digest. Emit a phase line after each completed phase.

- [ ] **Step 7: Run all tests and local Git integration**

```powershell
dotnet test AccessibilityModManager.slnx
```

Use a disposable working repository with a local bare remote to verify commit, push, branch creation, CRLF normalization behavior, and live blob read-back without network writes.

- [ ] **Step 8: Commit index publication**

```powershell
git add src tests
git commit -m "feat: add safe catalog publication"
```

---

### Task 7: Implement Patreon and SFTP Server Parity

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/PatreonWorkflow.cs`
- Create: `src/AccessibilityModManager.Authoring/Workflows/ServerWorkflow.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/PatreonCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/ServerCommands.cs`
- Modify: `src/AccessibilityModManager.Authoring/Workflows/ReleaseWorkflow.cs`
- Modify: `src/AccessibilityModManager.Authoring/Workflows/IndexWorkflow.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/PatreonWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/ServerWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/PatreonServerCommandTests.cs`

**Interfaces:**
- Produces: Patreon session, tier, post, and attachment results; server configuration, self-test, release upload, gate update, and lock results.
- Consumes: `PatreonAuthorService`, `ServerUploadService`, `ServerSelfTest`, and DPAPI-aware `AuthorConfigService`.

- [ ] **Step 1: Write Patreon command and workflow tests**

Cover signed-out status, sign-in cancellation, sign-out, tier refresh, invalid post URL, numeric post extraction, attachment selection, no selected tiers, no campaign id, and both Patreon-post and author-server delivery modes.

- [ ] **Step 2: Write server command and workflow tests**

Cover configuration validation, missing key, host-key mismatch, connection test steps, public upload, gated upload, refusing different bytes at an existing version, gate-only update, gate removal after catalog publication, publish-lock inspection, and changed-lock refusal.

- [ ] **Step 3: Run focused tests and verify failure**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~PatreonWorkflowTests|FullyQualifiedName~ServerWorkflowTests|FullyQualifiedName~PatreonServerCommandTests"
```

Expected: failure because the workflows and commands are absent.

- [ ] **Step 4: Implement Patreon workflow and commands**

Add `patreon status`, `login`, `logout`, `tiers`, and `post validate`. Browser-based OAuth remains the existing service behavior. `post validate` outputs every attachment with its file name and stable selection id.

Use these contracts:

```csharp
public sealed record PatreonSessionStatus(bool IsSignedIn, string? MemberName, string? CampaignId);
public sealed record PatreonTierInfo(string TierId, string DisplayName);
public sealed record PatreonAttachmentInfo(string SelectionId, string FileName, string? DownloadUrl);
public sealed record PatreonPostInspection(string PostId, IReadOnlyList<PatreonAttachmentInfo> Attachments);

public Task<WorkflowResult<PatreonSessionStatus>> GetStatusAsync(CancellationToken ct);
public Task<WorkflowResult<PatreonSessionStatus>> SignInAsync(CancellationToken ct);
public Task<WorkflowResult<bool>> SignOutAsync(CancellationToken ct);
public Task<WorkflowResult<IReadOnlyList<PatreonTierInfo>>> GetTiersAsync(CancellationToken ct);
public Task<WorkflowResult<PatreonPostInspection>> InspectPostAsync(string postUrl, CancellationToken ct);
```

- [ ] **Step 5: Implement server workflow and secret-safe configuration**

Add `server status`, `configure`, `clear`, `test`, `self-test`, `release inspect`, `release upload`, `gate set`, `gate remove`, `lock show`, and `lock break`. Read the SSH key passphrase only through concealed input or `--passphrase-stdin`, then persist it through the existing DPAPI-aware configuration service.

Wrap the existing sealed service behind a testable adapter without changing it:

```csharp
public sealed record ServerConfigurationInput(ServerUploadConfig Config, string KeyPassphrase);
public sealed record ServerConnectionReport(bool Connected, IReadOnlyList<ServerCheckStep> Steps);
public sealed record ServerReleaseRequest(
    string GameId, string Version, string AssetFileName, string LocalZipPath, PatreonGate? Gate);

public interface IServerAuthorTransport
{
    Task<ServerConnectionReport> TestAsync(ServerUploadConfig config, CancellationToken ct);
    Task<ServerUploadService.ReleasePublishOutcome> PublishReleaseAsync(
        ServerUploadConfig config, ServerReleaseRequest request, CancellationToken ct);
    Task PublishGateAsync(ServerUploadConfig config, string gameId, string version, PatreonGate gate, CancellationToken ct);
    Task RemoveGateAsync(ServerUploadConfig config, string gameId, string version, CancellationToken ct);
}
```

- [ ] **Step 6: Integrate gated release sequencing**

For a gated release, upload the package and fresh gate first, publish the index second, then apply a changed or removed gate only after the live catalog matches. Preserve the WPF flow's public-URL reachability check when removing a gate.

- [ ] **Step 7: Run all tests**

```powershell
dotnet test AccessibilityModManager.slnx
```

Expected: all tests pass and test configuration stays under disposable override directories.

- [ ] **Step 8: Commit Patreon and server parity**

```powershell
git add src tests
git commit -m "feat: add Patreon and server authoring"
```

---

### Task 8: Implement Signing and Registry-Admin Parity

**Files:**
- Create: `src/AccessibilityModManager.Authoring/Workflows/AuthoringBuildFlags.cs`
- Create: `src/AccessibilityModManager.Authoring/Workflows/SigningWorkflow.cs`
- Create: `src/AccessibilityModManager.Authoring/Workflows/RegistryAdminWorkflow.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/SigningCommands.cs`
- Create: `src/AccessibilityModManager.AuthorCli/Commands/RegistryCommands.cs`
- Modify: `src/AccessibilityModManager.AuthorTool/BuildFlags.cs`
- Modify: `src/AccessibilityModManager.AuthorTool/App.xaml.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/SigningWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/RegistryAdminWorkflowTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/SigningRegistryCommandTests.cs`

**Interfaces:**
- Produces: signing-key lifecycle operations, claim previews and signatures, publisher-head operations, registry validation and signature operations, and build-gated registry commands.
- Consumes: `ClaimSigningKeyStore`, `PublisherHeadStore`, `IndexProofService`, `ClaimSetBuilder`, `ClaimSigner`, `ServerUploadService`, `GitService`, and `GitHubService`.

- [ ] **Step 1: Write signing lifecycle tests**

Cover create, status, export, import, wrong passphrase, passphrase change, public-key mismatch, imported recordless key refusal, pending publish recovery, confirmation, and head reconciliation. Assert secrets never appear in workflow messages.

- [ ] **Step 2: Write standard/admin build-gate tests**

In a standard build, `registry status` must return exit code 4 with the explanation that an admin build is required. In an admin build, parser and workflow tests cover open/clone, refresh, JSON validation, signature creation, registry-pair upload, read-back, commit, and push.

- [ ] **Step 3: Run focused tests and verify failure**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~SigningWorkflowTests|FullyQualifiedName~RegistryAdminWorkflowTests|FullyQualifiedName~SigningRegistryCommandTests"
```

Expected: failure because signing and registry workflows are absent.

- [ ] **Step 4: Implement shared build flag**

Define:

```csharp
public static class AuthoringBuildFlags
{
#if REGISTRY_ADMIN
    public const bool IsRegistryAdmin = true;
#else
    public const bool IsRegistryAdmin = false;
#endif
}
```

Make the WPF `BuildFlags.IsRegistryAdmin` delegate to this value. Pass `RegistryAdmin=$(RegistryAdmin)` to both Authoring and CLI projects in build commands.

- [ ] **Step 5: Implement signing commands**

Add `signing status`, `create`, `export`, `import`, `change-passphrase`, `claims preview`, `claims sign`, `head status`, `head confirm`, `head commit-pending`, and `head resume`. Secret input follows the global rule, and commands call existing stores and proof services without alternate validation.

Use these workflow contracts:

```csharp
public sealed record SigningKeyStatus(
    string PluginId, string KeyId, string PublicKeyFingerprint, bool ImportedFromBackup, bool HasPublisherHead);
public sealed record ClaimPublishPreview(
    string PluginId, string KeyId, long PublishNumber, IReadOnlyList<string> Changes, string DeletionsToken);

public WorkflowResult<SigningKeyStatus> GetStatus(string pluginId);
public WorkflowResult<SigningKeyStatus> Create(string pluginId, string passphrase);
public WorkflowResult<string> Export(string pluginId, string destination, string exportPassphrase);
public WorkflowResult<SigningKeyStatus> Import(string source, string importPassphrase);
public WorkflowResult<SigningKeyStatus> ChangePassphrase(string pluginId, string currentPassphrase, string newPassphrase);
public Task<WorkflowResult<ClaimPublishPreview>> PreviewClaimsAsync(string projectPath, CancellationToken ct);
public Task<WorkflowResult<IndexProofService.PreparedPublish>> SignClaimsAsync(
    string projectPath, string deletionsToken, bool confirmed, CancellationToken ct);
```

- [ ] **Step 6: Implement registry-admin commands**

Add `registry status`, `open`, `refresh`, `json show`, `json validate`, `json save`, `sign`, `publish`, `commit`, and `push`. Standard builds register the group so help remains discoverable but every handler refuses before reading private configuration.

Use these workflow contracts:

```csharp
public sealed record RegistryDocumentResult(string Path, string Sha256, bool SignaturePresent);
public sealed record RegistryPublishResult(
    string Destination, string JsonSha256, string SignatureSha256, IReadOnlyList<string> CompletedPhases);

public WorkflowResult<RegistryDocumentResult> Validate(string registryJsonPath);
public WorkflowResult<RegistryDocumentResult> Sign(
    string registryJsonPath, string privateKeyPath, string passphrase, bool confirmed);
public Task<WorkflowResult<RegistryPublishResult>> PublishAsync(
    string registryRepoPath, bool confirmed, CancellationToken ct);
public Task<WorkflowResult<ProcessResult>> CommitAsync(
    string registryRepoPath, string message, CancellationToken ct);
public Task<WorkflowResult<ProcessResult>> PushAsync(string registryRepoPath, CancellationToken ct);
```

- [ ] **Step 7: Run both build variants and all tests**

```powershell
dotnet build AccessibilityModManager.slnx
dotnet build src/AccessibilityModManager.AuthorCli/AccessibilityModManager.AuthorCli.csproj -p:RegistryAdmin=true
dotnet test AccessibilityModManager.slnx
dotnet test AccessibilityModManager.slnx -p:RegistryAdmin=true
```

Expected: both builds succeed and all tests pass.

- [ ] **Step 8: Commit signing and registry parity**

```powershell
git add src tests
git commit -m "feat: add signing and registry CLI parity"
```

---

### Task 9: Complete Command Discovery, Help, JSON, and WPF Parity

**Files:**
- Create: `src/AccessibilityModManager.AuthorCli/Commands/CommandCatalog.cs`
- Modify: all files under `src/AccessibilityModManager.AuthorCli/Commands/`
- Modify: `src/AccessibilityModManager.AuthorTool/App.xaml.cs`
- Modify: affected AuthorTool view models under `src/AccessibilityModManager.AuthorTool/ViewModels/`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/CommandCoverageTests.cs`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/AccessibilityOutputTests.cs`
- Test: `tests/AccessibilityModManager.Tests/Authoring/GuiCliParityTests.cs`

**Interfaces:**
- Produces: a complete searchable command tree, stable JSON envelopes, and shared GUI/CLI workflow decisions.
- Consumes: every workflow from Tasks 2 through 8.

- [ ] **Step 1: Write command-coverage tests from a required inventory**

Pin this top-level inventory:

```csharp
var required = new[] {
    "project", "author", "game", "dependency", "script", "package", "release",
    "index", "github", "patreon", "server", "signing", "registry"
};
Assert.Equal(required, CommandCatalog.TopLevelNames);
```

For every group, assert all subcommands listed in the design are present and have a nonempty description and example.

- [ ] **Step 2: Write accessibility-output tests**

Reject carriage-return progress rewriting, ANSI escape sequences by default, messages whose only content is punctuation, and JSON mode with multiple standard-output documents. Verify `--quiet` still emits warnings and failures.

- [ ] **Step 3: Write GUI/CLI parity tests**

Feed the same fixture and confirmed preview into the shared workflow from a WPF-facing adapter and a CLI-facing adapter. Assert equal candidate index bytes, package validation reports, release metadata, destination decisions, and completed phases.

- [ ] **Step 4: Run focused tests and verify failures**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter "FullyQualifiedName~CommandCoverageTests|FullyQualifiedName~AccessibilityOutputTests|FullyQualifiedName~GuiCliParityTests"
```

Expected: failures identify unregistered commands, incomplete help, or remaining GUI-only orchestration.

- [ ] **Step 5: Finish command registration and examples**

Centralize registration in `CommandCatalog` and make each group expose `Create(IServiceProvider services)`. Add examples to help text using Windows quoting and absolute-path examples that do not contain this user's private paths.

- [ ] **Step 6: Finish WPF workflow adoption**

Replace remaining duplicated non-UI decisions in WPF view models with shared workflow calls. Keep existing observable properties, command names, dialog text, focus behavior, and screen-reader announcements intact.

- [ ] **Step 7: Run all tests and help snapshot**

```powershell
dotnet test AccessibilityModManager.slnx
dotnet run --project src/AccessibilityModManager.AuthorCli -- --help
dotnet run --project src/AccessibilityModManager.AuthorCli -- release publish --help
```

Expected: all tests pass; help is complete and readable as plain text.

- [ ] **Step 8: Commit parity and help**

```powershell
git add src tests
git commit -m "feat: complete AuthorTool CLI parity"
```

---

### Task 10: Build, Document, Install, and Verify the Local CLI

**Files:**
- Create: `installer/build-author-cli.ps1`
- Create: `src/AccessibilityModManager.AuthorCli/Properties/PublishProfiles/win-x64.pubxml`
- Modify: `README.md`
- Test: `tests/AccessibilityModManager.Tests/AuthorCli/PublishedCliSmokeTests.cs`

**Interfaces:**
- Produces: local `amm-author.exe`, optional `amm-author-admin.exe`, build hashes, PATH installation, and documented commands.
- Consumes: completed CLI and existing .NET 10 SDK.

- [ ] **Step 1: Write published-binary smoke test**

The test launches a supplied published executable with `--version`, `--help`, and `project status --project <fixture> --json`, then asserts exit code 0, version 0.28.0, parseable JSON, and no WPF process or window requirement.

- [ ] **Step 2: Run the smoke test and verify it skips or fails without a published path**

```powershell
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter FullyQualifiedName~PublishedCliSmokeTests
```

Expected: an explicit skipped result when `AMM_AUTHOR_CLI_EXE` is absent; the test never guesses a binary path.

- [ ] **Step 3: Add the build script**

The script accepts `-Configuration`, `-Version`, `-SelfContained`, and `-Admin`, reads version 0.28.0 from the CLI project when omitted, publishes `win-x64` single-file output, writes standard and admin builds to separate local folders, and prints a lowercase SHA256. It does not create or upload a GitHub release.

- [ ] **Step 4: Add documentation**

Document installation, project resolution, every command group, human versus JSON output, secret input, exit codes, dry-run and confirmation behavior, standard versus registry-admin builds, local-only license restriction, and five complete examples: inspect project, build package, validate package, publish a release, and dry-run an index publish.

- [ ] **Step 5: Build both local variants**

```powershell
powershell -ExecutionPolicy Bypass -File installer/build-author-cli.ps1 -SelfContained
powershell -ExecutionPolicy Bypass -File installer/build-author-cli.ps1 -SelfContained -Admin
```

Expected: `amm-author.exe` and `amm-author-admin.exe` are produced in separate local dist folders with hashes.

- [ ] **Step 6: Run complete verification**

```powershell
dotnet test AccessibilityModManager.slnx
$env:AMM_AUTHOR_CLI_EXE = (Resolve-Path 'dist-author-cli/amm-author.exe')
dotnet test tests/AccessibilityModManager.Tests/AccessibilityModManager.Tests.csproj --filter FullyQualifiedName~PublishedCliSmokeTests
```

Expected: all solution and published-binary tests pass.

- [ ] **Step 7: Install locally and update user PATH**

Copy the two executables and their `.sha256` files to `C:\Users\buu42\Tools\AccessibilityModManager`. Add that exact directory to the current-user PATH only when its case-insensitive normalized entry is absent. Do not copy source or binaries into Blind Soldier, `buu-s-mods`, or any GitHub checkout intended for publication.

- [ ] **Step 8: Verify outside the source tree**

Run from `C:\Users\buu42`:

```powershell
amm-author --version
amm-author --help
amm-author project status --project <disposable-project> --json
```

Expected: version 0.28.0, complete help, and a successful JSON status result.

- [ ] **Step 9: Confirm no external publication occurred**

```powershell
git status --short
git log --oneline --decorate -12
git remote -v
```

Expected: only local commits exist on `local/full-author-cli`; the remote remains read-only upstream and no push was performed.

- [ ] **Step 10: Commit build and documentation**

```powershell
git add installer src/AccessibilityModManager.AuthorCli/Properties README.md tests
git commit -m "build: package local Author CLI"
```

## Final Verification Checklist

- [ ] `dotnet build AccessibilityModManager.slnx` succeeds.
- [ ] `dotnet build src/AccessibilityModManager.AuthorCli/AccessibilityModManager.AuthorCli.csproj -p:RegistryAdmin=true` succeeds.
- [ ] `dotnet test AccessibilityModManager.slnx` passes without skipped behavioral tests.
- [ ] Standard CLI refuses registry administration with exit code 4 and a readable explanation.
- [ ] Admin CLI runs registry read-only status against a disposable fixture.
- [ ] Human output contains no ANSI sequences or rewritten lines.
- [ ] JSON mode writes exactly one result object to standard output.
- [ ] Secrets do not appear in process arguments, stdout, stderr, or logs.
- [ ] `--dry-run` leaves fixture files and Git history byte-identical.
- [ ] Package build and validation succeed for a disposable mod.
- [ ] Complete release publication succeeds against fake GitHub and local Git boundaries.
- [ ] Patreon and SFTP flows pass controlled integration tests without touching live accounts or servers.
- [ ] Existing WPF AuthorTool tests and behavior remain green.
- [ ] Installed `amm-author.exe` runs from outside the checkout.
- [ ] No fork, push, pull request, release, or redistribution occurred.
