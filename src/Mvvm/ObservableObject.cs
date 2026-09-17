using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace RobloxAccountManager.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raises PropertyChanged on the UI thread. Background services (presence polling, the crash
    /// watchdog, cookie checks) update models from the thread pool; plain bindings tolerate that, but
    /// sorted and filtered list views re-shape themselves inside the notification and must not do so
    /// off the UI thread.
    /// </summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        var handler = PropertyChanged;
        if (handler == null) return;

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
            handler(this, new PropertyChangedEventArgs(name));
        else
            dispatcher.BeginInvoke(new Action(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name))));
    }

    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
