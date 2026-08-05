using System.CommandLine;
using AccessibilityModManager.AuthorCli.Console;
using AccessibilityModManager.Authoring.Workflows;
using AccessibilityModManager.AuthorTool.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class PackageCommands
{
    public static void AddTo(RootCommand root, IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(services);

        var outcomeWriter = services.GetRequiredService<OutcomeWriter>();
        var console = services.GetRequiredService<ICliConsole>();
        var projects = services.GetRequiredService<AuthorProjectContext>();
        var config = services.GetRequiredService<AuthorConfigService>();
        var workflow = services.GetRequiredService<PackageWorkflow>();
        var hashes = services.GetRequiredService<Sha256HashService>();

        var package = new Command("package", "Build, validate, or hash wrapped mod packages.");

        var build = new Command("build", "Build a manager-format ZIP from a mod source folder.");
        var sourceOption = RequiredStringOption("--source", "Folder containing the mod files to wrap.");
        var buildGameOption = RequiredStringOption("--game", "Game id from the project index.");
        var buildVersionOption = RequiredStringOption("--version", "Release version written into manifest.json.");
        var outputOption = new Option<string>("--output")
        {
            Description = "Output ZIP path. Defaults to the AuthorTool builds folder."
        };
        build.Options.Add(sourceOption);
        build.Options.Add(buildGameOption);
        build.Options.Add(buildVersionOption);
        build.Options.Add(outputOption);
        build.SetAction(async (parseResult, cancellationToken) =>
        {
            var resolved = await CatalogCommandSupport.ResolveProjectAsync(projects, parseResult, cancellationToken);
            var gameId = parseResult.GetValue(buildGameOption)!;
            var version = parseResult.GetValue(buildVersionOption)!;
            var source = parseResult.GetValue(sourceOption)!;
            var game = CatalogCommandSupport.FindGame(resolved.Index, gameId);
            var scriptSources = config.GetGameScriptSources(resolved.ProjectPath, game.GameId);
            var scripts = new LifecycleScriptInputs(
                game.DefaultPreInstall,
                scriptSources?.PreInstall,
                game.DefaultPostInstall,
                scriptSources?.PostInstall,
                game.DefaultPostUninstall,
                scriptSources?.PostUninstall);
            var output = parseResult.GetValue(outputOption);
            if (string.IsNullOrWhiteSpace(output))
            {
                output = Path.Combine(
                    ManifestBuilderService.GetBuildsDirectory(),
                    $"{game.GameId}-v{version.Trim()}-amm.zip");
            }

            var request = new PackageBuildRequest(
                source,
                output,
                resolved.Index.PluginId,
                game.GameId,
                version,
                game.Dependencies,
                scripts);

            if (CatalogCommandSupport.GetDryRun(parseResult))
            {
                var preview = workflow.PreviewBuild(request);
                return CatalogCommandSupport.Complete(
                    outcomeWriter,
                    parseResult,
                    CatalogCommandSupport.Success(
                        "packageBuildPreviewed",
                        new
                        {
                            preview.SourceFolder,
                            preview.OutputZipPath,
                            preview.PluginId,
                            preview.GameId,
                            preview.Version,
                            preview.TopLevelEntryCount,
                            preview.HasLifecycleScripts,
                            dryRun = true
                        },
                        $"Package build is valid and would write '{preview.OutputZipPath}'."));
            }

            var inspection = await workflow.BuildAsync(request, cancellationToken);
            return CatalogCommandSupport.Complete(
                outcomeWriter,
                parseResult,
                CatalogCommandSupport.Success(
                    "packageBuilt",
                    inspection,
                    $"Built and validated '{inspection.ZipPath}' ({inspection.FileCount} files, SHA256 {inspection.Sha256})."));
        });

        var validate = new Command("validate", "Validate a finished package against an expected identity.");
        var zipOption = RequiredStringOption("--zip", "Wrapped package ZIP to inspect.");
        var pluginOption = RequiredStringOption("--plugin", "Expected plugin id.");
        var validateGameOption = RequiredStringOption("--game", "Expected game id.");
        var validateVersionOption = RequiredStringOption("--version", "Expected package version.");
        validate.Options.Add(zipOption);
        validate.Options.Add(pluginOption);
        validate.Options.Add(validateGameOption);
        validate.Options.Add(validateVersionOption);
        validate.SetAction(async (parseResult, cancellationToken) =>
        {
            var inspection = await workflow.ValidateAsync(
                parseResult.GetValue(zipOption)!,
                parseResult.GetValue(pluginOption)!,
                parseResult.GetValue(validateGameOption)!,
                parseResult.GetValue(validateVersionOption)!,
                cancellationToken);

            if (!inspection.Validation.IsValid)
            {
                var messages = new[]
                    {
                        "Package validation failed because the package contents or identity mismatch the expected release."
                    }
                    .Concat(inspection.Validation.Errors)
                    .ToArray();
                throw CatalogCommandSupport.Validation(messages);
            }

            return CatalogCommandSupport.Complete(
                outcomeWriter,
                parseResult,
                CatalogCommandSupport.Success(
                    "packageValidated",
                    inspection,
                    $"Package is valid ({inspection.FileCount} files, SHA256 {inspection.Sha256})."));
        });

        var hash = new Command("hash", "Compute the lowercase SHA256 of a file.");
        var fileOption = RequiredStringOption("--file", "File to hash.");
        hash.Options.Add(fileOption);
        hash.SetAction(async (parseResult, cancellationToken) =>
        {
            var path = Path.GetFullPath(parseResult.GetValue(fileOption)!);
            if (!File.Exists(path))
                throw CatalogCommandSupport.Validation($"File not found: {path}");
            var sha256 = await hashes.ComputeAsync(path, cancellationToken);

            if (!CatalogCommandSupport.GetJson(parseResult))
            {
                await console.Out.WriteLineAsync(sha256);
                await console.Out.FlushAsync();
                return (int)CliExitCode.Success;
            }

            return CatalogCommandSupport.Complete(
                outcomeWriter,
                parseResult,
                CatalogCommandSupport.Success(
                    "packageHashed",
                    new { file = path, sha256 },
                    $"SHA256 {sha256}"));
        });

        package.Subcommands.Add(build);
        package.Subcommands.Add(validate);
        package.Subcommands.Add(hash);
        root.Subcommands.Add(package);
    }

    private static Option<string> RequiredStringOption(string name, string description) =>
        new(name)
        {
            Description = description,
            Required = true
        };
}
