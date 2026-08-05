using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Tests.Authoring;

public sealed class CatalogWorkflowTests
{
    [Fact]
    public void Complete_fixture_populates_every_writable_current_model_property()
    {
        foreach (var sample in CatalogFixture.AllCoverageSamples())
            AssertFixturePopulatesWritableProperties(sample.Key, sample.Value);
    }

    [Fact]
    public void CreateProject_creates_the_current_starter_shape()
    {
        var workflow = new CatalogWorkflow();
        var before = DateTime.UtcNow;

        var created = workflow.CreateProject("sample-plugin");

        var after = DateTime.UtcNow;
        Assert.Equal("sample-plugin", created.PluginId);
        Assert.Equal("1", created.RepoVersion);
        Assert.InRange(created.GeneratedAt, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Empty(created.Games);
        Assert.Empty(created.ReleasesByGameId);
        Assert.Null(created.Author);
        Assert.Empty(created.DependencyPresets);
    }

    [Fact]
    public void SetAuthor_replaces_or_clears_only_the_author_block()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.AlternateAuthor();

        var changed = workflow.SetAuthor(original, replacement);
        var cleared = workflow.SetAuthor(original, null);

        AssertMutation(original, snapshot, changed, CatalogFixture.WithAuthor(snapshot, replacement));
        CatalogFixture.AssertJsonEquivalent(CatalogFixture.WithAuthor(snapshot, null), cleared);
    }

    [Fact]
    public void AddGame_adds_the_game_and_creates_an_empty_release_bucket()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var added = CatalogFixture.CompleteGame(CatalogFixture.AddedGameId, "Knights of the Old Republic");

        var changed = workflow.AddGame(original, added);

