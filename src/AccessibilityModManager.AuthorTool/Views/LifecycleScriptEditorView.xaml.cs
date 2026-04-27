using System.IO;
using System.Windows;
using System.Windows.Controls;
using AccessibilityModManager.AuthorTool.ViewModels;
using Microsoft.Win32;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class LifecycleScriptEditorView : UserControl
{
    public LifecycleScriptEditorView()
    {
        InitializeComponent();
    }

    private void BrowseScript_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not LifecycleScriptEditorViewModel vm) return;

        // Default to the folder of the previously-picked file when one exists, so re-picking
        // for an updated build doesn't make the author hunt around again.
        string? initial = null;
        if (!string.IsNullOrEmpty(vm.AbsoluteSourcePath))
        {
            var dir = Path.GetDirectoryName(vm.AbsoluteSourcePath);
            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) initial = dir;
        }

        var dialog = new OpenFileDialog
        {
            Title = $"Pick the {vm.HookLabel} script file",
            Filter = "Script files (*.exe;*.ps1;*.cmd;*.bat)|*.exe;*.ps1;*.cmd;*.bat|All files (*.*)|*.*",
            CheckFileExists = true,
            CheckPathExists = true,
            Multiselect = false
        };
        if (initial != null) dialog.InitialDirectory = initial;

        var owner = Window.GetWindow(this);
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return;

        var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
        if (ext is not ".exe" and not ".ps1" and not ".cmd" and not ".bat")
        {
            MessageBox.Show(owner!,
                $"'{Path.GetFileName(dialog.FileName)}' has extension '{ext}'. Allowed extensions for lifecycle scripts are .exe, .ps1, .cmd, and .bat.",
                "Unsupported script extension",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        vm.ApplyPickedFile(dialog.FileName);
    }
}
