using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using AccessibilityModManager.AuthorTool.ViewModels;
using Microsoft.Win32;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class ServerUploadSettingsDialog : Window
{
    private readonly ServerUploadSettingsViewModel _vm;

    public ServerUploadSettingsDialog(ServerUploadSettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        _vm = viewModel;
        viewModel.CloseDialog = () => Close();

        // Seed the password box with whatever's already saved (PasswordBox doesn't bind
        // its content because plaintext binding would defeat its memory protection).
        PassphraseBox.Password = viewModel.KeyPassphrase ?? string.Empty;
        PassphraseBox.PasswordChanged += (_, _) => _vm.KeyPassphrase = PassphraseBox.Password;

        Loaded += (_, _) => HostBox.Focus();
    }

    private void BrowsePrivateKey_Click(object sender, RoutedEventArgs e)
    {
        // Default to the user's .ssh folder if it exists, otherwise their profile root.
        // The picker filter shows "All files" by default because OpenSSH private keys are
        // extension-less (id_ed25519) and tend to get hidden when filters apply.
        var sshDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        var initialDir = Directory.Exists(sshDir)
            ? sshDir
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var dialog = new OpenFileDialog
        {
            Title = "Pick your SSH private key file",
            Filter = "All files (*.*)|*.*|OpenSSH keys (id_*)|id_*|PuTTY keys (*.ppk)|*.ppk",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false,
            InitialDirectory = initialDir
        };
        if (dialog.ShowDialog(this) == true)
        {
            _vm.PrivateKeyPath = dialog.FileName;
        }
    }
}
