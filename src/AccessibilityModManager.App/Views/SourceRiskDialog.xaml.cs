using System.Windows;
using AccessibilityModManager.App.ViewModels;

namespace AccessibilityModManager.App.Views;

public partial class SourceRiskDialog : Window
{
    public bool UserAccepted { get; private set; }

    public SourceRiskDialog(SourceRiskDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;

        // Focus starts on the WARNING, not on a button. Cancel stays IsDefault and IsCancel, so a
        // stray Enter or Escape is still the safe answer — but a custom window does not guarantee
        // that unfocused static text is spoken when it opens, and a user could otherwise reach the
        // accept button without the warning ever being read. The warning is the only protection the
        // unsigned-source design has at this point, so it is what focus lands on.
        Loaded += (_, _) => RiskTextBox.Focus();
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        UserAccepted = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        UserAccepted = false;
        Close();
    }
}
