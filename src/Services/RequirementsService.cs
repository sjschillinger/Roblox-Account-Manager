using System.IO;
using Microsoft.Win32;

namespace RobloxAccountManager.Services;

public static class RequirementsService
{
    public static bool IsRobloxInstalled()
    {
        // Protocol handler registered? That alone means a launch has somewhere to go, even when
        // the client lives somewhere this app wouldn't think to look.
        try { if (Registry.ClassesRoot.OpenSubKey("roblox-player") != null) return true; }
        catch { }

        // Otherwise look for the binary itself — including bootstrapper-managed installs, which
        // keep their versions outside the stock Roblox folder.
        return RobloxInstallService.IsInstalled();
    }

    /// <summary>Runs the startup requirement checks and offers downloads for anything missing.</summary>
    public static void CheckOnStartup()
    {
        if (!IsRobloxInstalled())
        {
            DialogService.OfferDownload("Roblox not found",
                "The Roblox player doesn't appear to be installed. You'll need it to launch accounts into games. Download Roblox now?",
                "https://www.roblox.com/download");
        }

        if (!ChromiumService.IsInstalled && !SettingsService.Current.SkipChromiumPrompt)
        {
            if (DialogService.Confirm("Set up browser support",
                "\"Open in browser\" uses CloakBrowser — a private, portable stealth Chromium (downloaded once, ~540 MB, kept separate from Edge/Chrome and your normal browsing). Download it now? You can also do this later from any account."))
            {
                DialogService.ShowChromiumDownload();
            }
            else
            {
                SettingsService.Current.SkipChromiumPrompt = true;
                SettingsService.Save();
            }
        }
    }
}
