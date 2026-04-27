using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using AccessibilityModManager.AuthorTool.ViewModels;

namespace AccessibilityModManager.AuthorTool.Views;

public partial class ProjectPickerView : UserControl
{
    public ProjectPickerView()
    {
        InitializeComponent();
        // Registry admin entry point — visible only in REGISTRY_ADMIN builds.
#if REGISTRY_ADMIN
        AdminButton.Visibility = Visibility.Visible;
#endif

        // Land focus on the recent-projects list if there's anything in it; otherwise on the
        // first action button so Tab progresses naturally.
        Loaded += (_, _) =>
        {
            if (RecentList.Items.Count > 0) RecentList.Focus();
            else RecentList.Focus(); // empty list still works as a focus target with the screen reader announcing "list, empty"
        };
    }

    private void RecentList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProjectPickerViewModel vm && vm.OpenRecentCommand.CanExecute(null))
            vm.OpenRecentCommand.Execute(null);
    }

    private void GitHubReposList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProjectPickerViewModel vm && vm.UseGitHubRepoCommand.CanExecute(null))
            vm.UseGitHubRepoCommand.Execute(null);
    }
}
