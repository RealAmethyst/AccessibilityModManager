#!/usr/bin/env python3
"""Apply the accessible frontend to the pinned XIVLauncher source, preserving its backend."""
from pathlib import Path
import shutil
import subprocess
import sys

root = Path(sys.argv[1])
frontend = Path(__file__).parent / "frontend"
assert subprocess.check_output(["git", "-C", str(root), "rev-parse", "HEAD"], text=True).strip() == "06c32980a75447fe0f15cdac30f8931f70d687e9"
assert subprocess.check_output(["git", "-C", str(root / "lib/FFXIVQuickLauncher"), "rev-parse", "HEAD"], text=True).strip() == "40ed6e93e7eb73e1c18f4d4871e05f32ab5fd2c6"
core = root / "src/XIVLauncher.Core"

def replace(text, old, new):
    if text.count(old) != 1:
        raise ValueError("Pinned source anchor changed: " + old[:100])
    return text.replace(old, new)

# Keep the original initialization, platform configuration, updater and keyring.
# The only window/event loop is the accessible frontend; SDL/ImGui is never initialized.
path = core / "Program.cs"
text = path.read_text(encoding="utf-8-sig")
text = replace(text, "    private static void Main(string[] args)\n    {", "    [STAThread]\n    private static void Main(string[] args) => AccessibleFrontend.Run(args);\n\n    internal static LauncherApp Initialize(string[] args, bool settingsOnly)\n    {")
text = replace(text, "        Dictionary<uint, string> apps = [];", "        if (settingsOnly)\n            return launcherApp = new LauncherApp(storage, null, null);\n\n        Dictionary<uint, string> apps = [];")
start = text.index('        Log.Debug("Creating SDL3 devices...");')
end = text.index("    public static void CreateCompatToolsInstance()", start)
text = text[:start] + "        var client = Net.LauncherClientConfig.GetAsync().GetAwaiter().GetResult();\n        return launcherApp = new LauncherApp(storage, client.frontierUrl, client.cutOffBootver);\n    }\n\n" + text[end:]
start = text.index("    public static void ShowWindow()")
end = text.index("    private static ISecretProvider GetSecretProvider", start)
text = text[:start] + "    public static void ShowWindow() => AccessibleFrontend.Show();\n\n    public static void HideWindow() => AccessibleFrontend.Hide();\n\n" + text[end:]
path.write_text(text)

# Keep the actual login, boot/game patching, Wine, Dalamud and game-lifetime methods.
# Only the original UI construction and account-persistence adapter are replaced.
path = core / "Components/MainPage/MainPage.cs"
text = path.read_text()
start = text.index("    private readonly LoginFrame loginFrame;")
end = text.index("    public async Task<bool> Login(", start)
text = text[:start] + "    public bool IsLoggingIn { get; private set; }\n\n    public MainPage(LauncherApp app) : base(app) { }\n\n" + text[end:]
start = text.index("    private void PersistAccount(")
end = text.index("    private async Task<bool> HandleBootCheck()", start)
text = text[:start] + "    private void PersistAccount(string username, string password, bool isOtp, bool isSteam, bool isFreeTrial)\n        => App.Credentials.Save(username, password, isOtp, isSteam, isFreeTrial, App.RememberPassword, App.AutoLogin);\n\n" + text[end:]
text = replace(text, "        if (loginResult.State == Launcher.LoginState.NoService)", "        if (loginResult is null) return false;\n\n        if (loginResult.State == Launcher.LoginState.NoService)")
text = replace(text, "        IGameRunner runner;", '        if (!dalamudOk)\n            throw new InvalidOperationException("Dalamud is not ready for this game version. The game was not started because the accessibility mod needs Dalamud.");\n\n        IGameRunner runner;')
text = replace(text, "            runner = new UnixGameRunner(Program.CompatibilityTools, dalamudLauncher, dalamudOk);", "            WineSpeechSetup.Prepare(Program.CompatibilityTools, App.Storage);\n\n            runner = new UnixGameRunner(Program.CompatibilityTools, dalamudLauncher, dalamudOk);")
path.write_text(text)

# Exclude the original UI components, retaining the upstream controller and base classes.
path = core / "XIVLauncher.Core.csproj"
text = path.read_text(encoding="utf-8-sig")
text = replace(text, "</Project>", '''    <ItemGroup>
        <Compile Remove="Components/**/*.cs" />
        <Compile Include="Components/Component.cs;Components/Page.cs;Components/MainPage/MainPage.cs;Components/MainPage/LoginAction.cs;Components/LoadingPage/DalamudOverlayInfoProxy.cs" />
        <PackageReference Include="Avalonia" Version="12.1.3" />
        <PackageReference Include="Avalonia.Desktop" Version="12.1.3" />
        <PackageReference Include="Avalonia.Themes.Fluent" Version="12.1.3" />
    </ItemGroup>
</Project>''')
path.write_text(text)
shutil.copy2(frontend / "LauncherApp.cs", core / "LauncherApp.cs")
destination = core / "Accessible"
destination.mkdir()
for path in frontend.glob("*.cs"):
    if path.name != "LauncherApp.cs":
        shutil.copy2(path, destination / path.name)
