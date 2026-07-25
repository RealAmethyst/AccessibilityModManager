using System.IO.Compression;
using System.Text;
using AccessibilityModManager.Core.Models;
using Serilog;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// What a wrapped mod ZIP looks like to the manager, checked BEFORE it is published.
/// </summary>
/// <param name="Errors">
/// Everything that would make <c>InstallerEngine</c> refuse this package. Empty means the
/// manager will accept it (subject to the runtime-only checks — collisions against other
/// installed mods and verify rules against a real game folder — which can't be answered
/// from the ZIP alone).
/// </param>
public sealed record PackageValidationReport(IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// The authoring-side mirror of the manager's install-time package gates (audit finding 38:
/// "the tool never opens the ZIP it publishes"). Every rule here exists because
/// <see cref="Installer.InstallerEngine"/> enforces it at install time — a package that fails
/// this check would be downloaded, hash-verified, and then rejected on the user's machine,
/// which is the worst place to find out. Kept in Infrastructure (not the AuthorTool) so the
/// two stay in step and so it can be unit-tested.
/// </summary>
public static class PluginPackageValidation
{
    /// <summary>Manifest lives at the ZIP root — the engine reads <c>tempDir/manifest.json</c>.</summary>
    public const string ManifestEntryName = "manifest.json";

    /// <summary>
    /// Reads <paramref name="zipStream"/> as a mod package and reports everything the manager
    /// would object to. The stream is left open and is rewound to its start first, so callers
    /// can pass the same held handle they hash and upload from — what we validate is then
    /// provably the same bytes that get published.
    /// </summary>
    public static PackageValidationReport Validate(
        Stream zipStream,
        string expectedPluginId,
        string expectedGameId,
        string expectedVersion,
        ILogger logger)
    {
        var errors = new List<string>();

        if (zipStream.CanSeek) zipStream.Position = 0;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);
        }
        catch (Exception ex)
        {
            return new PackageValidationReport(
                [$"The file isn't a readable ZIP archive ({ex.Message}). Rebuild the package."]);
        }

        using (archive)
        {
            // Entry names the extractor would refuse outright. SafeZipExtractor rejects these on
            // the user's machine; catching them here means the author never ships one.
            foreach (var entry in archive.Entries)
            {
                var name = Normalize(entry.FullName);
                if (name.StartsWith('/') || name.Contains(':') ||
                    name.Split('/').Any(p => p == ".."))
                {
                    errors.Add($"The ZIP contains an unsafe entry path '{entry.FullName}'. " +
                               "The manager refuses archives that can write outside the game folder.");
                }
            }

            // Duplicate entry names would make this check and the install look at different
            // files: extraction writes them in order, so the LAST one wins on disk, while a
            // reader picks the first. Same for two names that differ only in case, which land on
            // the same file on Windows.
            var duplicates = archive.Entries
                .Select(e => Normalize(e.FullName))
                .Where(n => !string.IsNullOrEmpty(n) && !n.EndsWith('/'))
                .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)
                .ToList();
            foreach (var duplicate in duplicates)
            {
                errors.Add($"The ZIP contains '{duplicate}' more than once. Which copy the manager ends up " +
                           "installing depends on extraction order — rebuild the package.");
            }

            var manifestEntry = FindManifestEntry(archive);
            if (manifestEntry == null)
            {
                var nested = archive.Entries.FirstOrDefault(e =>
                    Path.GetFileName(Normalize(e.FullName))
                        .Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase));
                errors.Add(nested == null
                    ? "The ZIP has no manifest.json. Build the package with the Build button so the " +
                      "manifest is generated, rather than zipping the mod files by hand."
                    : $"manifest.json sits at '{nested.FullName}' instead of the ZIP root. The manager " +
                      "only looks at the root — rebuild the package so manifest.json is the top-level entry.");
                return new PackageValidationReport(errors);
            }

            Manifest manifest;
            try
            {
                using var reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8);
                manifest = new ManifestParser(logger).Parse(reader.ReadToEnd());
            }
            catch (Exception ex)
            {
                errors.Add($"manifest.json doesn't parse the way the manager reads it: {ex.Message}");
                return new PackageValidationReport(errors);
            }

            // Identity — the engine compares all three and aborts the install on any mismatch.
            if (!string.Equals(manifest.PluginId, expectedPluginId, StringComparison.Ordinal))
            {
                errors.Add($"The package's manifest says pluginId '{manifest.PluginId}', but this index " +
                           $"publishes as '{expectedPluginId}'. The manager refuses the mismatch.");
            }
            if (!string.Equals(manifest.GameId, expectedGameId, StringComparison.Ordinal))
            {
                errors.Add($"The package's manifest says gameId '{manifest.GameId}', but this release is " +
                           $"for '{expectedGameId}'. The wrong ZIP may be selected.");
            }
            if (!string.Equals(manifest.ModVersion.Trim(), expectedVersion.Trim(), StringComparison.Ordinal))
            {
                errors.Add($"The package's manifest says version '{manifest.ModVersion}', but this release " +
                           $"is '{expectedVersion}'. Rebuild the package with the version you're publishing.");
            }

            var entryNames = new HashSet<string>(
                archive.Entries.Select(e => Normalize(e.FullName)), StringComparer.OrdinalIgnoreCase);

            CheckActionSources(manifest, entryNames, errors);
            CheckLifecycleScripts(manifest, entryNames, errors);
            CheckVerifyRules(manifest, errors);
        }

        return new PackageValidationReport(errors);
    }

    /// <summary>
    /// Every install action copies from a path inside the extracted package. A missing source
    /// fails mid-install — after the backup is taken — so it's worth catching before publish.
    /// </summary>
    private static void CheckActionSources(
        Manifest manifest, HashSet<string> entryNames, List<string> errors)
    {
        foreach (var action in manifest.InstallActions)
        {
            switch (action)
            {
                case CopyFileAction copy:
                    RequireFile(copy.Source, "copyFile");
                    RequireContainedTarget(copy.Target, "copyFile");
                    break;
                case ReplaceFileAction replace:
                    RequireFile(replace.Source, "replaceFile");
                    RequireContainedTarget(replace.Target, "replaceFile");
                    break;
                case CopyFolderAction folder:
                    RequireFolder(folder.SourceDir);
                    RequireContainedTarget(folder.TargetDir, "copyFolder");
                    break;
            }
        }

        // Where an action WRITES matters as much as where it reads: the manager resolves every
        // target against the game folder and aborts on anything that escapes it — but only once
        // the install is under way and the backup has been taken. Catching it here means the
        // author never ships a package that fails halfway through someone's game folder.
        void RequireContainedTarget(string target, string actionName)
        {
            if (string.IsNullOrWhiteSpace(target)) return;
            var normalized = Normalize(target);
            if (Path.IsPathRooted(target) || normalized.StartsWith('/') ||
                normalized.Split('/').Any(p => p == "..") ||
                target.Contains(':'))
            {
                errors.Add($"The manifest's {actionName} action writes to '{target}', which points outside " +
                           "the game folder. The manager refuses install targets that escape it.");
            }
        }

        void RequireFile(string source, string actionName)
        {
            if (string.IsNullOrWhiteSpace(source)) return; // parser-level concern
            if (!entryNames.Contains(Normalize(source)))
            {
                errors.Add($"The manifest's {actionName} action copies '{source}', but the ZIP has no such " +
                           "file. The install would fail halfway through on the user's machine.");
            }
        }

        void RequireFolder(string sourceDir)
        {
            if (string.IsNullOrWhiteSpace(sourceDir)) return;
            var prefix = Normalize(sourceDir).TrimEnd('/') + "/";
            if (!entryNames.Any(n => n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"The manifest's copyFolder action copies from '{sourceDir}', but the ZIP has " +
                           "nothing under that folder.");
            }
        }
    }

    /// <summary>
    /// Verify rules run against the game folder after installing, and the manager confines them
    /// the same way it confines install targets. A rule that can never pass — an escaping path,
    /// or a hashEquals with no hash — turns a good install into a rolled-back one.
    /// </summary>
    private static void CheckVerifyRules(Manifest manifest, List<string> errors)
    {
        foreach (var rule in manifest.Verify)
        {
            var normalized = Normalize(rule.Path);
            if (string.IsNullOrWhiteSpace(rule.Path) ||
                Path.IsPathRooted(rule.Path) || normalized.StartsWith('/') ||
                normalized.Split('/').Any(p => p == ".."))
            {
                errors.Add($"The manifest's verify rule points at '{rule.Path}', which isn't a path inside " +
                           "the game folder.");
            }

            if (string.Equals(rule.Type, "hashEquals", StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(rule.Sha256))
            {
                errors.Add($"The manifest's hashEquals rule for '{rule.Path}' has no sha256 to compare " +
                           "against, so the install could never verify.");
            }
        }
    }

    /// <summary>
    /// Mirrors <c>LifecycleScriptRunner.ValidateScriptInStaging</c>: allowed extension, no
    /// escaping the package, and the file actually present. Also insists the author actually
    /// filled in what the script does — those three lines are the entire basis on which a user
    /// consents to running author-supplied code.
    /// </summary>
    private static void CheckLifecycleScripts(
        Manifest manifest, HashSet<string> entryNames, List<string> errors)
    {
        Check(manifest.PreInstall, "pre-install");
        Check(manifest.PostInstall, "post-install");
        Check(manifest.PostUninstall, "post-uninstall");

        void Check(LifecycleScript? script, string label)
        {
            if (script == null) return;

            if (string.IsNullOrWhiteSpace(script.Executable))
            {
                errors.Add($"The {label} script has no executable path.");
                return;
            }

            var ext = Path.GetExtension(script.Executable).ToLowerInvariant();
            if (ext is not ".exe" and not ".ps1" and not ".cmd" and not ".bat")
            {
                errors.Add($"The {label} script '{script.Executable}' has extension '{ext}'. The manager " +
                           "only runs .exe, .ps1, .cmd, and .bat.");
            }

            var normalized = Normalize(script.Executable);
            if (normalized.StartsWith('/') || normalized.Split('/').Any(p => p == ".."))
            {
                errors.Add($"The {label} script path '{script.Executable}' escapes the package folder.");
                return;
            }

            if (!entryNames.Contains(normalized))
            {
                errors.Add($"The {label} script '{script.Executable}' isn't in the ZIP. The manager aborts " +
                           "the install when a declared script is missing.");
            }

            if (string.IsNullOrWhiteSpace(script.What) ||
                string.IsNullOrWhiteSpace(script.Why) ||
                string.IsNullOrWhiteSpace(script.Modifies))
            {
                errors.Add($"The {label} script doesn't say what it does, why, and what it changes. Those " +
                           "three lines are what the user is shown before agreeing to run it.");
            }
        }
    }

    private static ZipArchiveEntry? FindManifestEntry(ZipArchive archive) =>
        archive.Entries.FirstOrDefault(e =>
            Normalize(e.FullName).Equals(ManifestEntryName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// ZIP entry names are POSIX; manifests may be authored with either separator. Compare on
    /// one shape, with a leading "./" stripped the way extraction would.
    /// </summary>
    private static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/').Trim();
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized;
    }
}