        AssertMutation(original, snapshot, changed, CatalogFixture.WithGameAdded(snapshot, added));
    }

    [Fact]
    public void AddGame_rejects_case_insensitive_duplicates()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var duplicate = CatalogFixture.CompleteGame(CatalogFixture.PrimaryGameId.ToUpperInvariant(), "Duplicate");

        AssertRejectsWithoutMutation(
            original,
            snapshot,
            () => workflow.AddGame(original, duplicate),
            CatalogFixture.PrimaryGameId);
    }

    [Fact]
    public void UpdateGame_replaces_the_matching_game_without_touching_other_state()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.CompleteGame(CatalogFixture.SecondaryGameId, "Resident Evil 4 HD");

        var changed = workflow.UpdateGame(original, CatalogFixture.SecondaryGameId, replacement);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithGameUpdated(snapshot, CatalogFixture.SecondaryGameId, replacement));
    }

    [Fact]
    public void UpdateGame_renames_the_game_and_rekeys_empty_release_buckets()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.CompleteGame(CatalogFixture.RenamedSecondaryGameId, "Resident Evil 4 Remastered");

        var changed = workflow.UpdateGame(original, CatalogFixture.SecondaryGameId, replacement);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithGameUpdated(snapshot, CatalogFixture.SecondaryGameId, replacement));
    }

    [Fact]
    public void UpdateGame_rejects_case_insensitive_target_collisions()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.CompleteGame(CatalogFixture.PrimaryGameId.ToUpperInvariant(), "Collision");

        AssertRejectsWithoutMutation(
            original,
            snapshot,
            () => workflow.UpdateGame(original, CatalogFixture.SecondaryGameId, replacement),
            CatalogFixture.PrimaryGameId);
    }

    [Fact]
    public void UpdateGame_refuses_published_release_mismatches_without_explicit_rewrite()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.CompleteGame(CatalogFixture.RenamedPrimaryGameId, "Final Fantasy VII Reborn");

        AssertRejectsWithoutMutation(
            original,
            snapshot,
            () => workflow.UpdateGame(original, CatalogFixture.PrimaryGameId, replacement),
            "release");
    }

    [Fact]
    public void UpdateGame_can_explicitly_rewrite_embedded_release_game_ids()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.CompleteGame(CatalogFixture.RenamedPrimaryGameId, "Final Fantasy VII Reborn");

        var changed = workflow.UpdateGame(original, CatalogFixture.PrimaryGameId, replacement, true);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithGameUpdated(snapshot, CatalogFixture.PrimaryGameId, replacement, rewriteReleaseGameIds: true));
    }

    [Fact]
    public void RemoveGame_removes_only_that_game_and_its_release_bucket()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;

        var changed = workflow.RemoveGame(original, CatalogFixture.PrimaryGameId);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithGameRemoved(snapshot, CatalogFixture.PrimaryGameId));
    }

    [Fact]
    public void UpsertDependency_replaces_case_insensitive_matches_losslessly()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var replacement = CatalogFixture.CompleteDependency(
            "RUNTIME-INSTALLER",
            CatalogFixture.CompleteRunInstallerAutoInstall(),
            CatalogFixture.CompleteGitHubReleaseAssetVersionDiscovery());

        var changed = workflow.UpsertDependency(original, CatalogFixture.PrimaryGameId, replacement);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithDependencyUpserted(snapshot, CatalogFixture.PrimaryGameId, replacement));
    }

    [Fact]
    public void UpsertDependency_adds_a_new_dependency_without_touching_other_data()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var added = CatalogFixture.CompleteDependency(
            "new-helper",
            CatalogFixture.CompleteCopyFileAutoInstall(),
            CatalogFixture.CompleteStaticVersionDiscovery());

        var changed = workflow.UpsertDependency(original, CatalogFixture.PrimaryGameId, added);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithDependencyUpserted(snapshot, CatalogFixture.PrimaryGameId, added));
    }

    [Fact]
    public void UpsertDependency_rejects_preexisting_case_insensitive_duplicate_ids()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var game = original.Games.Single(g => g.GameId == CatalogFixture.PrimaryGameId);
        var duplicate = CatalogFixture.CompleteDependency(
            game.Dependencies[0].Id.ToUpperInvariant(),
            CatalogFixture.CompleteExtractZipAutoInstall(),
            CatalogFixture.CompleteGitHubReleaseVersionDiscovery());
        game.Dependencies.Add(duplicate);
        var snapshot = CatalogFixture.Clone(original)!;

        AssertRejectsWithoutMutation(
            original,
            snapshot,
            () => workflow.UpsertDependency(original, CatalogFixture.PrimaryGameId, game.Dependencies[0]),
            "multiple dependencies");
    }

    [Fact]
    public void RemoveDependency_removes_only_the_named_dependency()
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;

        var changed = workflow.RemoveDependency(original, CatalogFixture.PrimaryGameId, "COPY-HELPER");

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithDependencyRemoved(snapshot, CatalogFixture.PrimaryGameId, "COPY-HELPER"));
    }

    [Theory]
    [InlineData(LifecycleSlot.PreInstall)]
    [InlineData(LifecycleSlot.PostInstall)]
    [InlineData(LifecycleSlot.PostUninstall)]
    public void SetLifecycleScript_sets_only_the_selected_slot(LifecycleSlot slot)
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;
        var script = CatalogFixture.ReplacementLifecycleScript();

        var changed = workflow.SetLifecycleScript(original, CatalogFixture.PrimaryGameId, slot, script);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithLifecycleScript(snapshot, CatalogFixture.PrimaryGameId, slot, script));
    }

    [Theory]
    [InlineData(LifecycleSlot.PreInstall)]
    [InlineData(LifecycleSlot.PostInstall)]
    [InlineData(LifecycleSlot.PostUninstall)]
    public void ClearLifecycleScript_clears_only_the_selected_slot(LifecycleSlot slot)
    {
        var workflow = new CatalogWorkflow();
        var original = CatalogFixture.CreateCompleteIndex();
        var snapshot = CatalogFixture.Clone(original)!;

        var changed = workflow.ClearLifecycleScript(original, CatalogFixture.PrimaryGameId, slot);

        AssertMutation(
            original,
            snapshot,
            changed,
            CatalogFixture.WithLifecycleScript(snapshot, CatalogFixture.PrimaryGameId, slot, null));
    }

    private static void AssertMutation(
        PluginRepoIndex original,
        PluginRepoIndex snapshot,
        PluginRepoIndex actual,
        PluginRepoIndex expected)
    {
        Assert.NotSame(original, actual);
        CatalogFixture.AssertJsonEquivalent(snapshot, original);
        CatalogFixture.AssertJsonEquivalent(expected, actual);
    }

    private static void AssertRejectsWithoutMutation(
        PluginRepoIndex original,
        PluginRepoIndex snapshot,
        Action action,
        string messageFragment)
    {
        var ex = Assert.Throws<InvalidOperationException>(action);
        Assert.Contains(messageFragment, ex.Message, StringComparison.OrdinalIgnoreCase);
        CatalogFixture.AssertJsonEquivalent(snapshot, original);
    }

    private static void AssertFixturePopulatesWritableProperties(Type type, object sample)
    {
        var baseline = Activator.CreateInstance(type)
            ?? throw new InvalidOperationException($"Couldn't create baseline instance for {type.FullName}.");

        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0))
        {
            var actual = property.GetValue(sample);
            Assert.NotNull(actual);

            if (actual is string text)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{type.Name}.{property.Name} was blank.");
                continue;
            }

            if (actual is System.Collections.IEnumerable enumerable and not JsonNode)
            {
                Assert.True(enumerable.Cast<object?>().Any(), $"{type.Name}.{property.Name} was empty.");
                continue;
            }

            var baselineValue = property.GetValue(baseline);
            Assert.NotEqual(
                CatalogFixture.CanonicalJson(baselineValue),
                CatalogFixture.CanonicalJson(actual));
        }
    }

    internal static class CatalogFixture
    {
        internal const string PluginId = "amethyst";
        internal const string PrimaryGameId = "ff7";
        internal const string SecondaryGameId = "re4";
        internal const string AddedGameId = "kotor";
        internal const string RenamedPrimaryGameId = "ff7-reborn";
        internal const string RenamedSecondaryGameId = "re4-remastered";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        internal static IReadOnlyDictionary<Type, object> AllCoverageSamples() =>
            new Dictionary<Type, object>
            {
                [typeof(PluginRepoIndex)] = CreateCompleteIndex(),
                [typeof(PluginAuthorInfo)] = CompleteAuthor(),
                [typeof(GameDefinition)] = CompleteGame(PrimaryGameId, "Final Fantasy VII"),
                [typeof(PathProbeRule)] = CompletePathProbeRule(),
                [typeof(RegistryProbe)] = CompleteRegistryProbe(),
                [typeof(AsciiPathShim)] = CompleteAsciiPathShim(),
                [typeof(Dependency)] = CompleteDependency("coverage-dependency", CompleteExtractZipAutoInstall(), CompleteGitHubReleaseVersionDiscovery()),
                [typeof(DependencyCheck)] = CompleteDependencyCheck(),
                [typeof(DependencyFix)] = CompleteDependencyFix(CompleteRunInstallerAutoInstall()),
                [typeof(ExtractZipAutoInstall)] = CompleteExtractZipAutoInstall(),
                [typeof(RunInstallerAutoInstall)] = CompleteRunInstallerAutoInstall(),
                [typeof(CopyFileAutoInstall)] = CompleteCopyFileAutoInstall(),
                [typeof(ExtractAppAutoInstall)] = CompleteExtractAppAutoInstall(),
                [typeof(GitHubReleaseVersionDiscovery)] = CompleteGitHubReleaseVersionDiscovery(),
                [typeof(GitHubReleaseAssetVersionDiscovery)] = CompleteGitHubReleaseAssetVersionDiscovery(),
                [typeof(StaticVersionDiscovery)] = CompleteStaticVersionDiscovery(),
                [typeof(LifecycleScript)] = CompleteLifecycleScript("scripts/coverage.ps1"),
                [typeof(ModRelease)] = CompleteRelease(PrimaryGameId),
                [typeof(CompatibilityInfo)] = CompleteCompatibility(),
                [typeof(PatreonGate)] = CompletePatreonGate(),
                [typeof(DependencyPreset)] = CompleteDependencyPreset()
            };

        internal static PluginRepoIndex CreateStarterIndex(string pluginId) =>
            new()
            {
                PluginId = pluginId,
                RepoVersion = "1",
                GeneratedAt = new DateTime(2026, 8, 4, 18, 30, 0, DateTimeKind.Utc),
                Games = [],
                ReleasesByGameId = new Dictionary<string, List<ModRelease>>(),
                DependencyPresets = []
            };

        internal static PluginRepoIndex CreateCompleteIndex()
        {
            var primary = CompleteGame(PrimaryGameId, "Final Fantasy VII");
            var secondary = new GameDefinition
            {
                GameId = SecondaryGameId,
                DisplayName = "Resident Evil 4",
                ExeName = "re4.exe",
                ProbeRules = [new PathProbeRule { Type = "fileExists", RelativePath = "re4.exe" }],
                Dependencies =
                [
                    new Dependency
                    {
                        Id = "bepinex",
                        Type = "framework",
                        Required = true,
                        Check = new DependencyCheck { FilePath = "winhttp.dll" },
                        Fix = new DependencyFix { DownloadUrl = "https://github.com/BepInEx/BepInEx/releases" }
                    }
                ],
                Tags = ["controller-support"],
                Languages = ["en"]
            };

            return new PluginRepoIndex
            {
                PluginId = PluginId,
                RepoVersion = "1",
                GeneratedAt = new DateTime(2026, 8, 4, 18, 30, 0, DateTimeKind.Utc),
                Games = [primary, secondary],
                ReleasesByGameId = new Dictionary<string, List<ModRelease>>
                {
                    [PrimaryGameId] = [CompleteRelease(PrimaryGameId)],
                    [SecondaryGameId] = []
                },
                Author = CompleteAuthor(),
                DependencyPresets = [CompleteDependencyPreset()]
            };
        }

        internal static PluginRepoIndex WithGeneratedAt(PluginRepoIndex index, DateTime generatedAt) =>
            new()
            {
                PluginId = index.PluginId,
                RepoVersion = index.RepoVersion,
                GeneratedAt = generatedAt,
                Games = Clone(index.Games)!,
                ReleasesByGameId = Clone(index.ReleasesByGameId)!,
                Author = Clone(index.Author),
                DependencyPresets = Clone(index.DependencyPresets)!
            };

        internal static PluginRepoIndex WithAuthor(PluginRepoIndex index, PluginAuthorInfo? author) =>
            new()
            {
                PluginId = index.PluginId,
                RepoVersion = index.RepoVersion,
                GeneratedAt = index.GeneratedAt,
                Games = Clone(index.Games)!,
                ReleasesByGameId = Clone(index.ReleasesByGameId)!,
                Author = Clone(author),
                DependencyPresets = Clone(index.DependencyPresets)!
            };

        internal static PluginRepoIndex WithGameAdded(PluginRepoIndex index, GameDefinition game)
        {
            var clone = Clone(index)!;
            clone.Games.Add(Clone(game)!);
            if (!clone.ReleasesByGameId.ContainsKey(game.GameId))
                clone.ReleasesByGameId[game.GameId] = [];
            return clone;
        }

        internal static PluginRepoIndex WithGameUpdated(
            PluginRepoIndex index,
            string currentGameId,
            GameDefinition replacement,
            bool rewriteReleaseGameIds = false)
        {
            var clone = Clone(index)!;
            var gameIndex = clone.Games.FindIndex(g => string.Equals(g.GameId, currentGameId, StringComparison.Ordinal));
            clone.Games[gameIndex] = Clone(replacement)!;

            if (!string.Equals(currentGameId, replacement.GameId, StringComparison.Ordinal) &&
                clone.ReleasesByGameId.Remove(currentGameId, out var releases))
            {
                clone.ReleasesByGameId[replacement.GameId] = rewriteReleaseGameIds
                    ? [.. releases.Select(r => CopyRelease(r, replacement.GameId))]
                    : Clone(releases)!;
            }

            return clone;
        }

        internal static PluginRepoIndex WithGameRemoved(PluginRepoIndex index, string gameId)
        {
            var clone = Clone(index)!;
            clone.Games.RemoveAll(g => string.Equals(g.GameId, gameId, StringComparison.Ordinal));
            clone.ReleasesByGameId.Remove(gameId);
            return clone;
        }

        internal static PluginRepoIndex WithDependencyUpserted(PluginRepoIndex index, string gameId, Dependency dependency)
        {
            var clone = Clone(index)!;
            var game = clone.Games.Single(g => string.Equals(g.GameId, gameId, StringComparison.Ordinal));
            var existing = game.Dependencies.FindIndex(d => string.Equals(d.Id, dependency.Id, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                game.Dependencies[existing] = Clone(dependency)!;
            else
                game.Dependencies.Add(Clone(dependency)!);
            return clone;
        }

        internal static PluginRepoIndex WithDependencyRemoved(PluginRepoIndex index, string gameId, string dependencyId)
        {
            var clone = Clone(index)!;
            var game = clone.Games.Single(g => string.Equals(g.GameId, gameId, StringComparison.Ordinal));
            game.Dependencies.RemoveAll(d => string.Equals(d.Id, dependencyId, StringComparison.OrdinalIgnoreCase));
            return clone;
        }

        internal static PluginRepoIndex WithLifecycleScript(
            PluginRepoIndex index,
            string gameId,
            LifecycleSlot slot,
            LifecycleScript? script)
        {
            var clone = Clone(index)!;
            var gameIndex = clone.Games.FindIndex(g => string.Equals(g.GameId, gameId, StringComparison.Ordinal));
            var game = clone.Games[gameIndex];
            clone.Games[gameIndex] = slot switch
            {
                LifecycleSlot.PreInstall => CopyGame(game, preInstall: script, replacePreInstall: true),
                LifecycleSlot.PostInstall => CopyGame(game, postInstall: script, replacePostInstall: true),
                LifecycleSlot.PostUninstall => CopyGame(game, postUninstall: script, replacePostUninstall: true),
                _ => throw new ArgumentOutOfRangeException(nameof(slot), slot, null)
            };
            return clone;
        }

        internal static GameDefinition CompleteGame(string gameId, string displayName) =>
            new()
            {
                GameId = gameId,
                DisplayName = displayName,
                ModName = "Echoes",
                Description = "Narrated combat cues and menu readouts.",
                SteamAppId = "39140",
                ExeName = "ff7.exe",
                ProbeRules =
                [
                    CompletePathProbeRule(),
                    new PathProbeRule { Type = "folderExists", RelativePath = "mods" }
                ],
                RegistryProbe = CompleteRegistryProbe(),
                AsciiPathShim = CompleteAsciiPathShim(),
                Dependencies =
                [
                    CompleteDependency("extract-zip-loader", CompleteExtractZipAutoInstall(), CompleteGitHubReleaseVersionDiscovery()),
                    CompleteDependency("runtime-installer", CompleteRunInstallerAutoInstall(), CompleteGitHubReleaseAssetVersionDiscovery()),
                    CompleteDependency("copy-helper", CompleteCopyFileAutoInstall(), CompleteStaticVersionDiscovery()),
                    CompleteDependency("portable-emulator", CompleteExtractAppAutoInstall(), CompleteGitHubReleaseAssetVersionDiscovery())
                ],
                Tags = ["screen-reader", "completable"],
                Languages = ["en", "ja"],
                DefaultPreInstall = CompleteLifecycleScript("scripts/pre-install.ps1"),
                DefaultPostInstall = CompleteLifecycleScript("scripts/post-install.cmd"),
                DefaultPostUninstall = CompleteLifecycleScript("scripts/post-uninstall.bat")
            };

        internal static GameDefinition FlagDrivenGame() =>
            new()
            {
                GameId = AddedGameId,
                DisplayName = "Knights of the Old Republic",
                ModName = "Blindplay",
                Description = "Narrated menus and spoken combat cues.",
                SteamAppId = "32370",
                ExeName = "swkotor.exe",
                ProbeRules = [],
                Dependencies = [],
                Tags = ["screen-reader", "completable"],
                Languages = ["en", "fr"]
            };

        internal static PluginAuthorInfo CompleteAuthor() =>
            new()
            {
                DisplayName = "RealAmethyst",
                Bio = "Accessibility-focused mod author.",
                WebsiteUrl = "https://example.com/author",
                DiscordUrl = "https://discord.gg/example",
                PatreonUrl = "https://patreon.com/example",
                GitHubUrl = "https://github.com/RealAmethyst",
                DonationUrl = "https://ko-fi.com/example"
            };

        internal static PluginAuthorInfo AlternateAuthor() =>
            new()
            {
                DisplayName = "Second Author",
                Bio = "Updated author profile.",
                WebsiteUrl = "https://example.com/updated",
                DiscordUrl = "https://discord.gg/updated",
                PatreonUrl = "https://patreon.com/updated",
                GitHubUrl = "https://github.com/updated",
                DonationUrl = "https://buymeacoffee.com/updated"
            };

        internal static PathProbeRule CompletePathProbeRule() =>
            new() { Type = "fileExists", RelativePath = "ff7.exe" };

        internal static RegistryProbe CompleteRegistryProbe() =>
            new()
            {
                Hive = "HKCU",
                Key = "SOFTWARE\\SquareEnix\\FF7",
                Value = "InstallPath",
                ProbeSubfolders = false
            };

        internal static AsciiPathShim CompleteAsciiPathShim() =>
            new()
            {
                JunctionName = "FF7ASCII",
                Reason = "The loader cannot start from a non-ASCII game path."
            };

        internal static Dependency CompleteDependency(
            string id,
            DependencyAutoInstall autoInstall,
            DependencyVersionDiscovery versionDiscovery) =>
            new()
            {
                Id = id,
                Type = "system",
                MinVersion = "1.2.3",
                Check = CompleteDependencyCheck(),
                Fix = CompleteDependencyFix(autoInstall),
                Required = false,
                IsGameInstaller = true,
                VersionDiscovery = versionDiscovery
            };

        internal static DependencyCheck CompleteDependencyCheck() =>
            new()
            {
                RegistryKey = "SOFTWARE\\Vendor\\Runtime",
                RegistryValue = "Version",
                RegistryHive = "HKLM",
                RegistryView = "64",
                FilePath = "BepInEx/core/doorstop.dll"
            };

        internal static DependencyFix CompleteDependencyFix(DependencyAutoInstall autoInstall) =>
            new()
            {
                DownloadUrl = "https://downloads.example.com/dependencies/runtime.zip",
                BundledPath = "deps/runtime.zip",
                AutoInstall = autoInstall
            };

        internal static ExtractZipAutoInstall CompleteExtractZipAutoInstall() =>
            new()
            {
                Sha256 = new string('a', 64),
                TargetDir = "BepInEx",
                Blocklist = ["*.txt", "examples/*"]
            };

        internal static RunInstallerAutoInstall CompleteRunInstallerAutoInstall() =>
            new()
            {
                Sha256 = new string('b', 64),
                Args = ["/quiet", "/norestart"],
                NeedsAdmin = true
            };

        internal static CopyFileAutoInstall CompleteCopyFileAutoInstall() =>
            new()
            {
                Sha256 = new string('c', 64),
                TargetDir = "plugins",
                TargetFileName = "helper.dll"
            };

        internal static ExtractAppAutoInstall CompleteExtractAppAutoInstall() =>
            new() { Sha256 = new string('d', 64) };

        internal static GitHubReleaseVersionDiscovery CompleteGitHubReleaseVersionDiscovery() =>
            new() { Repo = "owner/repo" };

        internal static GitHubReleaseAssetVersionDiscovery CompleteGitHubReleaseAssetVersionDiscovery() =>
            new() { Repo = "owner/repo", AssetGlob = "*x64*.zip" };

        internal static StaticVersionDiscovery CompleteStaticVersionDiscovery() => new();

        internal static LifecycleScript CompleteLifecycleScript(string executable) =>
            new()
            {
                Executable = executable,
                NeedsAdmin = true,
                FailureFatal = false,
                What = "Patch a configuration file.",
                Why = "The mod requires an accessibility-specific override.",
                Modifies = "Game configuration and accessibility assets.",
                InstallToGameFolder = true,
                RunOnUpdate = true,
                RunFromGameFolder = true
            };

        internal static LifecycleScript ReplacementLifecycleScript() =>
            CompleteLifecycleScript("scripts/replacement.ps1");

        internal static ModRelease CompleteRelease(string gameId) =>
            new()
            {
                GameId = gameId,
                PluginId = PluginId,
                Version = "1.0.0",
                Channel = "stable",
                PackageUrl = new Uri($"https://downloads.example.com/{gameId}/1.0.0/mod.zip"),
                Sha256 = new string('e', 64),
                ChangelogUrl = "https://example.com/changelog/1.0.0",
                Notes = "## Changes\n- Added spoken inventory cues.",
                Compatibility = CompleteCompatibility(),
                Patreon = CompletePatreonGate()
            };

        internal static CompatibilityInfo CompleteCompatibility() =>
            new()
            {
                MinGameVersion = "1.0.0",
                MaxGameVersion = "1.9.9",
                Notes = "Requires the updated accessibility bootstrapper."
            };

        internal static PatreonGate CompletePatreonGate() =>
            new()
            {
                CampaignId = "campaign-42",
                TierIds = ["bronze", "silver"],
                PostId = "123456",
                AttachmentFileName = "ff7-accessibility.zip",
                ServerUrl = "https://patreon-downloads.example.com/ff7"
            };

        internal static DependencyPreset CompleteDependencyPreset() =>
            new()
            {
                Id = "runtime-preset",
                DisplayName = "Runtime Preset",
                Dependency = CompleteDependency("preset-runtime", CompleteRunInstallerAutoInstall(), CompleteGitHubReleaseAssetVersionDiscovery())
            };

        internal static T? Clone<T>(T? value)
        {
            if (value is null)
                return default;
            return JsonSerializer.Deserialize<T>(Serialize(value), JsonOptions);
        }

        internal static string Serialize(object? value)
        {
            if (value is null)
                return "null";
            return JsonSerializer.Serialize(value, value.GetType(), JsonOptions);
        }

        internal static string CanonicalJson(object? value)
        {
            JsonNode? node = value switch
            {
                null => null,
                JsonNode jsonNode => jsonNode,
                _ => JsonSerializer.SerializeToNode(value, value.GetType(), JsonOptions)
            };

            return Normalize(node)?.ToJsonString(JsonOptions) ?? "null";
        }

        internal static void AssertJsonEquivalent(object? expected, object? actual) =>
            Assert.Equal(CanonicalJson(expected), CanonicalJson(actual));

        private static JsonNode? Normalize(JsonNode? node)
        {
            switch (node)
            {
                case null:
                    return null;
                case JsonObject obj:
                {
                    var normalized = new JsonObject();
                    foreach (var property in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                        normalized[property.Key] = Normalize(property.Value);
                    return normalized;
                }
                case JsonArray array:
                {
                    var normalized = new JsonArray();
                    foreach (var item in array)
                        normalized.Add(Normalize(item));
                    return normalized;
                }
                default:
                    return JsonNode.Parse(node.ToJsonString());
            }
        }

        private static GameDefinition CopyGame(
            GameDefinition game,
            LifecycleScript? preInstall = null,
            bool replacePreInstall = false,
            LifecycleScript? postInstall = null,
            bool replacePostInstall = false,
            LifecycleScript? postUninstall = null,
            bool replacePostUninstall = false) =>
            new()
            {
                GameId = game.GameId,
                DisplayName = game.DisplayName,
                ModName = game.ModName,
                Description = game.Description,
                SteamAppId = game.SteamAppId,
                ExeName = game.ExeName,
                ProbeRules = Clone(game.ProbeRules)!,
                RegistryProbe = Clone(game.RegistryProbe),
                AsciiPathShim = Clone(game.AsciiPathShim),
                Dependencies = Clone(game.Dependencies)!,
                Tags = Clone(game.Tags)!,
                Languages = Clone(game.Languages)!,
                DefaultPreInstall = replacePreInstall ? Clone(preInstall) : Clone(game.DefaultPreInstall),
                DefaultPostInstall = replacePostInstall ? Clone(postInstall) : Clone(game.DefaultPostInstall),
                DefaultPostUninstall = replacePostUninstall ? Clone(postUninstall) : Clone(game.DefaultPostUninstall)
            };

        private static ModRelease CopyRelease(ModRelease release, string gameId) =>
            new()
            {
                GameId = gameId,
                PluginId = release.PluginId,
                Version = release.Version,
                Channel = release.Channel,
                PackageUrl = release.PackageUrl,
                Sha256 = release.Sha256,
                ChangelogUrl = release.ChangelogUrl,
                Notes = release.Notes,
                Compatibility = Clone(release.Compatibility),
                Patreon = Clone(release.Patreon)
            };
    }
}
