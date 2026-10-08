using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;
using AccessibilityModManager.Infrastructure.Security;
using AccessibilityModManager.Infrastructure.Services;

namespace AccessibilityModManager.Authoring.Services;

public sealed class XivLauncherPackageBuilder(ManifestBuilderService builder, string? bridgeCacheRoot = null)
{
    public async Task<BuiltPackage> BuildAsync(string sourceFolder, string gameId, string pluginId,
        string version, IList<Dependency> dependencies, string output, string assembly, string internalName,
        string workingPluginId, CancellationToken ct = default)
    {
        if (dependencies.Count(d => d.Fix?.Xlm is not null &&
                DependencyTargeting.AppliesTo(d, ReleaseTarget.XivLauncher)) != 1)
            throw new InvalidOperationException("Add the XIVLauncher on Linux (XLM) dependency preset before building this package.");
        var sourceAssembly = PathSafety.CombineContained(sourceFolder, assembly);
        if (!File.Exists(sourceAssembly)) throw new InvalidDataException("Choose the built plugin folder containing " + assembly);
        if (!File.Exists(Path.ChangeExtension(sourceAssembly, ".json")))
            throw new InvalidDataException("The plugin folder must also contain its matching JSON metadata file.");
        var metadata = JsonNode.Parse(await File.ReadAllTextAsync(Path.ChangeExtension(sourceAssembly, ".json"), ct));
        if (metadata?["InternalName"]?.GetValue<string>() != internalName)
            throw new InvalidDataException("The plugin JSON's InternalName does not match the package settings.");
        var stage = Path.Combine(Path.GetTempPath(), "amm-xiv-package-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            foreach (var file in Directory.EnumerateFiles(sourceFolder, "*", SearchOption.AllDirectories))
            {
                PathSafety.EnsureNoReparseTraversal(sourceFolder, file, "plugin source file");
                var target = PathSafety.CombineContained(stage, Path.GetRelativePath(sourceFolder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
            string? bridge = null;
            var pluginDirectory = Path.GetDirectoryName(PathSafety.CombineContained(stage, assembly))!;
            if (File.Exists(Path.Combine(pluginDirectory, "prism.dll")))
            {
                bridge = "amm-wine-bridge";
                using var client = new HttpClient();
                await XivLauncherSpeech.PrepareAsync(pluginDirectory,
                    Path.Combine(stage, bridge), client,
                    bridgeCacheRoot ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "AccessibilityModManager", "prism-bridges"), ct);
            }
            return await builder.BuildPackageAsync(stage, gameId, pluginId, version, dependencies, output,
                targetPlatform: ReleaseTarget.XivLauncher, ct: ct, xivLauncher: new XivLauncherConfig
                {
                    PluginAssembly = assembly, InternalName = internalName,
                    WorkingPluginId = workingPluginId, BridgeDirectory = bridge
                });
        }
        finally { Directory.Delete(stage, true); }
    }
}
