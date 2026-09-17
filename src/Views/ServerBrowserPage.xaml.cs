using System.Windows.Controls;
using System.Windows.Input;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views;

public partial class ServerBrowserPage : UserControl
{
    public ServerBrowserPage()
    {
        InitializeComponent();
    }

    private void PlaceBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ServerBrowserViewModel vm) return;
        if (vm.RefreshCommand.CanExecute(null)) vm.RefreshCommand.Execute(null);
        e.Handled = true;
    }

    private void ServerList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ServerBrowserViewModel vm && vm.Selected != null && vm.JoinCommand.CanExecute(null))
            vm.JoinCommand.Execute(null);
    }
}
