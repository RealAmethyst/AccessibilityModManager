using System.Buffers.Binary;
using System.Text;

namespace AccessibilityModManager.Infrastructure.Detection;

/// <summary>
/// Reads the executable Steam itself launches for a game from Steam's binary app cache
/// (<c>appcache/appinfo.vdf</c>, formats 27 to 29). Only a single, unambiguous Windows default
/// launch entry is returned; anything else, including an unreadable cache, returns null.
/// </summary>
public static class SteamAppInfo
{
    private const uint Version27 = 0x07564427;
    private const uint Version28 = 0x07564428;
    private const uint Version29 = 0x07564429;
    private const long MaxFileBytes = 512L * 1024 * 1024;
    private const int MaxDepth = 32;

    public static string? LaunchExecutable(string steamRoot, string appId)
    {
        if (!uint.TryParse(appId, out var wanted) || wanted == 0) return null;
        var path = Path.Combine(steamRoot, "appcache", "appinfo.vdf");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxFileBytes) return null;
            return Find(File.ReadAllBytes(path), wanted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   ArgumentException or IndexOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    internal static string? Find(byte[] data, uint wanted)
    {
        var magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic is not (Version27 or Version28 or Version29)) return null;
        var offset = 8;
        string[]? strings = null;
        if (magic == Version29)
        {
            var table = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(8));
            if (table < 16 || table >= data.Length) return null;
            strings = ReadStringTable(data, (int)table);
            offset = 16;
        }
        var header = 4 + 4 + 8 + 20 + 4 + (magic == Version27 ? 0 : 20);
        while (offset + 8 <= data.Length)
        {
            var appId = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
            if (appId == 0) return null;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset + 4));
            if (size < header || offset + 8L + size > data.Length) return null;
            if (appId == wanted)
            {
                var position = offset + 8 + header;
                var root = ReadObject(data, ref position, strings, 0);
                return SingleWindowsLaunch(root);
            }
            offset += 8 + (int)size;
        }
        return null;
    }

    private static string? SingleWindowsLaunch(Dictionary<string, object> root)
    {
        if (Child(Child(Child(root, "appinfo"), "config"), "launch") is not { } launch) return null;
        var executables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in launch.Values.OfType<Dictionary<string, object>>())
        {
            if (entry.GetValueOrDefault("executable") is not string executable ||
                string.IsNullOrWhiteSpace(executable))
                continue;
            var type = entry.GetValueOrDefault("type") as string;
            var config = Child(entry, "config");
            var osList = config?.GetValueOrDefault("oslist") as string;
            if (type is not (null or "" or "default") ||
                config?.GetValueOrDefault("betakey") is string ||
                !string.IsNullOrEmpty(osList) &&
                !osList.Split(',').Any(os => os.Trim().Equals("windows", StringComparison.OrdinalIgnoreCase)))
                continue;
            executables.Add(executable.Replace('\\', '/').TrimStart('/'));
        }
        return executables.Count == 1 ? executables.Single() : null;
    }

    private static Dictionary<string, object>? Child(Dictionary<string, object>? parent, string key) =>
        parent?.GetValueOrDefault(key) as Dictionary<string, object>;

    private static string[] ReadStringTable(byte[] data, int offset)
    {
        var count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        if (count > data.Length) throw new FormatException("Invalid Steam app cache string table.");
        var strings = new string[count];
        var position = offset + 4;
        for (var index = 0; index < count; index++)
            strings[index] = ReadCString(data, ref position);
        return strings;
    }

    private static Dictionary<string, object> ReadObject(byte[] data, ref int position, string[]? strings, int depth)
    {
        if (depth > MaxDepth) throw new FormatException("Steam app cache nesting is too deep.");
        var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (true)
        {
            var type = data[position++];
            if (type == 0x08) return result;
            string key;
            if (strings is null)
            {
                key = ReadCString(data, ref position);
            }
            else
            {
                var index = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
                position += 4;
                if (index < 0 || index >= strings.Length)
                    throw new FormatException("Invalid Steam app cache key.");
                key = strings[index];
            }
            object value;
            switch (type)
            {
                case 0x00:
                    value = ReadObject(data, ref position, strings, depth + 1);
                    break;
                case 0x01:
                    value = ReadCString(data, ref position);
                    break;
                case 0x02 or 0x03 or 0x04 or 0x06:
                    value = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(position));
                    position += 4;
                    break;
                case 0x07 or 0x0A:
                    value = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(position));
                    position += 8;
                    break;
                default:
                    throw new FormatException($"Unsupported Steam app cache value type {type}.");
            }
            result[key] = value;
        }
    }

    private static string ReadCString(byte[] data, ref int position)
    {
        var end = Array.IndexOf(data, (byte)0, position);
        if (end < 0) throw new FormatException("Unterminated Steam app cache string.");
        var value = Encoding.UTF8.GetString(data, position, end - position);
        position = end + 1;
        return value;
    }
}
