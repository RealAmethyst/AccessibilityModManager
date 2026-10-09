using System.CommandLine;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class CommandCatalog
{
    public static IReadOnlyList<string> TopLevelNames { get; } =
    [
        "project", "author", "game", "dependency", "script", "package", "release",
        "index", "github", "patreon", "directory"
    ];

    public static RootCommand CreateRoot(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        var root = RootCommands.Create();
        Command[] groups =
        [
            ProjectCommands.Create(services),
            AuthorCommands.Create(services),
            GameCommands.Create(services),
            DependencyCommands.Create(services),
            ScriptCommands.Create(services),
            PackageCommands.Create(services),
            ReleaseCommands.Create(services),
            IndexCommands.Create(services),
            GitHubCommands.Create(services),
            PatreonCommands.Create(services),
            DirectoryCommands.Create(services)
        ];

        foreach (var group in groups)
        {
            AddExamples(group, group.Name);
            root.Subcommands.Add(group);
        }

        return root;
    }

    private static void AddExamples(Command command, string path)
    {
        command.Description = $"{(command.Description ?? string.Empty).TrimEnd()}\n\nExample:\n  {ExampleFor(path)}";
        foreach (var child in command.Subcommands)
            AddExamples(child, $"{path} {child.Name}");
    }

    private static string ExampleFor(string path) => path switch
    {
        "project" => Help(path),
        "project create" => "amm-author project create owner/plugin-catalog --plugin-id my-plugin --project ./plugin-catalog --yes",
        "project init" => "amm-author project init sample-plugin --project \"C:\\Mods\\Sample\"",
        "project recent" => "amm-author project recent",
        "project open" => Project(path),
        "project clone" => "amm-author project clone owner/sample-plugin --project \"C:\\Mods\\Sample\"",
        "project pull" => Project(path),
        "project repos" => "amm-author project repos",
        "project status" => Project(path),

        "author" => Help(path),
        "author show" => Project(path),
        "author set" => Input(path, "author.json"),

        "game" => Help(path),
        "game repo" => "amm-author game repo sample-game --repo owner/game-mod --project ./plugin-catalog",
        "game list" => Project(path),
        "game show" => $"amm-author {path} sample-game --project \"C:\\Mods\\Sample\"",
        "game add" => $"amm-author {path} --id sample-game --display-name \"Sample Game\" --project \"C:\\Mods\\Sample\"",
        "game update" => $"amm-author {path} sample-game --display-name \"Updated Game\" --project \"C:\\Mods\\Sample\"",
        "game remove" => $"amm-author {path} sample-game --project \"C:\\Mods\\Sample\" --yes",

        "dependency" => Help(path),
        "dependency list" => CatalogArguments(path, "sample-game"),
        "dependency show" => CatalogArguments(path, "sample-game sample-dependency"),
        "dependency set" => InputWithArgument(path, "sample-game", "dependency.json"),
        "dependency remove" => $"{CatalogArguments(path, "sample-game sample-dependency")} --yes",
        "dependency presets" => Project(path),
        "dependency apply-preset" => CatalogArguments(path, "sample-game sample-preset"),

        "script" => Help(path),
        "script show" => CatalogArguments(path, "sample-game pre-install"),
        "script set" => InputWithArgument(path, "sample-game pre-install", "script.json"),
        "script clear" => CatalogArguments(path, "sample-game pre-install"),

        "package" => Help(path),
        "package build" => $"amm-author {path} --source \"C:\\Mods\\Sample\\Files\" --game sample-game --version 1.0.0 --output \"C:\\Packages\\sample.zip\" --project \"C:\\Mods\\Sample\"",
        "package validate" => $"amm-author {path} --zip \"C:\\Packages\\sample.zip\" --plugin sample-plugin --game sample-game --version 1.0.0",
        "package hash" => $"amm-author {path} --file \"C:\\Packages\\sample.zip\"",

        "release" => Help(path),
        "release list" => CatalogArguments(path, "sample-game"),
        "release show" => CatalogArguments(path, "sample-game 1.0.0 stable"),
        "release add" => InputWithArgument(path, "sample-game", "release.json"),
        "release edit" => InputWithArgument(path, "sample-game 1.0.0 stable", "release.json"),
        "release remove" => $"{CatalogArguments(path, "sample-game 1.0.0 stable")} --yes",
        "release upload" => ReleaseUpload(path),
        "release publish" => $"{ReleaseUpload(path)} --index-message \"Publish sample-game 1.0.0\"",

        "index" => Help(path),
        "index show" or "index validate" or "index reconcile" or "index save" or
        "index membership" => Project(path),
        "index publish" => $"{Project(path)} --message \"Publish catalog update\" --yes",
        "index destination set" => $"amm-author {path} github --project \"C:\\Mods\\Sample\"",

        "directory submit" => "amm-author directory submit --project ./catalog --yes",

        "github" => Help(path),
        "github status" or "github repos" => $"amm-author {path}",
        "github releases" => $"amm-author {path} --repo owner/sample-plugin",

        "patreon" => Help(path),
        "patreon status" or "patreon login" or "patreon logout" or "patreon tiers" or
        "patreon post" => $"amm-author {path}",
        "patreon post validate" => $"amm-author {path} --url \"https://www.patreon.com/posts/123456\"",

        _ => Help(path)
    };

    private static string Help(string path) => $"amm-author {path} --help";
    private static string AdminHelp(string path) => $"amm-author-admin {path} --help";
    private static string Project(string path) => $"amm-author {path} --project \"C:\\Mods\\Sample\"";
    private static string CatalogArguments(string path, string arguments) =>
        $"amm-author {path} {arguments} --project \"C:\\Mods\\Sample\"";
    private static string Input(string path, string file) =>
        $"amm-author {path} --input \"C:\\Mods\\Sample\\{file}\" --project \"C:\\Mods\\Sample\"";
    private static string InputWithArgument(string path, string argument, string file) =>
        $"amm-author {path} {argument} --input \"C:\\Mods\\Sample\\{file}\" --project \"C:\\Mods\\Sample\"";
    private static string ReleaseUpload(string path) =>
        $"amm-author {path} --game sample-game --version 1.0.0 --channel stable --repo owner/sample-plugin --zip \"C:\\Packages\\sample.zip\" --project \"C:\\Mods\\Sample\" --yes";
}
