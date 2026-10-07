using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AccessibilityModManager.AuthorTool.ViewModels;
using AccessibilityModManager.Authoring.ViewModels;
using AccessibilityModManager.Core.Models;

namespace AccessibilityModManager.LinuxAuthorTool;

internal static class AuthorPages
{
    private static readonly Thickness Gap = new(0, 0, 0, 8);

    public static async Task Dialog(Window owner, string title, object vm, Control body,
        Action<Action>? setClose, double width = 620, double height = 610,
        Action<Window>? onCreate = null)
    {
        var window = new Window { Title = title, Width = width, Height = height,
            MinWidth = 480, MinHeight = 360, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = vm, Content = new ScrollViewer { Content = body } };
        AutomationProperties.SetName(window, title);
        setClose?.Invoke(window.Close);
        onCreate?.Invoke(window);
        window.Opened += (_, _) => Dispatcher.UIThread.Post(() => FirstControl(body)?.Focus(),
            DispatcherPriority.ApplicationIdle);
        await window.ShowDialog(owner);
    }

    private static Control? FirstControl(Control root)
    {
        if (root is TextBox or ListBox or Button or ComboBox) return root;
        if (root is Panel panel)
            return panel.Children.Select(FirstControl).FirstOrDefault(item => item is not null);
        if (root is ContentControl content && content.Content is Control child) return FirstControl(child);
        return null;
    }

