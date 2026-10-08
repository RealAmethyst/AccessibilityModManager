using Config.Net;
using XIVLauncher.Core;
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Accounts.Secrets;
using XIVLauncher.Core.Configuration;
using XIVLauncher.Core.Configuration.Parsers;

public sealed class LauncherCredentialTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amm-xl-credentials-" + Guid.NewGuid().ToString("N"));
    private readonly FakeSecrets secrets = new();
    private readonly ILauncherConfig config;
    private readonly AccountManager accounts;
    private readonly LauncherCredentials credentials;

    public LauncherCredentialTests()
    {
        Directory.CreateDirectory(root);
        config = new ConfigurationBuilder<ILauncherConfig>().UseIniFile(Path.Combine(root, "launcher.ini"))
            .UseTypeParser(new DirectoryInfoParser()).UseTypeParser(new AddonListParser()).Build();
        accounts = new AccountManager(new FileInfo(Path.Combine(root, "accounts.json")));
        credentials = new LauncherCredentials(accounts, config, secrets);
    }

    [Fact]
    public void RememberedLoginUsesNativeAccountAndSeparateKeyring()
    {
        credentials.Save(" Example ", "test-secret", true, true, false, true, true);
        var saved = Assert.Single(accounts.Accounts);
        Assert.Equal("example-True-True", config.CurrentAccountId);
        Assert.True(config.IsAutologin);
        Assert.True(saved.UseOtp);
        Assert.Equal("test-secret", credentials.ReadPassword(saved));
        var json = File.ReadAllText(Path.Combine(root, "accounts.json"));
        Assert.DoesNotContain("test-secret", json);
        var reloaded = new AccountManager(new FileInfo(Path.Combine(root, "accounts.json")));
        Assert.Equal(saved.Id, Assert.Single(reloaded.Accounts).Id);
    }

    [Fact]
    public void UnrememberedLoginDoesNotSavePasswordOrEnableAutomaticLogin()
    {
        credentials.Save("example", "session-secret", false, true, false, false, false);
        Assert.Empty(secrets.Values);
        Assert.False(Assert.Single(accounts.Accounts).SavePassword);
        Assert.False(config.IsAutologin);
        Assert.DoesNotContain("session-secret", File.ReadAllText(Path.Combine(root, "accounts.json")));
    }

    [Fact]
    public void UpdatingOneAccountPreservesOtherAccountsAndCharacterMetadata()
    {
        credentials.Save("first", "one", false, true, false, true, true);
        accounts.Accounts[0].ChosenCharacterName = "Existing character";
        credentials.Save("second", "two", false, true, false, true, false);
        credentials.Save("first", "changed", false, true, true, true, true);
        Assert.Equal(2, accounts.Accounts.Count);
        Assert.Equal("Existing character", credentials.Current!.ChosenCharacterName);
        Assert.True(credentials.Current.IsFreeTrial);
        Assert.Equal("two", secrets.Values["second"]);
        Assert.Equal("changed", secrets.Values["first"]);
    }

    [Fact]
    public void TurningOffRememberRemovesThePreviouslySavedPassword()
    {
        credentials.Save("example", "stored", false, true, false, true, true);
        credentials.Save("example", "session", false, true, false, false, false);
        Assert.Empty(secrets.Values);
        Assert.False(credentials.Current!.SavePassword);
        Assert.False(config.IsAutologin);
    }

    [Fact]
    public void AutomaticLoginWithoutRememberingIsRejectedWithoutWriting()
    {
        Assert.Throws<InvalidOperationException>(() => credentials.Save("example", "secret", false, true, false, false, true));
        Assert.Empty(accounts.Accounts);
        Assert.Empty(secrets.Values);
    }

    [Fact]
    public void FailedKeyringWriteDoesNotPublishAutomaticLoginOrAccount()
    {
        secrets.FailWrites = true;
        Assert.Throws<IOException>(() => credentials.Save("example", "secret", false, true, false, true, true));
        Assert.Empty(accounts.Accounts);
        Assert.NotEqual(true, config.IsAutologin);
    }

    private sealed class FakeSecrets : ISecretProvider
    {
        public Dictionary<string, string> Values { get; } = [];
        public bool FailWrites { get; set; }
        public string? GetPassword(string accountName) => Values.GetValueOrDefault(accountName);
        public void SavePassword(string accountName, string password)
        {
            if (FailWrites) throw new IOException("Keyring unavailable.");
            Values[accountName] = password;
        }
        public void DeletePassword(string accountName) => Values.Remove(accountName);
    }

    public void Dispose() => Directory.Delete(root, true);
}
