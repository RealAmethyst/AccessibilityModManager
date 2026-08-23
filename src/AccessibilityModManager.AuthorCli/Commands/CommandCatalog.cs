using System.CommandLine;

namespace AccessibilityModManager.AuthorCli.Commands;

public static class CommandCatalog
{
    public static IReadOnlyList<string> TopLevelNames { get; } =
    [
        "project", "author", "game", "dependency", "script", "package", "release",
        "index", "github", "patreon", "server", "signing", "registry"
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
            ServerCommands.Create(services),
            SigningCommands.Create(services),
            RegistryCommands.Create(services)
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
        "index destination" or "index destination get" or "index membership" or
        "index lock" or "index lock show" => Project(path),
        "index publish" => $"{Project(path)} --message \"Publish catalog update\" --yes",
        "index destination set" => $"amm-author {path} github --project \"C:\\Mods\\Sample\"",
        "index lock break" => $"amm-author {path} --fingerprint abc123 --project \"C:\\Mods\\Sample\" --yes",

        "github" => Help(path),
        "github status" or "github repos" => $"amm-author {path}",
        "github releases" => $"amm-author {path} --repo owner/sample-plugin",

        "patreon" => Help(path),
        "patreon status" or "patreon login" or "patreon logout" or "patreon tiers" or
        "patreon post" => $"amm-author {path}",
        "patreon post validate" => $"amm-author {path} --url \"https://www.patreon.com/posts/123456\"",

        "server" => Help(path),
        "server status" or "server test" or "server self-test" or
        "server release" or "server gate" or "server lock" or "server lock show" => Project(path),
        "server clear" => $"amm-author {path} --yes",
        "server configure" => $"Get-Content \"C:\\Secrets\\ssh-passphrase.txt\" | amm-author {path} --input \"C:\\Mods\\Sample\\server.json\" --passphrase-stdin",
        "server release inspect" => ServerRelease(path, confirmed: false),
        "server release upload" => ServerRelease(path, confirmed: true),
        "server gate set" => $"amm-author {path} --game sample-game --version 1.0.0 --input \"C:\\Mods\\Sample\\patreon-gate.json\" --project \"C:\\Mods\\Sample\" --yes",
        "server gate remove" => $"amm-author {path} --game sample-game --version 1.0.0 --project \"C:\\Mods\\Sample\" --yes",
        "server lock break" => $"amm-author {path} --fingerprint abc123 --project \"C:\\Mods\\Sample\" --yes",

        "signing" => Help(path),
        "signing status" => $"amm-author {path} --plugin sample-plugin",
        "signing create" => SecretPipe(path, "new-key-passphrase.txt", "--plugin sample-plugin --passphrase-stdin"),
        "signing export" => SecretPipe(path, "backup-passphrase.txt", "--plugin sample-plugin --destination \"C:\\Secrets\\sample-plugin-key.json\" --passphrase-stdin"),
        "signing import" => SecretPipe(path, "backup-passphrase.txt", "--source \"C:\\Secrets\\sample-plugin-key.json\" --passphrase-stdin"),
        "signing change-passphrase" => SecretPipe(path, "old-and-new-passphrases.txt", "--plugin sample-plugin --passphrases-stdin"),
        "signing claims" or "signing claims preview" or "signing head" => Project(path),
        "signing claims sign" or "signing head confirm" or "signing head commit-pending" or
        "signing head resume" => $"{Project(path)} --yes",
        "signing head status" => $"amm-author {path} --plugin sample-plugin",

        "registry" => AdminHelp(path),
        "registry status" => "amm-author-admin registry status",
        "registry open" => "amm-author-admin registry open --repo \"C:\\Registry\\PluginRegistry\"",
        "registry refresh" => AdminRepo(path),
        "registry json" => AdminHelp(path),
        "registry json show" or "registry json validate" => $"amm-author-admin {path} --path \"C:\\Registry\\PluginRegistry\\registry.json\"",
        "registry json save" => $"amm-author-admin {path} --path \"C:\\Registry\\PluginRegistry\\registry.json\" --input \"C:\\Registry\\candidate.json\"",
        "registry sign" => $"Get-Content \"C:\\Secrets\\registry-passphrase.txt\" | amm-author-admin {path} --path \"C:\\Registry\\PluginRegistry\\registry.json\" --private-key \"C:\\Secrets\\registry-key.pem\" --passphrase-stdin",
        "registry publish" => $"{AdminRepo(path)} --yes",
        "registry commit" => $"{AdminRepo(path)} --message \"Update registry\" --yes",
        "registry push" => $"{AdminRepo(path)} --yes",

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
    private static string ServerRelease(string path, bool confirmed) =>
        $"amm-author {path} --game sample-game --version 1.0.0 --zip \"C:\\Packages\\sample.zip\" --project \"C:\\Mods\\Sample\"{(confirmed ? " --yes" : string.Empty)}";
    private static string SecretPipe(string path, string file, string arguments) =>
        $"Get-Content \"C:\\Secrets\\{file}\" | amm-author {path} {arguments}";
    private static string AdminRepo(string path) =>
        $"amm-author-admin {path} --repo \"C:\\Registry\\PluginRegistry\"";
}
