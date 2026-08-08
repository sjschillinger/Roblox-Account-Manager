using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using RobloxAccountManager.ViewModels;

namespace RobloxAccountManager.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    // ---- Global hotkey capture --------------------------------------------------
    // Recording a chord is a view concern: it needs raw key events, which the view-model
    // has no business seeing. The button arms itself on click, swallows the next chord,
    // and hands the finished (modifiers, virtual-key) pair back to the row.

    private HotkeyRow? _capturing;

    private void HotkeyCapture_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not HotkeyRow row) return;
        _capturing?.CancelCapture();
        _capturing = row;
        row.Capturing = true;
        Keyboard.Focus(fe as IInputElement);
    }

    private void HotkeyCapture_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not HotkeyRow row) return;
        if (!row.Capturing) return;

        e.Handled = true;   // never let a recorded chord also act as a normal keypress

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { row.CancelCapture(); _capturing = null; return; }

        // A modifier on its own is not a chord — keep waiting for the real key.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        // Win32 MOD_* bitmask: Alt=1, Ctrl=2, Shift=4, Win=8.
        uint modifiers = 0;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Alt)) modifiers |= 1;
        if (mods.HasFlag(ModifierKeys.Control)) modifiers |= 2;
        if (mods.HasFlag(ModifierKeys.Shift)) modifiers |= 4;
        if (mods.HasFlag(ModifierKeys.Windows)) modifiers |= 8;

        // A bare key would steal that key from every other program on the machine.
        if (modifiers == 0)
        {
            if (DataContext is SettingsViewModel vm)
                vm.SetStatusHint("A global hotkey needs at least one modifier (Ctrl, Alt, Shift or Win).");
            return;
        }

        row.SetChord(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key));
        _capturing = null;
    }

    private void HotkeyCapture_LostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Clicking elsewhere abandons the recording rather than leaving the row armed.
        if (sender is FrameworkElement { DataContext: HotkeyRow row } && row.Capturing)
        {
            row.CancelCapture();
            if (ReferenceEquals(_capturing, row)) _capturing = null;
        }
    }
}
