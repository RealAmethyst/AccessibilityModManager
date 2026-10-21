using System.Buffers.Binary;
using System.Text;
using AccessibilityModManager.Infrastructure.Detection;

namespace AccessibilityModManager.PortableTests;

public sealed class SteamAppInfoTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-appinfo-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ReadsTheSingleWindowsDefaultLaunchExecutable(bool stringTable)
    {
        Write(stringTable,
            (10, Launches(("Other.exe", null, "windows"))),
            (20, Launches(
                (@"exe\Game.exe", null, null),
                ("game.sh", "default", "linux"),
                (@"exe\Game.exe", "default", "windows"),
                ("Editor.exe", "option1", "windows"))));

        Assert.Equal("exe/Game.exe", SteamAppInfo.LaunchExecutable(root, "20"));
        Assert.Equal("Other.exe", SteamAppInfo.LaunchExecutable(root, "10"));
        Assert.Null(SteamAppInfo.LaunchExecutable(root, "30"));
    }

    [Fact]
    public void AmbiguousOrDamagedCacheGivesNoExecutable()
    {
        Write(true, (20, Launches(("A.exe", "default", "windows"), ("B.exe", null, "windows"))));
        Assert.Null(SteamAppInfo.LaunchExecutable(root, "20"));

        var path = Path.Combine(root, "appcache", "appinfo.vdf");
        var bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);
        Assert.Null(SteamAppInfo.LaunchExecutable(root, "20"));
    }

    [Fact]
    public void ReadsAnInstalledSteamClientCache()
    {
        // AMM_STEAM_APPINFO=<steam root>|<app id>|<expected executable>
        var check = Environment.GetEnvironmentVariable("AMM_STEAM_APPINFO")?.Split('|');
        if (check is not { Length: 3 }) return;
        Assert.Equal(check[2], SteamAppInfo.LaunchExecutable(check[0], check[1]));
    }

    private static Node Launches(params (string Exe, string? Type, string? Os)[] entries) =>
        Branch("appinfo", Branch("config", new Node("launch", null, entries.Select((entry, index) =>
        {
            var fields = new List<Node> { Leaf("executable", entry.Exe) };
            if (entry.Type is not null) fields.Add(Leaf("type", entry.Type));
            if (entry.Os is not null) fields.Add(Branch("config", Leaf("oslist", entry.Os)));
            return new Node(index.ToString(), null, fields);
        }).ToList())));

    private sealed record Node(string Key, string? Text, List<Node>? Children);

    private static Node Leaf(string key, string text) => new(key, text, null);

    private static Node Branch(string key, params Node[] children) => new(key, null, [.. children]);

    /// <summary>Writes appinfo.vdf version 29 (key string table) or 28 (inline keys).</summary>
    private void Write(bool stringTable, params (uint AppId, Node Root)[] apps)
    {
        var strings = new List<string>();
        var body = new MemoryStream();
        foreach (var (appId, node) in apps)
        {
            var kv = new MemoryStream();
            WriteNode(kv, node, stringTable, strings);
            kv.WriteByte(0x08);
            var header = new byte[4 + 4 + 8 + 20 + 4 + 20];
            WriteUInt(body, appId);
            WriteUInt(body, (uint)(header.Length + kv.Length));
            body.Write(header);
            body.Write(kv.ToArray());
        }
        WriteUInt(body, 0);

        var file = new MemoryStream();
        WriteUInt(file, stringTable ? 0x07564429u : 0x07564428u);
        WriteUInt(file, 1);
        if (stringTable)
        {
            var tableOffset = 16 + body.Length;
            var offset = new byte[8];
            BinaryPrimitives.WriteInt64LittleEndian(offset, tableOffset);
            file.Write(offset);
        }
        file.Write(body.ToArray());
        if (stringTable)
        {
            WriteUInt(file, (uint)strings.Count);
            foreach (var value in strings) WriteCString(file, value);
        }
        Directory.CreateDirectory(Path.Combine(root, "appcache"));
        File.WriteAllBytes(Path.Combine(root, "appcache", "appinfo.vdf"), file.ToArray());
    }

    private static void WriteNode(Stream stream, Node node, bool stringTable, List<string> strings)
    {
        stream.WriteByte(node.Text is not null ? (byte)0x01 : (byte)0x00);
        if (stringTable)
        {
            var index = strings.IndexOf(node.Key);
            if (index < 0)
            {
                index = strings.Count;
                strings.Add(node.Key);
            }
            WriteUInt(stream, (uint)index);
        }
        else
        {
            WriteCString(stream, node.Key);
        }
        if (node.Text is not null)
        {
            WriteCString(stream, node.Text);
            return;
        }
        foreach (var child in node.Children!) WriteNode(stream, child, stringTable, strings);
        stream.WriteByte(0x08);
    }

    private static void WriteUInt(Stream stream, uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteCString(Stream stream, string value)
    {
        stream.Write(Encoding.UTF8.GetBytes(value));
        stream.WriteByte(0);
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
