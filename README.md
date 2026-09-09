<div align="center">

<img src="icon.png" width="96" alt="Roblox Account Manager icon">

# Roblox Account Manager

**Fast, private, beautifully minimal manager for your Roblox accounts.**

Add accounts once — launch any of them into any game with one click, run several at the same time,
all from a clean, modern desktop app.

<p>
  <a href="https://github.com/Vaelixx/Roblox-Account-Manager/releases/latest"><img src="https://img.shields.io/github/v/release/Vaelixx/Roblox-Account-Manager?style=for-the-badge&label=release&color=6d5cff" alt="Latest release"></a>
  <a href="https://github.com/Vaelixx/Roblox-Account-Manager/releases"><img src="https://img.shields.io/github/downloads/Vaelixx/Roblox-Account-Manager/total?style=for-the-badge&label=downloads&color=22c55e" alt="Downloads"></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4?style=for-the-badge&logo=dotnet&logoColor=white" alt=".NET 8">
  <img src="https://img.shields.io/badge/platform-Windows-0078d4?style=for-the-badge&logo=windows&logoColor=white" alt="Windows">
</p>

<p>
  <a href="#-features">Features</a> ·
  <a href="#-install">Install</a> ·
  <a href="#-quick-start">Quick start</a> ·
  <a href="#-security">Security</a> ·
  <a href="#-building-from-source">Build</a> ·
  <a href="#-roadmap">Roadmap</a> ·
  <a href="#-faq">FAQ</a>
</p>

<img src="docs/accounts.png" alt="Accounts view" width="820">

</div>

---

## ✨ Features

### Launching
| | |
|---|---|
| 🚀 **One-click launch** | Paste a Place ID, hit **Launch**. Optional Job ID joins a specific server. |
| 🧩 **Multi-instance** | Run several Roblox clients side by side — no extra tools needed. |
| 🌐 **Server browser** | Browse public servers with live player counts, join directly or copy a Job ID. |
| 🕹️ **Open anywhere** | Open an account signed-in in a private Chromium window, in the Roblox app, or view its public profile. |

### Adding accounts
| | |
|---|---|
| 🔑 **Sign in with username & password** | Type the account's Roblox credentials — the session cookie is fetched and validated automatically. No developer tools, no copying cookies by hand. |
| 🔢 **Two-step verification** | A 2FA-protected account asks for its 6-digit code right in the dialog and continues the same sign-in. |
| 🌍 **Browser sign-in fallback** | When Roblox insists on a captcha, the real login page opens in a throw-away browser profile and the session is picked up automatically. |
| 📋 **Cookie import** | Pasting a `.ROBLOSECURITY` cookie still works, one at a time or in bulk. |

### Organisation
| | |
|---|---|
| 🗂️ **Groups, aliases & notes** | Structure any number of accounts, see everything at a glance. |
| 📊 **Live data** | Avatars, presence (online / in-game / studio) and Robux for every account, refreshed automatically. |
| 🔎 **Instant overview** | Presence updates within seconds of launching or closing a game. |
| ⏱️ **Playtime tracking** | Records how long each account's client actually runs — totals per account and across the last seven days. |

### Experience
| | |
|---|---|
| 🎨 **Modern UI** | Dark, minimal WPF interface with smooth page transitions, animated buttons and subtle glow effects. |
| 📥 **System tray** | Close to tray, keep running silently — tray icon and global hotkeys run on native Win32, no WinForms. |
| 📦 **Zero setup** | One portable, self-contained `.exe` (~56 MB). No .NET install required on the target PC. |
| 🔄 **Verified auto-update** | Notifies you when a release is out, checks its SHA-256 before installing, and keeps the previous build so you can roll back. Interval, pre-release channel and “skip this version” are all yours. |
| 🚀 **Start with Windows** | Optional per-user autostart, straight into the tray if you want it. |

### Automation
| | |
|---|---|
| ☕ **Anti-AFK** | Keeps running clients from being idle-kicked — one key tap per interval, then focus goes back where it was. |
| 🔁 **Crash watchdog** | Notices a client that closed or crashed and relaunches accounts marked **Auto-rejoin** into the same server, with a crash-loop brake. |
| 📉 **RAM monitor** | Live per-client memory, with an optional cap that closes a client that runs away. |
| ⏱️ **Scheduler & presets** | Time-triggered launches and closes, driven by named launch presets. |
| ⌨️ **Global hotkeys** | Record a chord per action — launch, server-hop, close all clients, focus the manager. |
| 🔌 **Local control API** | Token-gated HTTP server on `127.0.0.1` so scripts can list, launch, close and query accounts. |

### Tuning
| | |
|---|---|
| 🚩 **FastFlags** | Uncap frame rate, disable telemetry, force voxel lighting, disable voice chat — plus a raw JSON editor. Applies globally or per account. |
| 🌐 **Proxy** | Route the manager's Roblox API traffic through an HTTP/SOCKS proxy, with credentials and a connection test. |

