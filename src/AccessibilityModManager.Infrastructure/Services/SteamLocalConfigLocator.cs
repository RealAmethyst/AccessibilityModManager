namespace AccessibilityModManager.Infrastructure.Services;

public sealed record SteamAccountConfig(string AccountId, string ConfigPath)
{
    public override string ToString() => "Steam account " + AccountId;
}

/// <summary>Finds Steam account setting files without reading their contents.</summary>
public static class SteamLocalConfigLocator
{
    public static IReadOnlyList<SteamAccountConfig> Find(string steamRoot)
    {
        var userdata = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userdata)) return [];
        return Directory.EnumerateDirectories(userdata)
            .Select(path => new SteamAccountConfig(Path.GetFileName(path),
                Path.Combine(path, "config", "localconfig.vdf")))
            .Where(account => account.AccountId != "0" &&
                              account.AccountId.All(char.IsAsciiDigit) &&
                              File.Exists(account.ConfigPath))
            .OrderBy(account => account.AccountId, StringComparer.Ordinal)
            .ToArray();
    }

    public static SteamAccountConfig RequireSingle(string steamRoot)
    {
        var accounts = Find(steamRoot);
        return accounts.Count switch
        {
            1 => accounts[0],
            0 => throw new InvalidOperationException("No Steam account settings file was found."),
            _ => throw new InvalidOperationException(
                "Several Steam accounts have settings here. Select the account to configure: " +
                string.Join(", ", accounts.Select(account => account.AccountId)))
        };
    }
}
