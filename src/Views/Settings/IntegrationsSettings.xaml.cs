using System.ComponentModel;
using System.Windows.Controls;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views.Settings;

public partial class IntegrationsSettings : UserControl
{
    public IntegrationsSettings()
    {
        InitializeComponent();
        // The token is shown as dots (it is a password for everything the API can do); copying it
        // goes through the secret clipboard path instead of a selectable text field.
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnVmChanged;
            if (e.NewValue is INotifyPropertyChanged now) now.PropertyChanged += OnVmChanged;
            ShowToken();
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SettingsViewModel.WebApiToken) or "" or null) ShowToken();
    }

    private void ShowToken()
    {
        if (DataContext is SettingsViewModel vm) TokenBox.Password = vm.WebApiToken;
    }
}
