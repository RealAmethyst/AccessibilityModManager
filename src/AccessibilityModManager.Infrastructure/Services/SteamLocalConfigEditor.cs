using System.Text;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>
/// Edits only one app's LaunchOptions value in Steam's KeyValues localconfig.vdf. It retains
/// every other byte of the input text, including settings unrelated to this game.
/// </summary>
public static class SteamLocalConfigEditor
{
    public static string? Read(string text, string appId)
    {
        var app = FindApp(text, appId);
        return app is null ? null : FindOne(app.Children!, "LaunchOptions")?.Value;
    }

    public static bool HasApp(string text, string appId) => FindApp(text, appId) is not null;

    public static string Install(string text, string appId, SteamLaunchOptionsPlan plan)
    {
        var app = FindApp(text, appId);
        if ((app is not null) != plan.AppWasPresent)
            throw new InvalidOperationException("Steam's game settings changed after the installation plan was prepared.");
        if (app is null)
        {
            if (plan.WasPresent || plan.Previous.Length != 0)
                throw new InvalidOperationException("A missing Steam app block cannot have previous launch options.");
            var apps = FindApps(text);
            var appsClose = apps.CloseBrace;
            var appsLineStart = text.LastIndexOf('\n', Math.Max(0, appsClose - 1)) + 1;
            var appsIndent = text[appsLineStart..appsClose];
            if (appsIndent.Any(c => c is not (' ' or '\t')))
                throw new InvalidOperationException("Steam apps block does not use a separate closing line.");
            var appsNewline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
            var block = appsIndent + "\t\"" + appId + "\"" + appsNewline +
                        appsIndent + "\t{" + appsNewline +
                        appsIndent + "\t\t\"LaunchOptions\"\t\t" + Quote(plan.Installed) + appsNewline +
                        appsIndent + "\t}" + appsNewline;
            return text[..appsLineStart] + block + text[appsLineStart..];
        }
        var option = FindOne(app.Children!, "LaunchOptions");
        if (!string.Equals(option?.Value ?? string.Empty, plan.Previous, StringComparison.Ordinal) ||
            (option is not null) != plan.WasPresent)
            throw new InvalidOperationException(
                "Steam launch options changed after the installation plan was prepared.");

        if (option is not null)
            return text[..option.ValueStart] + Quote(plan.Installed) + text[option.ValueEnd..];

        var close = app.CloseBrace;
        var lineStart = text.LastIndexOf('\n', Math.Max(0, close - 1)) + 1;
        var indent = text[lineStart..close];
        if (indent.Any(c => c is not (' ' or '\t')))
            throw new InvalidOperationException("Steam app block does not use a separate closing line.");
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var inserted = indent + "\t\"LaunchOptions\"\t\t" + Quote(plan.Installed) + newline;
        return text[..lineStart] + inserted + text[lineStart..];
    }

    public static string Restore(string text, string appId, SteamLaunchOptionsPlan plan)
    {
        var app = FindApp(text, appId);
        if (app is null)
            throw new InvalidOperationException("The mod's Steam app block is missing.");
        var option = FindOne(app.Children!, "LaunchOptions")
            ?? throw new InvalidOperationException("The mod's Steam launch option is missing.");
        plan.Restore(option.Value!);
        if (!plan.AppWasPresent && app.Children!.Count == 1)
        {
            var appLineStart = text.LastIndexOf('\n', Math.Max(0, app.KeyStart - 1)) + 1;
            var appLineEnd = text.IndexOf('\n', app.CloseBrace);
            if (appLineEnd < 0) appLineEnd = text.Length;
            else appLineEnd++;
            if (text[appLineStart..app.KeyStart].Any(c => c is not (' ' or '\t')) ||
                text[(app.CloseBrace + 1)..appLineEnd].Any(c => c is not (' ' or '\t' or '\r' or '\n')))
                throw new InvalidOperationException("The mod-created Steam game block shares a line with other data.");
            return text[..appLineStart] + text[appLineEnd..];
        }
        if (plan.WasPresent)
            return text[..option.ValueStart] + Quote(plan.Previous) + text[option.ValueEnd..];

        var lineStart = text.LastIndexOf('\n', Math.Max(0, option.KeyStart - 1)) + 1;
        var lineEnd = text.IndexOf('\n', option.ValueEnd);
        if (lineEnd < 0) lineEnd = text.Length;
        else lineEnd++;
        var before = text[lineStart..option.KeyStart];
        var after = text[option.ValueEnd..lineEnd];
        if (before.Any(c => c is not (' ' or '\t')) ||
            after.Any(c => c is not (' ' or '\t' or '\r' or '\n')))
            throw new InvalidOperationException("The mod's Steam launch option shares a line with other data.");
        return text[..lineStart] + text[lineEnd..];
    }

    // Reuse the strict, position-preserving parser for Steam's global compatibility mapping.
    public static string? ReadBlock(string text, params string[] path)
    {
        var node = FindPath(text, path);
        if (node is null) return null;
        if (node.Children is null) throw new InvalidDataException("Expected a Steam settings block.");
        return text[node.KeyStart..(node.CloseBrace + 1)];
    }

    public static bool BlocksEqual(string? left, string? right)
    {
        if (left is null || right is null) return left == right;
        static string Canonical(string text)
        {
            var tokens = Tokenize(text);
            var position = 0;
            static string Render(List<Node> nodes) => string.Join(";", nodes
                .OrderBy(n => n.Key, StringComparer.OrdinalIgnoreCase)
                .Select(n => Quote(n.Key.ToLowerInvariant()) + (n.Children is null ? Quote(n.Value!) : "{" + Render(n.Children) + "}")));
            return Render(ParseNodes(tokens, ref position, false));
        }
        return Canonical(left) == Canonical(right);
    }