### Privacy & security
| | |
|---|---|
| 🔐 **Encrypted at rest** | Account store encrypted with Windows **DPAPI**, or a **master password** (AES-256-GCM + PBKDF2). |
| 🕵️ **Never plain text** | Each cookie additionally encrypted individually — even the decrypted store never contains a readable cookie. |
| 💾 **Portable backups** | Password-encrypted export that restores on any PC, unlike the DPAPI store which is bound to one Windows user. |
| 🔑 **Two-factor codes** | Store a 2FA secret per account and read the live 6-digit code — generated locally, never transmitted. |
| 📋 **Audit log** | Optional record of unlocks, launches, cookie rotations and account changes. Cookie values are never written. |
| 🏠 **100 % local** | Cookies never leave your machine. Only official Roblox endpoints are contacted. No telemetry, no analytics. |

---

## 🖼️ Screenshots

| Accounts | Settings |
|----------|----------|
| ![Accounts](docs/accounts.png) | ![Settings](docs/settings.png) |

---

## 📦 Install

1. Download the latest `RobloxAccountManager.exe` from the [**Releases**](../../releases) page.
2. Put it in its own folder — it creates a `data\` folder next to itself.
3. Double-click to run. Nothing else to install.

> [!NOTE]
> Windows SmartScreen may warn about an unrecognised app the first time — click **More info → Run anyway**.
> The exe is unsigned; you can always [build it from source](#-building-from-source) yourself.

---

## 🚀 Quick start

1. **Add account** → type the account's Roblox **username and password**. The session is fetched from
   Roblox and validated before the account is stored.
2. Select the account — details and launch options appear on the right.
3. Enter a **Place ID** (game preview loads automatically) and press **Launch**.
4. Organise: set an **Alias**, a **Description**, pick or create a **Group** on the fly.

<div align="center"><img src="docs/add-account.png" width="440" alt="Add account dialog"></div>

### What happens to the password

It is posted to Roblox's own login endpoint (`auth.roblox.com`) over HTTPS and to nothing else. It is
never written to disk, never logged, and gone the moment the dialog closes — only the session cookie
Roblox returns is stored, encrypted like every other account.

If the account uses **two-step verification**, the dialog asks for the 6-digit code and finishes the
same sign-in. If Roblox demands a **captcha** — it does that for some accounts and some networks, and
no application can answer one on your behalf — use **Sign in in a browser window instead**: the
genuine Roblox login page opens in a throw-away browser profile, and the session is picked up the
moment you are logged in. That profile is wiped when the window closes.

### Using a cookie instead

The **Paste cookie** tab takes a `.ROBLOSECURITY` value directly, which is still the fastest route if
you already have one — log into the account in a browser, open the cookies for `roblox.com` and copy
the `.ROBLOSECURITY` value (starts with `_|WARNING:-DO-NOT-SHARE-THIS...`).

> [!CAUTION]
> Never share this cookie with anyone or paste it into websites. Whoever has it controls the account.

---

## 🔐 Security

Account cookies are the keys to your accounts — this app treats them accordingly:

- **Encrypted at rest.** The account file is encrypted with **Windows DPAPI** (tied to your Windows user) by default, or with a **master password** you set (AES-256-GCM + PBKDF2) from Settings.
- **Never plain text.** On top of the file encryption, each cookie is individually DPAPI-encrypted — even the decrypted store never contains a readable cookie.
- **Passwords are not stored.** A sign-in posts the password to Roblox's own login endpoint and keeps nothing: no file, no log, no memory beyond the open dialog. Only the returned session cookie is persisted.
- **Local only.** No server, no sync, no telemetry. The app talks exclusively to official Roblox APIs over HTTPS.
- **Validated on startup.** Cookies are re-checked against Roblox so dead sessions are flagged immediately.
- **Open source.** Every line that touches your cookies is in this repository — audit it, build it yourself.

---

## 🛠️ Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```powershell
git clone https://github.com/Vaelixx/Roblox-Account-Manager.git
cd Roblox-Account-Manager

# quick dev build + run
dotnet run --project src\RobloxAccountManager.csproj

# single-file, self-contained release exe  ->  .\dist\
dotnet publish src\RobloxAccountManager.csproj `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -p:SatelliteResourceLanguages=en `
    -o dist
```

Produces a single portable `dist\Roblox Account Manager.exe` — copy anywhere, double-click, done.

### Cutting a release

The changelog lives in a single [`CHANGELOG.md`](CHANGELOG.md); the build embeds it and the app
slices out the section matching the version it is running.

1. Add a `## vX.Y.Z — YYYY-MM-DD` section at the top of `CHANGELOG.md`.
2. Set `<Version>` in `src/RobloxAccountManager.csproj` to the same number.
3. Push the tag:

```powershell
git tag v1.7.0
git push origin v1.7.0
```

[`.github/workflows/release.yml`](.github/workflows/release.yml) does the rest: it builds the
single-file exe on GitHub, refuses the build on a compiler warning or a tag that disagrees with
`<Version>`, uses your changelog section as the release body, and attaches
`RobloxAccountManager.exe` together with its `SHA256SUMS.txt`.

