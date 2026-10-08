using System.Text.Json.Nodes;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.Infrastructure.Services;

/// <summary>Edits Dalamud's typed JSON without deserializing publisher-controlled CLR types.</summary>
public static class XivLauncherSettings
{
    public sealed record Registration(string DllPath, string InternalName, string WorkingPluginId,
        bool HadDevMode, bool? PreviousDevMode);

    public static (string Json, Registration Registration) Install(string? json,
        string wineDllPath, XivLauncherConfig plugin)
    {
        var root = Parse(json);
        var registration = new Registration(wineDllPath, plugin.InternalName, plugin.WorkingPluginId,
            root.ContainsKey("DevMode"), root["DevMode"]?.GetValue<bool>());
        var (locations, settings, profiles) = Sections(root, create: true);
        if (locations.Any(n => Equal(n?["Path"], wineDllPath)) ||
            settings.Any(p => p.Key.Equals(wineDllPath, StringComparison.OrdinalIgnoreCase)) ||
            settings.Any(p => p.Key != "$type" && Equal(p.Value?["WorkingPluginId"], plugin.WorkingPluginId)) ||
            profiles.Any(n => Equal(n?["InternalName"], plugin.InternalName) || Equal(n?["WorkingPluginId"], plugin.WorkingPluginId)))
            throw new InvalidOperationException("This Dalamud plugin already has a registration. Remove or migrate that installation before installing it here.");
        root["DevMode"] = true;
        locations.Add(new JsonObject
        {
            ["$type"] = "Dalamud.Configuration.DevPluginLocationSettings, Dalamud",
            ["Path"] = wineDllPath, ["IsEnabled"] = true, ["Nickname"] = plugin.InternalName
        });
        settings[wineDllPath] = new JsonObject
        {
            ["$type"] = "Dalamud.Configuration.Internal.DevPluginSettings, Dalamud",
            ["StartOnBoot"] = true, ["NotifyForErrors"] = true, ["AutomaticReloading"] = false,
            ["WorkingPluginId"] = plugin.WorkingPluginId
        };
        profiles.Add(new JsonObject
        {
            ["$type"] = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud",
            ["InternalName"] = plugin.InternalName, ["WorkingPluginId"] = plugin.WorkingPluginId,
            ["IsEnabled"] = true
        });
        return (root.ToJsonString(Options), registration);
    }

    public static bool IsAbsent(string json, Registration registration)
    {
        var (locations, settings, profiles) = Sections(Parse(json), create: false);
        return !locations.Any(n => Equal(n?["Path"], registration.DllPath)) &&
            !settings.Any(p => p.Key.Equals(registration.DllPath, StringComparison.OrdinalIgnoreCase) ||
                p.Key != "$type" && Equal(p.Value?["WorkingPluginId"], registration.WorkingPluginId)) &&
            !profiles.Any(n => Equal(n?["InternalName"], registration.InternalName) || Equal(n?["WorkingPluginId"], registration.WorkingPluginId));
    }

    public static void Verify(string json, Registration registration)
    {
        var (locations, settings, profiles) = Sections(Parse(json), create: false);
        var location = locations.Where(n => Equal(n?["Path"], registration.DllPath)).ToArray();
        var setting = settings.Where(p => p.Key.Equals(registration.DllPath, StringComparison.OrdinalIgnoreCase)).ToArray();
        var profile = profiles.Where(n => Equal(n?["WorkingPluginId"], registration.WorkingPluginId)).ToArray();
        if (location.Length != 1 || setting.Length != 1 || profile.Length != 1 ||
            !Equal(setting[0].Value?["WorkingPluginId"], registration.WorkingPluginId) ||
            !Equal(profile[0]?["InternalName"], registration.InternalName))
            throw new InvalidOperationException("The managed Dalamud registration changed or is incomplete. It was left untouched.");
    }

    public static string Remove(string json, Registration registration)
    {
        Verify(json, registration);
        var root = Parse(json);
        var (locations, settings, profiles) = Sections(root, create: false);
        locations.Remove(locations.Single(n => Equal(n?["Path"], registration.DllPath)));
        settings.Remove(settings.Single(p => p.Key.Equals(registration.DllPath, StringComparison.OrdinalIgnoreCase)).Key);
        profiles.Remove(profiles.Single(n => Equal(n?["WorkingPluginId"], registration.WorkingPluginId)));
        if (locations.Count == 0 && root["DevMode"]?.GetValue<bool>() == true)
        {
            if (registration.HadDevMode) root["DevMode"] = registration.PreviousDevMode;
            else root.Remove("DevMode");
        }
        return root.ToJsonString(Options);
    }

    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { WriteIndented = true };
    private static bool Equal(JsonNode? node, string value) =>
        string.Equals(node?.GetValue<string>(), value, StringComparison.OrdinalIgnoreCase);
    private static JsonObject Parse(string? json) => json is null ? new JsonObject() :
        JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Dalamud configuration is not a JSON object.");

    private static (JsonArray Locations, JsonObject Settings, JsonArray Profiles) Sections(JsonObject root, bool create)
    {
        if (create)
        {
            root["DevPluginLoadLocations"] ??= TypedList("Dalamud.Configuration.DevPluginLocationSettings, Dalamud");
            root["DevPluginSettings"] ??= new JsonObject();
            root["DefaultProfile"] ??= new JsonObject
            {
                ["$type"] = "Dalamud.Plugin.Internal.Profiles.ProfileModelV1, Dalamud",
                ["Plugins"] = TypedList("Dalamud.Plugin.Internal.Profiles.ProfileModelV1+ProfileModelV1Plugin, Dalamud")
            };
        }
        if (root["DevPluginLoadLocations"]?["$values"] is not JsonArray locations ||
            root["DevPluginSettings"] is not JsonObject settings ||
            root["DefaultProfile"]?["Plugins"]?["$values"] is not JsonArray profiles)
            throw new InvalidDataException("Dalamud configuration has an unsupported plugin or profile layout.");
        return (locations, settings, profiles);
    }

    private static JsonObject TypedList(string element) => new()
    {
        ["$type"] = $"System.Collections.Generic.List`1[[{element}]], System.Private.CoreLib",
        ["$values"] = new JsonArray()
    };
}
