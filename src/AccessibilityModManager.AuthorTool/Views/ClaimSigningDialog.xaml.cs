using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

/// <summary>
/// Passphrases live in the PasswordBoxes and reach the view model as <c>char[]</c>, never as
/// strings — the same handling the registry signing screen uses. The view model zeroes them.
/// </summary>
public partial class ClaimSigningDialog : Window
{
    private readonly ClaimSigningViewModel _vm;

    public ClaimSigningDialog(ClaimSigningViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        // Land on the first thing to fill in, or on the backup passphrase when the key already
        // exists — which is the state this screen is most often reopened in.
        Loaded += (_, _) =>
        {
            // Restore-only means publishing found the registry naming a key this machine lacks.
            // The only useful action is restoring, so that is where the caret starts.
            if (vm.RestoreOnly) ImportPassphraseBox.Focus();
            else if (vm.HasKey) ExportPassphraseBox.Focus();
            else KeyIdBox.Focus();
        };
    }

    private void CreateKey_Click(object sender, RoutedEventArgs e)
    {
        // Read both first, then hand them over, and clear the boxes however it ends. Clearing
        // after an unguarded call leaves the characters in the control if anything throws.
        var passphrase = Read(CreatePassphraseBox.SecurePassword);
        var confirmation = Read(CreateConfirmBox.SecurePassword);
        try
        {
            _vm.CreateKey(KeyIdBox.Text, passphrase, confirmation);
        }
        finally
        {
            Array.Clear(passphrase);
            Array.Clear(confirmation);
            CreatePassphraseBox.Clear();
            CreateConfirmBox.Clear();
        }
    }

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var passphrase = Read(ExportPassphraseBox.SecurePassword);
        var confirmation = Read(ExportConfirmBox.SecurePassword);
        try
        {
            _vm.ExportBackup(passphrase, confirmation);
        }
        finally
        {
            Array.Clear(passphrase);
            Array.Clear(confirmation);
            ExportPassphraseBox.Clear();
            ExportConfirmBox.Clear();
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var passphrase = Read(ImportPassphraseBox.SecurePassword);
        try
        {
            _vm.ImportBackup(passphrase);
        }
        finally
        {
            Array.Clear(passphrase);
            ImportPassphraseBox.Clear();
        }
    }

    /// <summary>
    /// Copies a <see cref="SecureString"/> out as characters, freeing the unmanaged copy it has to
    /// make on the way. Same routine as the registry screen's.
    /// </summary>
    private static char[] Read(SecureString secure)
    {
        using (secure)
        {
            if (secure.Length == 0) return [];

            var bstr = IntPtr.Zero;
            try
            {
                bstr = Marshal.SecureStringToBSTR(secure);
                var chars = new char[secure.Length];
                for (var i = 0; i < secure.Length; i++)
                    chars[i] = (char)Marshal.ReadInt16(bstr, i * 2);
                return chars;
            }
            finally
            {
                if (bstr != IntPtr.Zero) Marshal.ZeroFreeBSTR(bstr);
            }
        }
    }
}
