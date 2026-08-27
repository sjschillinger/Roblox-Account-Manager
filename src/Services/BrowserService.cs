using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using RobloxAccountManager.Models;

namespace RobloxAccountManager.Services;

/// <summary>
/// Opens an account in a real browser, logged in. The private CloakBrowser build is launched with a
/// per-account profile and remote-debugging enabled; the .ROBLOSECURITY cookie is injected over the
/// DevTools protocol, then the tab is navigated to roblox.com — landing signed in.
/// </summary>
public static class BrowserService
{
    /// <summary>Public profile page in the user's default browser (not logged in).</summary>
    public static void OpenProfile(Account acc)
    {
        if (acc.UserId <= 0) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://www.roblox.com/users/{acc.UserId}/profile") { UseShellExecute = true });
        }
        catch { }
    }

    public record OpenResult(bool Success, string Message);

    /// <summary>Launches the private CloakBrowser build signed in as this account. When
    /// <paramref name="injectJs"/> is set, the snippet is evaluated in the page once it has settled
    /// (the "Inject" power-tool — e.g. a bookmarklet run in the authenticated session).</summary>
    public static async Task<OpenResult> OpenLoggedInAsync(Account acc, string? injectJs = null)
    {
        if (string.IsNullOrEmpty(acc.Cookie))
            return new(false, "This account has no cookie.");

        if (!ChromiumService.IsInstalled)
            return new(false, "no-chromium");

        try
        {
            string profileDir = Paths.InData(Path.Combine("browser", acc.UserId > 0 ? acc.UserId.ToString() : "acct"));
            Directory.CreateDirectory(profileDir);

            int port = FreePort();
            var psi = new ProcessStartInfo(ChromiumService.ChromePath)
            {
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{profileDir}\" --remote-debugging-port={port} "
                          + "--no-first-run --no-default-browser-check --new-window about:blank"
            };
            var proc = Process.Start(psi);

            // Privacy: the profile must never outlive the browser session. The moment the
            // browser closes, the whole profile folder (cookie DB, site storage, cache) is
            // wiped so nothing readable stays on disk.
            if (proc != null)
            {
                try
                {
                    proc.EnableRaisingEvents = true;
                    proc.Exited += (_, _) => { WipeProfileWithRetry(profileDir); proc.Dispose(); };
                }
                catch { /* exit hook is best-effort; startup/exit cleanup still covers it */ }
            }

            string? wsUrl = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(15));
            if (wsUrl == null)
                return new(false, "CloakBrowser started but the debugger didn't respond in time.");

            await InjectCookieAndNavigateAsync(wsUrl, acc.Cookie, injectJs);
            string what = string.IsNullOrWhiteSpace(injectJs) ? "Opened" : "Injected script into";
            return new(true, $"{what} {acc.DisplayNameOrUser} in CloakBrowser.");
        }
        catch (Exception ex)
        {
            return new(false, $"Couldn't open CloakBrowser: {ex.Message}");
        }
    }

    /// <summary>Result of a browser sign-in: the captured session cookie, or why it did not happen.</summary>
    public sealed record LoginCapture(bool Success, string Message, string? Cookie);

    /// <summary>
    /// Opens the real Roblox login page in a throw-away CloakBrowser profile and waits for the
    /// user to finish signing in, then reads the resulting <c>.ROBLOSECURITY</c> cookie straight
    /// out of the browser over the DevTools protocol.
    ///
    /// This is the fallback whenever <see cref="RobloxAuthService"/> cannot complete a login on its
    /// own — a captcha, a device confirmation, a security question — because those can only be
    /// answered by a human in a real browser. The password is typed into Roblox's own page and
    /// never passes through this application at all.
    ///
    /// The profile is created fresh for this sign-in and wiped when the window closes, so the
    /// session never stays readable on disk.
    /// </summary>
    public static async Task<LoginCapture> CaptureLoginCookieAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!ChromiumService.IsInstalled) return new(false, "no-chromium", null);

        string profileDir = Paths.InData(Path.Combine("browser", "login-" + Guid.NewGuid().ToString("N")));
        Process? proc = null;
        ClientWebSocket? socket = null;

        try
        {
            Directory.CreateDirectory(profileDir);
            int port = FreePort();

            var psi = new ProcessStartInfo(ChromiumService.ChromePath)
            {
                UseShellExecute = false,
                Arguments = $"--user-data-dir=\"{profileDir}\" --remote-debugging-port={port} "
                          + "--no-first-run --no-default-browser-check --new-window https://www.roblox.com/login"
            };
            proc = Process.Start(psi);
            progress?.Report("Opening the Roblox sign-in page…");

            // The browser-level endpoint is used rather than a page target: it survives every
            // navigation the login flow performs and emits almost no events, so a cookie poll is
            // a clean request/response.
            string? wsUrl = await WaitForBrowserSocketAsync(port, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            bool pageTarget = false;
            if (wsUrl == null)
            {
                wsUrl = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                pageTarget = wsUrl != null;
            }
            if (wsUrl == null)
                return new(false, "The sign-in window opened but its debugger did not respond.", null);

            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(wsUrl), ct).ConfigureAwait(false);

            int id = 0;
            string method = pageTarget ? "Network.getAllCookies" : "Storage.getCookies";
            if (pageTarget) (await CallAsync(socket, ++id, "Network.enable", new { }, ct).ConfigureAwait(false))?.Dispose();

            progress?.Report("Waiting for you to sign in…");

            var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(10);
            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();

                if (proc != null && proc.HasExited)
                    return new(false, "The sign-in window was closed before the login finished.", null);

                var (cookie, unsupported) = await TryReadSessionCookieAsync(socket, ++id, method, ct).ConfigureAwait(false);
                if (cookie != null)
                {
                    progress?.Report("Signed in — checking the account…");
                    return new(true, "Signed in.", cookie);
                }

                // Some Chromium builds do not expose Storage.getCookies on the browser target.
                // Switch to a page target's Network domain once, then keep polling.
                if (unsupported && !pageTarget)
                {
                    string? pageWs = await WaitForPageSocketAsync(port, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    if (pageWs == null)
                        return new(false, "The sign-in window did not expose a readable page.", null);

                    try { socket.Dispose(); } catch { }
                    socket = new ClientWebSocket();
                    await socket.ConnectAsync(new Uri(pageWs), ct).ConfigureAwait(false);
                    pageTarget = true;
                    method = "Network.getAllCookies";
                    (await CallAsync(socket, ++id, "Network.enable", new { }, ct).ConfigureAwait(false))?.Dispose();
                    continue;
                }

                await Task.Delay(1200, ct).ConfigureAwait(false);
            }

            return new(false, "Timed out waiting for the sign-in to finish.", null);
        }
        catch (OperationCanceledException)
        {
            return new(false, "Sign-in cancelled.", null);
        }
        catch (Exception ex)
        {
            return new(false, $"Browser sign-in failed: {ex.Message}", null);
        }
        finally
        {
            try { socket?.Dispose(); } catch { }
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            try { proc?.Dispose(); } catch { }
            WipeProfileWithRetry(profileDir);
        }
    }

    /// <summary>
    /// One cookie poll. Returns the session cookie once it exists; <c>unsupported</c> says the
    /// CDP method itself was refused, which is the signal to switch targets rather than keep
    /// asking a question this browser will never answer.
    /// </summary>
    private static async Task<(string? cookie, bool unsupported)> TryReadSessionCookieAsync(
        ClientWebSocket socket, int id, string method, CancellationToken ct)
    {
        using var doc = await CallAsync(socket, id, method, new { }, ct).ConfigureAwait(false);
        if (doc == null) return (null, false);

        var root = doc.RootElement;
        if (root.TryGetProperty("error", out _)) return (null, true);
        if (!root.TryGetProperty("result", out var result)) return (null, false);
        if (!result.TryGetProperty("cookies", out var cookies) || cookies.ValueKind != JsonValueKind.Array)
            return (null, false);

        foreach (var c in cookies.EnumerateArray())
        {
            string name = c.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            if (!string.Equals(name, ".ROBLOSECURITY", StringComparison.Ordinal)) continue;

            string value = c.TryGetProperty("value", out var v) ? v.GetString() ?? "" : "";
            // A logged-out placeholder has no warning prefix; only a real session does.
            if (value.Contains("WARNING", StringComparison.Ordinal)) return (value, false);
        }

        return (null, false);
    }

    /// <summary>
    /// Deletes leftover app-browser profiles under data/browser. Runs at app start and exit so
    /// no cookie/site data from a previous session stays readable on disk (crash leftovers and
    /// profiles written by older versions that still used persistent cookies). A profile whose
    /// browser is still running keeps its files locked — it is skipped and caught next start.
    /// </summary>
    public static void CleanupLeftoverProfiles()
    {
        try
        {
            string root = Paths.InData("browser");
            if (!Directory.Exists(root)) return;
            foreach (string dir in Directory.GetDirectories(root))
            {
                try { Directory.Delete(dir, true); }
                catch { WipeSensitiveFiles(dir); }
            }
        }
        catch { }
    }

    private static void WipeProfileWithRetry(string dir)
    {
        _ = Task.Run(async () =>
        {
            // The browser can hold file locks for a moment after its main process exits.
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    if (!Directory.Exists(dir)) return;
                    Directory.Delete(dir, true);
                    return;
                }
                catch { await Task.Delay(500); }
            }
            WipeSensitiveFiles(dir); // last resort: at least nothing credential-bearing stays
        });
    }

    /// <summary>Best-effort wipe of everything credential-bearing inside a (possibly locked) profile.</summary>
    private static void WipeSensitiveFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;

            string[] sensitive = { "Cookies", "Login Data", "Web Data", "History",
                                   "Network Persistent State", "TransportSecurity", "Trust Tokens" };
            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                string name = Path.GetFileName(file);
                if (sensitive.Any(s => name.StartsWith(s, StringComparison.OrdinalIgnoreCase)))
                {
                    try { File.Delete(file); } catch { }
                }
            }

            foreach (string sub in new[] { "Sessions", "Session Storage", "Local Storage", "IndexedDB" })
            {
                foreach (string d in Directory.EnumerateDirectories(dir, sub, SearchOption.AllDirectories).ToList())
                {
                    try { Directory.Delete(d, true); } catch { }
                }
            }
        }
        catch { }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<string?> WaitForPageSocketAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json");
                using var doc = JsonDocument.Parse(json);
                foreach (var target in doc.RootElement.EnumerateArray())
                {
                    if (target.TryGetProperty("type", out var ty) && ty.GetString() == "page" &&
                        target.TryGetProperty("webSocketDebuggerUrl", out var ws))
                        return ws.GetString();
                }
            }
            catch { }
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>
    /// Browser-level DevTools endpoint. Unlike a page socket it is not tied to a tab, so it
    /// survives every navigation and reload the login flow goes through.
    /// </summary>
    private static async Task<string?> WaitForBrowserSocketAsync(int port, TimeSpan timeout)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                string json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version");
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("webSocketDebuggerUrl", out var ws))
                {
                    string? url = ws.GetString();
                    if (!string.IsNullOrEmpty(url)) return url;
                }
            }
            catch { }
            await Task.Delay(250);
        }
        return null;
    }

    /// <summary>
    /// Sends one DevTools command and waits for the reply with the matching id, skipping the
    /// protocol events that arrive in between. Returns null if nothing answered in time — the
    /// caller simply polls again.
    /// </summary>
    private static async Task<JsonDocument?> CallAsync(ClientWebSocket socket, int id, string method,
        object @params, CancellationToken ct)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params });
        var outgoing = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(outgoing, WebSocketMessageType.Text, true, ct);

        var buffer = new byte[32 * 1024];
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (DateTime.UtcNow < deadline && socket.State == WebSocketState.Open)
        {
            var sb = new StringBuilder();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            JsonDocument doc;
            try { doc = JsonDocument.Parse(sb.ToString()); }
            catch { continue; }

            if (doc.RootElement.TryGetProperty("id", out var replyId)
                && replyId.TryGetInt32(out int got) && got == id)
                return doc;

            doc.Dispose();   // an unrelated protocol event
        }

        return null;
    }

    private static async Task InjectCookieAndNavigateAsync(string wsUrl, string cookie, string? injectJs = null)
    {
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri(wsUrl), CancellationToken.None);

        await SendAsync(socket, 1, "Network.enable", new { });
        // No "expires" on purpose: that makes it a session cookie, which lives in browser
        // memory only and dies with the window — it is never persisted into the profile's
        // cookie database where it would sit readable on disk.
        await SendAsync(socket, 2, "Network.setCookie", new
        {
            name = ".ROBLOSECURITY",
            value = cookie,
            domain = ".roblox.com",
            path = "/",
            secure = true,
            httpOnly = true
        });
        await SendAsync(socket, 3, "Page.navigate", new { url = "https://www.roblox.com/home" });

        if (!string.IsNullOrWhiteSpace(injectJs))
        {
            // Let the page load before running the snippet, then evaluate it in the top frame.
            await SendAsync(socket, 4, "Runtime.enable", new { });
            await Task.Delay(2500);
            await SendAsync(socket, 5, "Runtime.evaluate", new
            {
                expression = injectJs,
                userGesture = true,
                awaitPromise = false
            });
        }

        // Give the commands a moment to be processed before we drop the socket.
        await Task.Delay(400);
        try { await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
    }

    private static async Task SendAsync(ClientWebSocket socket, int id, string method, object @params)
    {
        string payload = JsonSerializer.Serialize(new { id, method, @params });
        var bytes = Encoding.UTF8.GetBytes(payload);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(120); // small gap so commands land in order
    }
}
