// SPDX-License-Identifier: GPL-3.0-or-later
using XIVLauncher.Core.Accounts;
using XIVLauncher.Core.Accounts.Secrets;
using XIVLauncher.Core.Configuration;

namespace XIVLauncher.Core;

public sealed class LauncherCredentials(AccountManager accounts, ILauncherConfig settings, ISecretProvider secrets)
{
    public XivAccount? Current => accounts.Accounts.Count == 1 ? accounts.Accounts[0]
        : accounts.Accounts.FirstOrDefault(account => account.Id == settings.CurrentAccountId);

    public string ReadPassword(XivAccount account) => account.SavePassword
        ? secrets.GetPassword(account.UserName) ?? "" : "";

    public void Save(string username, string password, bool otp, bool steam, bool trial, bool remember, bool automatic)
    {
        username = username.Trim().ToLowerInvariant();
        if (username.Length == 0) throw new InvalidOperationException("Enter your account username.");
        if (automatic && !remember) throw new InvalidOperationException("Remember password must be enabled for automatic login.");
        var id = $"{username}-{otp}-{steam}";
        var account = accounts.Accounts.FirstOrDefault(item => item.Id == id) ?? new XivAccount(username);
        // The provider is XIVLauncher's own keyring provider; no manager credential copy.
        if (remember)
        {
            if (password.Length == 0) throw new InvalidOperationException("Enter your password before saving it.");
            secrets.SavePassword(username, password);
        }
        else if (account.SavePassword)
            secrets.DeletePassword(username);
        account.SavePassword = remember;
        account.UseOtp = otp;
        account.UseSteamServiceAccount = steam;
        account.IsFreeTrial = trial;
        if (!accounts.Accounts.Contains(account)) accounts.Accounts.Add(account);
        accounts.Save();
        settings.CurrentAccountId = account.Id;
        settings.IsAutologin = automatic;
    }
}
