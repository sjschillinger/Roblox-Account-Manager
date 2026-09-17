using System.Windows;
using System.Windows.Controls;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views.Settings;

public partial class NetworkSettings : UserControl
{
    private bool _loading;

    public NetworkSettings()
    {
        InitializeComponent();
        // PasswordBox.Password is deliberately not bindable; fill it once the view-model arrives.
        DataContextChanged += (_, _) => LoadPassword();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) LoadPassword(); };
    }

    private void LoadPassword()
    {
        if (DataContext is not SettingsViewModel vm || ProxyPasswordBox.Password == vm.ProxyPassword) return;
        _loading = true;
        ProxyPasswordBox.Password = vm.ProxyPassword;
        _loading = false;
    }

    private void ProxyPassword_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading || DataContext is not SettingsViewModel vm) return;
        vm.ProxyPassword = ProxyPasswordBox.Password;
    }
}
