using System.CommandLine;
using AccessibilityModManager.AuthorCli;
using AccessibilityModManager.AuthorCli.Commands;

namespace AccessibilityModManager.Tests.AuthorCli;

public sealed class CommandCoverageTests
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredPaths =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["project"] = ["create", "init", "recent", "open", "clone", "pull", "repos", "status"],
            ["author"] = ["show", "set"],
            ["game"] = ["repo", "list", "show", "add", "update", "remove"],
            ["dependency"] = ["list", "show", "set", "remove", "presets", "apply-preset"],
            ["script"] = ["show", "set", "clear"],
            ["package"] = ["build", "validate", "hash"],
            ["release"] = ["list", "show", "add", "edit", "remove", "upload", "publish"],
            ["index"] = ["show", "validate", "reconcile", "save", "membership", "publish"],
            ["github"] = ["status", "repos", "releases"],
            ["directory"] = ["submit"],
            ["patreon"] = ["status", "login", "logout", "tiers", "post", "post validate"],
        };

    [Fact]
    public void Catalog_has_the_complete_ordered_top_level_inventory()
    {
        string[] required =
        [
            "project", "author", "game", "dependency", "script", "package", "release",
            "index", "github", "patreon", "directory"
        ];

        Assert.Equal(required, CommandCatalog.TopLevelNames);
    }

    [Fact]
    public void Every_designed_command_is_registered_in_order_with_description_and_example()
    {
        using var services = CliServices.Create();
        var root = CommandCatalog.CreateRoot(services);

        Assert.Equal(CommandCatalog.TopLevelNames, root.Subcommands.Select(command => command.Name));

        foreach (var group in root.Subcommands)
        {
            Assert.True(RequiredPaths.TryGetValue(group.Name, out var required),
                $"Unexpected top-level command '{group.Name}'.");
            Assert.Equal(required, CollectDescendantPaths(group));

            foreach (var command in SelfAndDescendants(group))
            {
                Assert.False(string.IsNullOrWhiteSpace(command.Description),
                    $"'{FullPath(command)}' has no description.");
                Assert.Contains("Example:", command.Description, StringComparison.Ordinal);
                Assert.DoesNotContain("buu42", command.Description, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static IReadOnlyList<string> CollectDescendantPaths(Command group)
    {
        var paths = new List<string>();
        AddChildren(group, string.Empty, paths);
        return paths;
    }

    private static void AddChildren(Command parent, string prefix, ICollection<string> paths)
    {
        foreach (var child in parent.Subcommands)
        {
            var path = string.IsNullOrEmpty(prefix) ? child.Name : $"{prefix} {child.Name}";
            paths.Add(path);
            AddChildren(child, path, paths);
        }
    }

    private static IEnumerable<Command> SelfAndDescendants(Command command)
    {
        yield return command;
        foreach (var child in command.Subcommands)
        foreach (var descendant in SelfAndDescendants(child))
            yield return descendant;
    }

    private static string FullPath(Command command)
    {
        var names = new Stack<string>();
        for (Command? current = command; current is not null; current = current.Parents.OfType<Command>().FirstOrDefault())
            names.Push(current.Name);
        return string.Join(' ', names);
    }
}