Publishing that checksum matters — the in-app updater verifies the download against it before
anything replaces the running application. A release built and uploaded by hand has no checksum in
its body, so the updater falls back to the size and executable-header checks alone.

A tag with a suffix (`v1.8.0-beta.1`) is published as a pre-release and only offered to users who
turned on **Include pre-releases**.

### Tech overview

| | |
|---|---|
| UI | WPF (.NET 8), MVVM, custom dark theme, hand-rolled animations |
| Tray & hotkeys | Native Win32 (`Shell_NotifyIcon`, `RegisterHotKey`) — no WinForms dependency |
| Storage | DPAPI / AES-256-GCM encrypted local store |
| Networking | `HttpClient` against official Roblox web APIs |
| Packaging | Single-file, self-contained, compressed publish |

---

## 🗺️ Roadmap

Planned / under consideration — open an [issue](../../issues) to vote or suggest:

Shipped:

- [x] **Bulk launch** — select multiple accounts, launch all into the same server with stagger delay
- [x] **Auto-rejoin watchdog** — relaunch an instance into the same server if it crashes or disconnects
- [x] **Window arranger** — auto-tile multiple Roblox windows in a grid
- [x] **Join friend / follow player** — join the server a given username is playing on
- [x] **Private-server & share-link support** — paste any roblox.com share link, launch directly
- [x] **Encrypted backup & restore** — one-file export/import of the whole account store *(v1.5.0)*
- [x] **Quick-launch hotkeys** — bind actions to a global hotkey *(v1.5.0)*
- [x] **Accent & theme picker** — custom accent colour and a full theme editor
- [x] **Localisation** — English + German
- [x] **CLI companion** — `ram launch --account <alias> --place <id>` for scripting

- [x] **Sign in with username & password** — the cookie is fetched automatically, 2FA included *(v1.6.0)*
- [x] **Update settings** — interval, pre-release channel, skip a version, verified downloads, roll-back *(v1.7.0)*
- [x] **Start with Windows** — per-user autostart, optionally straight into the tray *(v1.7.0)*
- [x] **Playtime tracking** — recorded per account, shown on the cards and the dashboard *(v1.7.0)*

Still open:

- [ ] **Account health panel** — cookie age, last validation, expiry warnings
- [ ] **Scheduler & preset editor** — the engine runs; it still needs a UI to build tasks and presets
- [ ] **Per-account proxy** — the field is stored, but only the global proxy is applied today
- [ ] **Light mode** — the theme editor can already get close; a proper preset is not there yet

---

## ❓ FAQ

<details>
<summary><b>Is this safe? Where do my cookies go?</b></summary>

Nowhere. Everything is stored encrypted on your own disk and only sent to official `roblox.com` endpoints for validation and launching — same as your browser does. The full source is in this repo.
</details>

<details>
<summary><b>What happens to my password when I sign in?</b></summary>

It goes to `auth.roblox.com` — Roblox's own login endpoint — over HTTPS, and to nowhere else. It is never written to disk, never logged and not kept after the dialog closes; only the session cookie Roblox returns is stored, encrypted like every other account. If you would rather not type it into the app at all, use **Sign in in a browser window instead**: the real Roblox login page opens in a throw-away browser profile and the session is picked up automatically. The relevant code is [`RobloxAuthService.cs`](src/Services/RobloxAuthService.cs).
</details>

<details>
<summary><b>What if an update breaks something?</b></summary>

The build being replaced is kept next to the app, so **Settings → Updates → Restore previous version** puts it back and restarts. Your accounts and settings live in the `data\` folder and are never touched by an update. If you would rather not spend the ~56 MB, turn **Keep the previous version** off in the same place.
</details>

<details>
<summary><b>Why does SmartScreen warn me?</b></summary>

The exe is not code-signed (certificates cost money). **More info → Run anyway**, or build from source.
</details>

<details>
<summary><b>Why is the exe ~56 MB?</b></summary>

It bundles the entire .NET 8 runtime so you don't have to install anything. One file, works on any Windows 10/11 x64 machine.
</details>

<details>
<summary><b>Can I run more than one Roblox instance?</b></summary>

Yes — multi-instance is built in. Launch as many accounts as your PC can handle.
</details>

<details>
<summary><b>Does this violate Roblox ToS?</b></summary>

The app only automates what you could do manually in a browser (log in, launch games) and talks only to official APIs. Managing alt accounts is widely tolerated, but automation is a grey area — use it responsibly, at your own risk.
</details>

---

## 🤝 Contributing

Issues and pull requests welcome. For bigger changes, open an issue first to discuss the direction.

1. Fork → branch → change → PR
2. Keep the style: MVVM, no code-behind logic where avoidable, nullable enabled
3. `dotnet build -c Release` must pass with **0 warnings**

---

<div align="center">

**Not affiliated with Roblox Corporation.**
Use responsibly and in accordance with Roblox's Terms of Service. You are responsible for your own accounts.

Made with WPF + .NET 8 · Windows 10/11 x64

</div>
