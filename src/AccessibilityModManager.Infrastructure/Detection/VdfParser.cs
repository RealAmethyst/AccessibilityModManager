namespace AccessibilityModManager.Infrastructure.Detection;

/// <summary>
/// Parses Valve's VDF (KeyValues) format used by libraryfolders.vdf.
/// Only handles the subset needed for Steam library detection.
/// </summary>
public static class VdfParser
{
    /// <summary>
    /// Extracts Steam library folder paths from libraryfolders.vdf content.
    /// </summary>
    public static List<string> ParseLibraryFolders(string vdfContent)
    {
        var paths = new List<string>();
        var lines = vdfContent.Split('\n');

        // libraryfolders.vdf has a structure like:
        // "0" { "path" "C:\\Program Files (x86)\\Steam" ... }
        // "1" { "path" "D:\\SteamLibrary" ... }
        // We look for "path" keys and extract their values.

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
            {
                var path = ExtractQuotedValue(trimmed);
                if (!string.IsNullOrEmpty(path))
                {
                    // VDF uses escaped backslashes
                    path = path.Replace("\\\\", "\\");
                    paths.Add(path);
                }
            }
        }

        return paths;
    }

    /// <summary>
    /// Extracts installed app IDs from a libraryfolders.vdf entry's "apps" block.
    /// Returns a mapping of library path -> list of app IDs.
    /// </summary>
    public static Dictionary<string, List<string>> ParseLibraryApps(string vdfContent)
    {
        var result = new Dictionary<string, List<string>>();
        var lines = vdfContent.Split('\n');
        string? currentPath = null;
        var inApps = false;
        var braceDepth = 0;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (trimmed.StartsWith("\"path\"", StringComparison.OrdinalIgnoreCase))
            {
                currentPath = ExtractQuotedValue(trimmed)?.Replace("\\\\", "\\");
                if (currentPath != null && !result.ContainsKey(currentPath))
                    result[currentPath] = [];
            }
            else if (trimmed == "\"apps\"")
            {
                inApps = true;
            }
            else if (inApps && trimmed == "{")
            {
                braceDepth++;
            }
            else if (inApps && trimmed == "}")
            {
                braceDepth--;
                if (braceDepth <= 0)
                    inApps = false;
            }
            else if (inApps && currentPath != null && braceDepth > 0)
            {
                // Lines like: "374320"  "12345678"
                var appId = ExtractFirstQuotedString(trimmed);
                if (!string.IsNullOrEmpty(appId))
                    result[currentPath].Add(appId);
            }
        }

        return result;
    }

    private static string? ExtractQuotedValue(string line)
    {
        // Format: "key"    "value"
        var parts = SplitQuotedPairs(line);
        return parts.Count >= 2 ? parts[1] : null;
    }

    private static string? ExtractFirstQuotedString(string line)
    {
        var parts = SplitQuotedPairs(line);
        return parts.Count >= 1 ? parts[0] : null;
    }

    private static List<string> SplitQuotedPairs(string line)
    {
        var results = new List<string>();
        var inQuote = false;
        var current = new System.Text.StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '"')
            {
                if (inQuote)
                {
                    results.Add(current.ToString());
                    current.Clear();
                    inQuote = false;
                }
                else
                {
                    inQuote = true;
                }
            }
            else if (inQuote)
            {
                current.Append(c);
            }
        }

        return results;
    }
}
