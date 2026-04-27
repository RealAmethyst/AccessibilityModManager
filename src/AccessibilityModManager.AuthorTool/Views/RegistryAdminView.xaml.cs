using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class RegistryAdminView : UserControl
{
    public RegistryAdminView()
    {
        InitializeComponent();
        // Issues list is the primary work surface — open requests waiting to be processed.
        Loaded += (_, _) => IssuesList.Focus();
    }

    private void Sign_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not RegistryAdminViewModel vm) return;

        var secure = PasswordBox.SecurePassword;
        var chars = SecureStringToChars(secure);
        try
        {
            vm.Sign(chars);
        }
        finally
        {
            secure.Dispose();
        }
    }

    private static char[] SecureStringToChars(SecureString secure)
    {
        if (secure.Length == 0) return Array.Empty<char>();

        IntPtr bstr = IntPtr.Zero;
        try
        {
            bstr = Marshal.SecureStringToBSTR(secure);
            var chars = new char[secure.Length];
            for (int i = 0; i < secure.Length; i++)
                chars[i] = (char)Marshal.ReadInt16(bstr, i * 2);
            return chars;
        }
        finally
        {
            if (bstr != IntPtr.Zero) Marshal.ZeroFreeBSTR(bstr);
        }
    }
}