    public static Control Picker(ProjectPickerViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Plugin Index Author"));
        root.Children.Add(new TextBlock { Text = "Recent projects" });
        var recent = List("Recent projects", "RecentProjects", "SelectedRecent", item =>
            item is RecentProjectItem project ? project.DisplayName + ", " + project.Subtitle : "Project");
        recent.MinHeight = 120; recent.MaxHeight = 220;
        recent.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Enter && vm.OpenRecentCommand.CanExecute(null))
            { vm.OpenRecentCommand.Execute(null); e.Handled = true; }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
        recent.ContainerPrepared += (_, e) =>
        {
            if (e.Index == recent.SelectedIndex)
                Dispatcher.UIThread.Post(() => e.Container.Focus(), DispatcherPriority.ApplicationIdle);
        };
        root.Children.Add(recent);
        var openLocal = ActionButton("Open local folder", "OpenLocalFolderCommand");
        root.Tag = vm.RecentProjects.Count > 0 ? recent : openLocal;
        root.Children.Add(Row(ActionButton("Open selected project", "OpenRecentCommand"),
            openLocal,
            ActionButton("Find GitHub repos", "ListGitHubReposCommand")));
        var repos = List("Your GitHub repos", "GitHubRepos", "SelectedGitHubRepo",
            item => item is GitHubRepoItem repo ? repo.NameWithOwner : "Repository");
        repos.MinHeight = 110; repos.MaxHeight = 180;
        root.Children.Add(repos);
        root.Children.Add(Row(ActionButton("Use selected repo", "UseGitHubRepoCommand"),
            ActionButton("Back from repos", "BackFromGitHubListCommand")));
        root.Children.Add(Row(ActionButton("Server settings", "EditServerUploadSettingsCommand"),
            ActionButton("Registry admin", "OpenAdminCommand")));
        root.Children.Add(Status("StatusMessage"));
        return root;
    }

    public static Control Editor(IndexEditorViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Plugin: " + vm.PluginId));
        root.Children.Add(new TextBlock { Text = vm.ProjectPath, TextWrapping = TextWrapping.Wrap });
        root.Children.Add(Row(ActionButton("Author info", "EditAuthorInfoCommand"),
            ActionButton("Server settings", "EditServerUploadSettingsCommand"),
            ActionButton("Catalog signing", "EditCatalogSigningCommand"),
            ActionButton("Patreon sign in or out", "SignInOrOutOfPatreonCommand")));
        root.Children.Add(Row(ActionButton("Publish destination", "ChangePublishDestinationCommand"),
            ActionButton("Save", "SaveCommand"), ActionButton("Publish index", "PublishIndexCommand"),
            ActionButton("Close project", "CloseProjectCommand")));
        root.Children.Add(Status("RegistryStatusText"));
        var gameList = List("Games in this index", "Games", "SelectedGame",
            item => item is GameItemViewModel game ? game.DisplayName + ", " + game.GameId : "Game");
        gameList.MinHeight = 100; gameList.MaxHeight = 180;
        root.Children.Add(gameList);
        root.Children.Add(Row(ActionButton("Add game", "AddGameCommand"),
            ActionButton("Remove selected game", "RemoveSelectedGameCommand")));
        var gameContent = new ContentControl();
        void ShowGame() => gameContent.Content = vm.SelectedGame is { } game ? Game(game, vm) :
            new TextBlock { Text = "Select a game to edit it." };
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.SelectedGame)) ShowGame();
        };
        ShowGame();
        root.Children.Add(gameContent);
        root.Children.Add(Status("StatusMessage"));
        root.Children.Add(Row(ActionButton("Check my server", "CheckServerCommand"),
            ActionButton("Clear publish lock", "BreakPublishLockCommand")));
        return root;
    }

    private static Control Game(GameItemViewModel game, IndexEditorViewModel parent)
    {
        var tabs = new TabControl { DataContext = game };
        var general = Stack();
        foreach (var (label, property) in new[] {
            ("Game ID", "GameId"), ("Display name", "DisplayName"), ("Mod name", "ModName"),
            ("Description", "Description"), ("Steam App ID", "SteamAppId"),
            ("Windows executable", "ExeName"), ("Native Linux executable", "LinuxExeName"),
            ("GitHub repo", "PerGameSourceRepo") })
            general.Children.Add(Field(label, property, property == "Description"));
        tabs.Items.Add(new TabItem { Header = "General", Content = general });

        var detection = Stack();
        foreach (var (label, property) in new[] {
            ("Registry hive", "RegistryHive"), ("Registry key", "RegistryKey"),
            ("Registry value", "RegistryValue"), ("ASCII junction name", "JunctionName"),
            ("ASCII junction reason", "JunctionReason") })
            detection.Children.Add(Field(label, property));
        detection.Children.Add(Check("Probe registry subfolders", "RegistryProbeSubfolders"));
        tabs.Items.Add(new TabItem { Header = "Detection", Content = detection });

        var releases = Stack();
        releases.Children.Add(new TextBlock { Text = "Releases" });
        var releaseList = List("Releases", "Releases", "SelectedRelease", item =>
            item is ModRelease release ? $"{release.Version}, {release.Channel}, {ReleaseTarget.Normalize(release.TargetPlatform)}" : "Release");
        releaseList.MinHeight = 100; releaseList.MaxHeight = 180;
        releases.Children.Add(releaseList);
        var releaseActions = Row();
        releaseActions.Children.Add(ButtonFor(parent, "Add release", "AddReleaseCommand"));
        releaseActions.Children.Add(ButtonFor(parent, "Edit selected release", "EditSelectedReleaseCommand"));
        releaseActions.Children.Add(ButtonFor(parent, "Remove selected release", "RemoveSelectedReleaseCommand"));
        releases.Children.Add(releaseActions);
        tabs.Items.Add(new TabItem { Header = "Releases", Content = releases });

        var filters = Stack();
        filters.Children.Add(new TextBlock { Text = "Tags" });
        filters.Children.Add(CheckList(game.TagSelections, item => item.Label));
        filters.Children.Add(new TextBlock { Text = "Languages" });
        filters.Children.Add(CheckList(game.LanguageSelections, item => item.Label));
        var customTag = new TextBox { PlaceholderText = "Custom tag", MinWidth = 160 };
        AutomationProperties.SetName(customTag, "Custom tag");
        var addTag = new Button { Content = "Add custom tag" };
        addTag.Click += (_, _) => { game.AddCustomTag(customTag.Text ?? ""); customTag.Text = ""; };
        filters.Children.Add(Row(customTag, addTag));
        tabs.Items.Add(new TabItem { Header = "Filters", Content = filters });

        var deps = Stack();
        var preset = new ComboBox { ItemsSource = DependencyPresets.All, MinWidth = 180 };
        AutomationProperties.SetName(preset, "Dependency preset");
        var addPreset = new Button { Content = "Add preset" };
        addPreset.Click += (_, _) =>
        {
            if (preset.SelectedItem is AccessibilityModManager.AuthorTool.ViewModels.DependencyPreset selected) game.AddDependencyFromPreset(selected);
            else NativeDialogs.Info("Dependency preset", "Choose a preset first.");
        };
        var addCustom = new Button { Content = "Add custom dependency" };
        addCustom.Click += (_, _) => game.AddCustomDependency();
        var removeDependency = new Button { Content = "Remove selected dependency" };
        removeDependency.Click += (_, _) =>
        {
            if (game.SelectedDependency is { } selected) game.RemoveDependency(selected);
        };
        deps.Children.Add(Row(preset, addPreset, addCustom, removeDependency));
        var depList = List("Dependencies", "Dependencies", "SelectedDependency", item =>
            item is DependencyItemViewModel dep ? dep.Id + ", " + dep.Type : "Dependency");
        depList.MinHeight = 100; depList.MaxHeight = 180;
        deps.Children.Add(depList);
        var depContent = new ContentControl();
        void ShowDependency() => depContent.Content = game.SelectedDependency is { } dep ? Dependency(dep) : null;
        game.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(game.SelectedDependency)) ShowDependency(); };
        ShowDependency();
        deps.Children.Add(depContent);
        tabs.Items.Add(new TabItem { Header = "Dependencies", Content = deps });

        var scripts = Stack();
        scripts.Children.Add(Script(game.PreInstallScript));
        scripts.Children.Add(Script(game.PostInstallScript));
        scripts.Children.Add(Script(game.PostUninstallScript));
        tabs.Items.Add(new TabItem { Header = "Lifecycle scripts", Content = scripts });
        return tabs;
    }

    private static Control Script(LifecycleScriptEditorViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(new TextBlock { Text = vm.HookLabel, FontWeight = FontWeight.SemiBold });
        root.Children.Add(Check("Enable " + vm.HookLabel, "IsEnabled"));
        root.Children.Add(Status("PickedFileDisplay"));
        var browse = new Button { Content = "Choose " + vm.HookLabel + " script" };
        browse.Click += (_, _) =>
        {
            var path = NativeDialogs.File("Choose " + vm.HookLabel + " script", "", vm.AbsoluteSourcePath);
            if (path is not null) vm.ApplyPickedFile(path);
        };
        root.Children.Add(browse);
        root.Children.Add(Field("Package script path", "Executable"));
        root.Children.Add(Field("What the script does", "What"));
        root.Children.Add(Field("Why it runs", "Why"));
        root.Children.Add(Field("What it modifies", "Modifies"));
        root.Children.Add(Check("Needs administrator rights on Windows", "NeedsAdmin"));
        root.Children.Add(Check("Failure aborts install", "FailureFatal"));
        root.Children.Add(Check("Install script into game folder", "InstallToGameFolder"));
        root.Children.Add(Check("Run on updates", "RunOnUpdate"));
        root.Children.Add(Check("Run from game folder", "RunFromGameFolder"));
        return root;
    }

    private static Control Dependency(DependencyItemViewModel dep)
    {
        var root = Stack(); root.DataContext = dep;
        foreach (var (label, property) in new[] {
            ("ID", "Id"), ("Type", "Type"), ("Minimum version", "MinVersion"),
            ("Check file path", "CheckFilePath"), ("Check registry key", "CheckRegistryKey"),
            ("Check registry value", "CheckRegistryValue"), ("Fix download URL", "FixDownloadUrl"),
            ("Auto install kind", "AutoInstallKind"), ("Auto install SHA-256", "AutoInstallSha256"),
            ("Auto install target folder", "AutoInstallTargetDir"),
            ("Auto install target filename", "AutoInstallTargetFileName") })
            root.Children.Add(Field(label, property));
        root.Children.Add(Check("Required", "Required"));
        root.Children.Add(Check("Automatic legacy platform compatibility", "AutomaticPlatforms"));
        root.Children.Add(new TextBlock { Text = "Legacy compatibility applies to Windows and Proton. Turn it off to choose platforms, including native Linux.", TextWrapping = TextWrapping.Wrap });
        var platforms = Row(Check("Windows", "TargetWindows"), Check("Proton", "TargetProton"), Check("Native Linux", "TargetLinux"));
        platforms.Bind(Control.IsEnabledProperty, new Binding("AutomaticPlatforms") { Converter = Avalonia.Data.Converters.BoolConverters.Not });
        root.Children.Add(platforms);
        root.Children.Add(Check("Automatic installation", "AutoInstallEnabled"));
        root.Children.Add(Check("This dependency is the game", "IsGameInstaller"));
        return root;
    }

    public static Control AddGame(AddGameDialogViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Add game"));
        foreach (var (label, property) in new[] {
            ("Display name", "DisplayName"), ("Game ID", "GameId"), ("Mod name", "ModName"),
            ("Description", "Description"), ("Steam App ID", "SteamAppId"),
            ("Windows executable", "ExeName"), ("Native Linux executable", "LinuxExeName"),
            ("GitHub repo", "GitHubRepo") })
            root.Children.Add(Field(label, property, property == "Description"));
        root.Children.Add(new TextBlock { Text = "Tags" });
        root.Children.Add(CheckList(vm.TagSelections, item => item.Label));
        root.Children.Add(new TextBlock { Text = "Languages" });
        root.Children.Add(CheckList(vm.LanguageSelections, item => item.Label));
        root.Children.Add(Row(ActionButton("Save game", "SaveCommand"), ActionButton("Cancel", "CancelCommand")));
        return root;
    }

    public static Control AuthorInfo(AuthorInfoDialogViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Author info for " + vm.PluginId));
        foreach (var (label, property) in new[] {
            ("Display name", "DisplayName"), ("Bio", "Bio"), ("Website URL", "WebsiteUrl"),
            ("Discord URL", "DiscordUrl"), ("Patreon URL", "PatreonUrl"),
            ("GitHub URL", "GitHubUrl"), ("Donation URL", "DonationUrl") })
            root.Children.Add(Field(label, property, property == "Bio"));
        root.Children.Add(Row(ActionButton("Save", "SaveCommand"), ActionButton("Cancel", "CancelCommand")));
        return root;
    }

    public static Control ServerSettings(ServerUploadSettingsViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Download server settings"));
        foreach (var (label, property) in new[] {
            ("Host", "Host"), ("Host key fingerprint", "HostKeyFingerprint"),
            ("User", "User"), ("Port", "Port"), ("Private key path", "PrivateKeyPath"),
            ("Key passphrase", "KeyPassphrase"), ("Remote release path", "RemoteBasePath"),
            ("Remote catalog root", "RemoteCatalogRoot"), ("Public base URL", "PublicBaseUrl") })
            root.Children.Add(Field(label, property, password: property == "KeyPassphrase"));
        root.Children.Add(Status("StatusMessage"));
        root.Children.Add(Row(ActionButton("Test connection", "TestConnectionCommand"),
            ActionButton("Save", "SaveCommand"), ActionButton("Cancel", "CancelCommand")));
        return root;
    }

    public static Control Build(BuildPackageDialogViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Build package for " + vm.GameDisplayName));
        root.Children.Add(Field("Source folder", "SourceFolder"));
        root.Children.Add(ActionButton("Choose source folder", "PickSourceCommand"));
        root.Children.Add(Field("Version", "Version"));
        root.Children.Add(Choice("Target platform", "TargetPlatform", new[] {
            ReleaseTarget.Windows, ReleaseTarget.Proton, ReleaseTarget.Linux, ReleaseTarget.WindowsLinux }));
        var proton = Stack();
        foreach (var (label, property) in new[] {
            ("Steam App ID", "SteamAppId"), ("Game executable", "GameExecutable"),
            ("Launcher path", "LauncherPath"), ("Prism bridge directory", "BridgeDirectory"),
            ("Wine DLL overrides", "WineDllOverrides"), ("Wine proxy paths", "WineDllProxyPaths"),
            ("Windows Desktop Runtime version", "WindowsDesktopRuntimeVersion"),
            ("Windows Desktop Runtime SHA-512", "WindowsDesktopRuntimeSha512"),
            ("Reloaded II root", "ReloadedRoot"), ("Reloaded II mod ID", "ReloadedModId") })
            proton.Children.Add(Field(label, property));
        proton.Children.Add(Choice("Launch mode", "LaunchMode", new[] { "direct", "replaceExecutable" }));
        proton.Bind(Control.IsVisibleProperty, new Binding("IsProton"));
        root.Children.Add(proton);
        var entries = List("Files in source folder", "DetectedEntries", null,
            item => item is DetectedEntry entry ? entry.Name + ", " + entry.Description : "File");
        entries.MinHeight = 80; entries.MaxHeight = 150;
        root.Children.Add(entries);
        root.Children.Add(Status("StatusMessage"));
        root.Children.Add(Row(ActionButton("Build", "BuildCommand"), ActionButton("Cancel", "CancelCommand")));
        return root;
    }

    public static Control Release(ReleaseDialogViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading(vm.DialogTitle));
        root.Children.Add(Status("SubtitleText"));
        root.Children.Add(Check("Patreon-only release", "IsPatreonGated"));
        var patron = Stack();
        patron.Children.Add(ActionButton("Sign in to Patreon", "SignInToPatreonInDialogCommand"));
        patron.Children.Add(Status("PatreonSignedInAsText"));
        patron.Children.Add(Status("PatreonStatusText"));
        patron.Children.Add(new TextBlock { Text = "Tiers that grant access" });
        var tiers = new ItemsControl();
        tiers.Bind(ItemsControl.ItemsSourceProperty, new Binding("PatreonTierSelections"));
        tiers.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<PatreonTierSelection>((tier, _) =>
        {
            var box = new CheckBox { Content = tier?.DisplayLabel, DataContext = tier };
            box.Bind(CheckBox.IsCheckedProperty, new Binding("IsSelected") { Mode = BindingMode.TwoWay });
            return box;
        });
        patron.Children.Add(tiers);
        patron.Children.Add(Field("Patreon post URL", "PatreonPostUrl"));
        patron.Children.Add(ActionButton("Validate Patreon post", "ValidatePatreonPostCommand"));
        patron.Children.Add(Choice("Post attachment", "SelectedPatreonAttachmentFileName",
            vm.PatreonAttachmentFileNames));
        patron.Bind(Control.IsVisibleProperty, new Binding("IsPatreonGated"));
        root.Children.Add(patron);
        root.Children.Add(Choice("Host public release on", "HostingDestination",
            new[] { ReleaseDialogViewModel.HostingGitHub, ReleaseDialogViewModel.HostingMyServer }));
        foreach (var (label, property) in new[] {
            ("Version", "Version"), ("Channel", "Channel"), ("GitHub repo", "SourceRepo"),
            ("Tag", "TagName"), ("Local ZIP", "LocalZipPath"), ("Asset filename", "AssetFileName"),
            ("SHA-256", "Sha256"), ("Package URL", "PackageUrl"),
            ("Changelog URL", "ChangelogUrl"), ("Release notes", "ReleaseNotes") })
            root.Children.Add(Field(label, property, property == "ReleaseNotes"));
        root.Children.Add(Row(ActionButton("Choose ZIP", "PickZipCommand"),
            ActionButton("Build package", "BuildPackageCommand")));
        root.Children.Add(Status("StatusMessage"));
        root.Children.Add(Row(ActionButton("Upload and save", "UploadAndSaveCommand"),
            ActionButton("Save without upload", "SaveWithoutUploadCommand"),
            ActionButton("Cancel", "CancelCommand")));
        return root;
    }

    public static Control Registry(RegistryAdminViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Registry admin"));
        root.Children.Add(Field("Registry repository path", "RegistryRepoPath"));
        root.Children.Add(Row(ActionButton("Refresh repository", "RefreshRepoCommand"),
            ActionButton("Choose private key", "PickPrivateKeyCommand"),
            ActionButton("Use local signing key", "UseLocalSigningKeyCommand")));
        root.Children.Add(Field("Private key path", "PrivateKeyPath"));
        root.Children.Add(Field("Registry JSON", "RegistryJsonContent", multiline: true));
        root.Children.Add(Row(ActionButton("Save JSON", "SaveJsonCommand"),
            ActionButton("Publish registry release", "PublishReleaseCommand"),
            ActionButton("Commit and push", "CommitAndPushCommand"),
            ActionButton("Back", "GoBackCommand")));
        root.Children.Add(Status("StatusMessage"));
        return root;
    }

    public static Control Signing(ClaimSigningViewModel vm)
    {
        var root = Stack(); root.DataContext = vm;
        root.Children.Add(Heading("Catalog signing for " + vm.PluginId));
        root.Children.Add(new TextBlock { Text = vm.KeySummary, TextWrapping = TextWrapping.Wrap });
        var keyId = new TextBox { Text = vm.SuggestedKeyId };
        AutomationProperties.SetName(keyId, "New key ID");
        root.Children.Add(keyId);
        var pass = new TextBox { PasswordChar = '●', PlaceholderText = "Passphrase" };
        AutomationProperties.SetName(pass, "Signing key passphrase");
        var confirm = new TextBox { PasswordChar = '●', PlaceholderText = "Confirm passphrase" };
        AutomationProperties.SetName(confirm, "Confirm signing key passphrase");
        root.Children.Add(pass); root.Children.Add(confirm);
        var actions = Row();
        var create = new Button { Content = "Create signing key", IsEnabled = vm.CanCreate };
        create.Click += (_, _) => { vm.CreateKey(keyId.Text ?? "", (pass.Text ?? "").ToCharArray(),
            (confirm.Text ?? "").ToCharArray()); pass.Text = confirm.Text = ""; };
        var backup = new Button { Content = "Export encrypted backup" };
        backup.Click += (_, _) => { vm.ExportBackup((pass.Text ?? "").ToCharArray(),
            (confirm.Text ?? "").ToCharArray()); pass.Text = confirm.Text = ""; };
        var restore = new Button { Content = "Restore encrypted backup" };
        restore.Click += (_, _) => { vm.ImportBackup((pass.Text ?? "").ToCharArray());
            pass.Text = confirm.Text = ""; };
        actions.Children.Add(create); actions.Children.Add(backup); actions.Children.Add(restore);
        root.Children.Add(actions); root.Children.Add(Status("StatusMessage"));
        return root;
    }

    private static StackPanel Stack() => new() { Spacing = 5, Margin = new Thickness(16) };
    private static StackPanel Row(params Control[] controls)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = Gap };
        foreach (var control in controls) row.Children.Add(control);
        return row;
    }
    private static TextBlock Heading(string text) => new() { Text = text, FontSize = 22,
        FontWeight = FontWeight.Bold, Margin = Gap };
    private static Control Field(string label, string property, bool multiline = false, bool password = false)
    {
        var row = Stack(); row.Margin = Gap;
        row.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { MinWidth = 300, AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiline ? 95 : 0 };
        if (password) box.PasswordChar = '●';
        AutomationProperties.SetName(box, label);
        box.Bind(TextBox.TextProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        row.Children.Add(box);
        return row;
    }
    private static CheckBox Check(string label, string property)
    {
        var box = new CheckBox { Content = label, Margin = Gap };
        AutomationProperties.SetName(box, label);
        box.Bind(CheckBox.IsCheckedProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        return box;
    }
    private static Control Choice(string label, string property, IEnumerable<string> options)
    {
        var row = Stack(); row.Margin = Gap;
        row.Children.Add(new TextBlock { Text = label });
        var box = new ComboBox { ItemsSource = options, MinWidth = 180 };
        AutomationProperties.SetName(box, label);
        box.Bind(ComboBox.SelectedItemProperty, new Binding(property) { Mode = BindingMode.TwoWay });
        box.SelectionChanged += (_, _) => AutomationProperties.SetName(box,
            box.SelectedItem is null ? label : label + ", " + box.SelectedItem);
        row.Children.Add(box); return row;
    }
    private static Button ActionButton(string label, string command)
    {
        var button = new Button { Content = label, Margin = Gap };
        AutomationProperties.SetName(button, label);
        button.Bind(Button.CommandProperty, new Binding(command));
        return button;
    }
    private static Button ButtonFor(object vm, string label, string command)
    {
        var button = ActionButton(label, command); button.DataContext = vm; return button;
    }
    private static ListBox List(string name, string source, string? selected,
        Func<object?, string> describe)
    {
        var list = new ListBox { Margin = Gap };
        AutomationProperties.SetName(list, name);
        list.Bind(ItemsControl.ItemsSourceProperty, new Binding(source));
        if (selected is not null)
            list.Bind(ListBox.SelectedItemProperty, new Binding(selected) { Mode = BindingMode.TwoWay });
        list.ContainerPrepared += (_, e) =>
        {
            if (e.Index >= 0 && e.Index < list.ItemCount)
                AutomationProperties.SetName(e.Container, describe(list.Items[e.Index]));
        };
        return list;
    }
    private static TextBlock Status(string property)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap, Focusable = true, Margin = Gap };
        AutomationProperties.SetLiveSetting(block, AutomationLiveSetting.Polite);
        block.Bind(TextBlock.TextProperty, new Binding(property));
        return block;
    }

    private static ItemsControl CheckList<T>(IEnumerable<T> items, Func<T, string> label)
        where T : class
    {
        var list = new ItemsControl { ItemsSource = items };
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<T>((item, _) =>
        {
            var box = new CheckBox { Content = item is null ? "" : label(item), DataContext = item };
            box.Bind(CheckBox.IsCheckedProperty, new Binding("IsSelected") { Mode = BindingMode.TwoWay });
            return box;
        });
        return list;
    }
}
