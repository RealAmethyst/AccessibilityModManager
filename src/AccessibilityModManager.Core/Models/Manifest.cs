using System.Text.Json.Serialization;

namespace AccessibilityModManager.Core.Models;

/// <summary>
/// The manifest.json inside a mod package ZIP. Drives the entire install process.
/// </summary>
public sealed class Manifest
{
    public required string GameId { get; init; }
    public required string PluginId { get; init; }
    public required string ModVersion { get; init; }
    public List<InstallAction> InstallActions { get; init; } = [];
    public List<Dependency> Dependencies { get; init; } = [];
    public List<VerifyRule> Verify { get; init; } = [];
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CopyFileAction), "copyFile")]
[JsonDerivedType(typeof(CopyFolderAction), "copyFolder")]
[JsonDerivedType(typeof(ReplaceFileAction), "replaceFile")]
public abstract class InstallAction;

public sealed class CopyFileAction : InstallAction
{
    public required string Source { get; init; }
    public required string Target { get; init; }
}

public sealed class CopyFolderAction : InstallAction
{
    public required string SourceDir { get; init; }
    public required string TargetDir { get; init; }
}

public sealed class ReplaceFileAction : InstallAction
{
    public required string Source { get; init; }
    public required string Target { get; init; }
    public bool Backup { get; init; } = true;
}

public sealed class Dependency
{
    public required string Id { get; init; }
    public required string Type { get; init; } // "system" or "framework"
    public string? MinVersion { get; init; }
    public DependencyCheck? Check { get; init; }
    public DependencyFix? Fix { get; init; }
    public bool Required { get; init; } = true;
}

public sealed class DependencyCheck
{
    public string? RegistryKey { get; init; }
    public string? RegistryValue { get; init; }
    public string? FilePath { get; init; }
}

public sealed class DependencyFix
{
    public string? DownloadUrl { get; init; }
    public string? BundledPath { get; init; }
}

public sealed class VerifyRule
{
    public required string Type { get; init; } // "fileExists", "folderExists", "hashEquals"
    public required string Path { get; init; }
    public string? Sha256 { get; init; }
}