    public static string SetBlock(string text, string[] path, string? block)
    {
        if (path.Length == 0) throw new ArgumentException("A settings path is required.");
        var node = FindPath(text, path);
        if (node is not null)
        {
            if (node.Children is null) throw new InvalidDataException("Expected a Steam settings block.");
            return text[..node.KeyStart] + (block ?? "") + text[(node.CloseBrace + 1)..];
        }
        if (block is null) return text;
        if (path.Length == 1) throw new InvalidDataException("Steam configuration root is missing.");
        var parent = FindPath(text, path[..^1]);
        if (parent is null)
            return SetBlock(text, path[..^1], Quote(path[^2]) + "\n{\n" + block + "\n}");
        if (parent.Children is null) throw new InvalidDataException("Expected a Steam settings block.");
        return text.Insert(parent.CloseBrace, "\n" + block + "\n");
    }

    private static Node? FindPath(string text, string[] path)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var nodes = ParseNodes(tokens, ref position, nested: false);
        Node? node = null;
        foreach (var key in path)
        {
            node = FindOne(nodes, key);
            if (node is null) return null;
            nodes = node.Children ?? [];
        }
        return node;
    }

    private static Node? FindApp(string text, string appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit))
            throw new InvalidOperationException("Steam App ID must contain only digits.");
        var apps = FindApps(text);
        return FindOne(apps.Children!, appId);
    }

    private static Node FindApps(string text)
    {
        var tokens = Tokenize(text);
        var position = 0;
        var roots = ParseNodes(tokens, ref position, nested: false);
        if (position != tokens.Count)
            throw new InvalidDataException("Steam localconfig.vdf has trailing malformed data.");
        var store = FindOne(roots, "UserLocalConfigStore")
            ?? throw new InvalidDataException("Steam localconfig.vdf has no UserLocalConfigStore.");
        var software = RequireChild(store, "Software");
        var valve = RequireChild(software, "Valve");
        var steam = RequireChild(valve, "Steam");
        var apps = RequireChild(steam, "apps");
        return apps;
    }

    private static Node RequireChild(Node parent, string key)
    {
        if (parent.Children is null)
            throw new InvalidDataException($"Steam localconfig.vdf '{parent.Key}' is not a block.");
        return FindOne(parent.Children, key)
            ?? throw new InvalidDataException($"Steam localconfig.vdf has no '{key}' block.");
    }

    private static Node? FindOne(IReadOnlyList<Node> nodes, string key)
    {
        var matches = nodes.Where(n => n.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1)
            throw new InvalidDataException($"Steam localconfig.vdf has duplicate '{key}' entries.");
        return matches.FirstOrDefault();
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                i = text.IndexOf('\n', i);
                if (i < 0) break;
                continue;
            }
            if (text[i] is '{' or '}')
            {
                tokens.Add(new Token(text[i].ToString(), i, ++i, false));
                continue;
            }
            if (text[i] != '"')
                throw new InvalidDataException($"Unexpected Steam VDF character at offset {i}.");
            var start = i++;
            var value = new StringBuilder();
            var closed = false;
            while (i < text.Length)
            {
                var c = text[i++];
                if (c == '"') { closed = true; break; }
                if (c == '\\' && i < text.Length)
                {
                    var escaped = text[i++];
                    value.Append(escaped switch
                    {
                        'n' => '\n', 'r' => '\r', 't' => '\t', '\\' => '\\', '"' => '"',
                        _ => '\\'
                    });
                    if (escaped is not ('n' or 'r' or 't' or '\\' or '"')) value.Append(escaped);
                }
                else value.Append(c);
            }
            if (!closed) throw new InvalidDataException("Unclosed string in Steam localconfig.vdf.");
            tokens.Add(new Token(value.ToString(), start, i, true));
        }
        return tokens;
    }

    private static List<Node> ParseNodes(IReadOnlyList<Token> tokens, ref int position, bool nested)
    {
        var result = new List<Node>();
        while (position < tokens.Count)
        {
            var key = tokens[position++];
            if (!key.IsString)
            {
                if (nested && key.Value == "}") return result;
                throw new InvalidDataException("Unexpected brace in Steam localconfig.vdf.");
            }
            if (position >= tokens.Count)
                throw new InvalidDataException("Steam VDF key has no value.");
            var value = tokens[position++];
            if (value.IsString)
                result.Add(new Node(key.Value, key.Start, value.Value, value.Start, value.End, -1, null));
            else if (value.Value == "{")
            {
                var children = ParseNodes(tokens, ref position, nested: true);
                var close = tokens[position - 1];
                if (close.Value != "}")
                    throw new InvalidDataException("Unclosed Steam VDF block.");
                result.Add(new Node(key.Value, key.Start, null, -1, -1, close.Start, children));
            }
            else throw new InvalidDataException("Unexpected Steam VDF block close.");
        }
        if (nested) throw new InvalidDataException("Unclosed Steam VDF block.");
        return result;
    }

    private static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private sealed record Token(string Value, int Start, int End, bool IsString);
    private sealed record Node(
        string Key, int KeyStart, string? Value, int ValueStart, int ValueEnd,
        int CloseBrace, List<Node>? Children);
}
