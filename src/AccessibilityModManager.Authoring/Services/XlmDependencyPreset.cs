using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Authoring.Services;

public static class XlmDependencyPreset
{
    public static Dependency Create() => new()
    {
        Id = "xlm", Type = "system", Required = true,
        TargetPlatforms = [ReleaseTarget.Linux],
        Fix = new DependencyFix
        {
            DownloadUrl = "https://github.com/Blooym/xlm/releases/download/v0.4.0/xlm-x86_64-unknown-linux-gnu",
            Xlm = new XlmAutoInstall { Sha256 = "b839f633b5ae4cea65346e51a0d89d5c2cf6e93e3fc1f46de75d3e3081628691" }
        }
    };
}
