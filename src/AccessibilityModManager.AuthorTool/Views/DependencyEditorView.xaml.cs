using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class DependencyEditorView : UserControl
{
    public DependencyEditorView()
    {
        InitializeComponent();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is DependencyItemViewModel dep)
            dep.RemoveSelf();
    }

    /// <summary>
    /// Lets the author compute the SHA256 of a downloaded loader artifact locally instead of
    /// hand-pasting the upstream hash. Drops the lowercase hex into the AutoInstallSha256
    /// field on the bound view-model.
    /// </summary>
    private void ComputeSha_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DependencyItemViewModel dep) return;

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select dependency artifact to hash",
            Filter = "All files (*.*)|*.*",
            Multiselect = false
        };
        var owner = Window.GetWindow(this);
        var ok = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
        if (ok != true) return;

        try
        {
            using var stream = File.OpenRead(dialog.FileName);
            var hash = SHA256.HashData(stream);
            dep.AutoInstallSha256 = System.Convert.ToHexStringLower(hash);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(owner ?? Application.Current?.MainWindow, ex.Message, "Hash failed",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Downloads the Fix download URL once, computes its SHA256, and writes the lowercase
    /// hex into the bound view-model. This is the same artifact the manager will fetch at
    /// install time, so the hash is guaranteed to match — no need for the author to track
    /// down the upstream's published hash. HTTPS is enforced.
    /// </summary>
    private async void FetchShaFromUrl_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not DependencyItemViewModel dep) return;
        var owner = Window.GetWindow(this);
        var url = dep.FixDownloadUrl?.Trim();

        if (string.IsNullOrWhiteSpace(url))
        {
            MessageBox.Show(owner ?? Application.Current?.MainWindow,
                "Fill in the Fix download URL first — that's the artifact the manager will download.",
                "URL required", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!url.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(owner ?? Application.Current?.MainWindow,
                "Only HTTPS URLs are allowed for dependency downloads.",
                "HTTPS required", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        FetchShaButton.IsEnabled = false;
        var oldContent = FetchShaButton.Content;
        FetchShaButton.Content = "Fetching...";
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // Short-lived HttpClient — the AuthorTool runs locally and one-shot allocs are
            // fine here. Stream the body straight into SHA256 so we never buffer the whole
            // artifact (loaders can be 100 MB+).
            using var http = new HttpClient();
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync();
            var hash = await SHA256.HashDataAsync(body);
            dep.AutoInstallSha256 = System.Convert.ToHexStringLower(hash);
        }
        catch (System.Exception ex)
        {
            MessageBox.Show(owner ?? Application.Current?.MainWindow,
                $"Couldn't fetch + hash the URL:\n\n{ex.Message}",
                "Fetch failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            FetchShaButton.IsEnabled = true;
            FetchShaButton.Content = oldContent;
            Mouse.OverrideCursor = null;
        }
    }
}
