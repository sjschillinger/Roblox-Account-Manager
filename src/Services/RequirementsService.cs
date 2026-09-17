using Microsoft.Win32;

namespace RobloxAccountManager.Services;

public static class RequirementsService
{
    public static bool IsRobloxInstalled()
    {
        // A registered protocol handler alone means a launch has somewhere to go, even when the client
        // lives somewhere this app would not think to look.
        try { if (Registry.ClassesRoot.OpenSubKey("roblox-player") != null) return true; }
        catch { }

        // Otherwise look for the binary itself — including bootstrapper-managed installs.
        return RobloxInstallService.IsInstalled();
    }

    /// <summary>
    /// First-run check. The browser download prompt that used to follow is gone: signing in and
    /// opening accounts now work with the Edge or Chrome already on the PC, so nobody has to fetch a
    /// 540 MB browser before they can use the app.
    /// </summary>
    public static void CheckOnStartup()
    {
        if (!IsRobloxInstalled())
            DialogService.OfferDownload(L.T("Requirements.NoRoblox.Title"), L.T("Requirements.NoRoblox.Body"),
                "https://www.roblox.com/download");
    }
}
