## What's new in v1.5.0

### The engines that were already in there, now reachable

Several complete features shipped in earlier builds but had no way to be switched on: the flags that enabled them defaulted to off and no page bound to them. The Anti-AFK loop, the crash watchdog, the RAM monitor, the local control API and the FastFlag writer were all fully implemented, wired up at startup — and unreachable. Some had never run in any release.

This version gives every one of them a real UI, and fixes the ones that were also broken underneath.

### New

| Feature | Where | What it does |
|---|---|---|
| **Anti-AFK** | Settings → Automation | Focuses each running client, sends one key tap, returns you to what you were doing. Configurable interval and key, plus *Run one pass now*. |
| **Crash watchdog** | Settings → Automation | Notices when a tracked client closes or crashes. Accounts with the new per-account **Auto-rejoin** switch are relaunched into the same server, with a brake that gives up after 3 crashes in 10 minutes. |
| **RAM monitor** | Settings → Automation | Live per-client memory readout, with an optional cap that closes a client that runs away. |
| **FastFlags** | Settings → FastFlags | Uncap frame rate, disable telemetry, force voxel lighting, disable voice chat, plus a raw JSON editor with live validation. *Write flags now* and *Clear all flags*. |
| **Per-account FastFlags** | Accounts → details | Extra flags applied only when that account launches, merged on top of the global set. |
| **Two-factor codes** | Accounts → details | Paste the Base32 secret Roblox shows under *"Can't scan the QR code?"* and get the live 6-digit code with a rollover countdown and a copy button. Generated locally — nothing leaves the machine. |
| **Global hotkeys** | Settings → Global hotkeys | Record a chord per action (launch, server-hop, close all, focus manager). Click, press, done. |
| **Local control API** | Settings → Local API | Bearer-token HTTP server on 127.0.0.1 for scripting the manager, with token generation, copy, and the endpoint list. |
| **Proxy** | Settings → Proxy | Route the manager's Roblox API traffic through a proxy, with credentials and a *Test proxy* button. |
| **Encrypted backup / restore** | Settings → Security | Portable, password-encrypted export of your accounts (AES-256-GCM, PBKDF2-SHA256). `accounts.dat` is DPAPI-bound to one Windows user; a backup restores anywhere. Restoring **merges** — existing accounts keep their place and only get a fresh cookie. |
| **More live-data control** | Settings → Live data | Economy tracking toggle and an adjustable presence poll interval. |
| **More security control** | Settings → Security | Startup cookie validation (plus *Check all cookies now*), rotated-cookie capture, and the audit log. |

### Fixes

- **Unlock FPS never did anything.** It wrote `ClientAppSettings.json` to `%LOCALAPPDATA%\Roblox\ClientSettings\` — a path the modern client does not read. The cap was silently ignored on every launch since the feature was added. It now writes into each installed `Versions\<hash>\ClientSettings\`, and merges with your FastFlags instead of the two overwriting each other.
- **The FastFlags engine was never called.** `FFlagsService` existed and worked; nothing in the app invoked it. It now runs on the launch path.
- **Proxy settings were inert.** The address and credentials were saved to disk and read back into the UI, but the HTTP client was built once with no proxy at all, so every Roblox call went out over the direct connection regardless. The proxy is now actually applied, and rebuilds itself when you change it.
- **Presence could not be turned back on without restarting.** The poll loop refuses to start while the setting is off, so an app that started with presence disabled had no timer — and flipping the switch did nothing until the next launch.
- **The Friends tab never picked an account.** Its picker was seeded while the account store was still empty (accounts are decrypted a moment later during startup), so it always landed on nothing and the tab stayed blank until you chose an account by hand.
- **The server browser lost your selection when you changed the sort mode**, after which Join answered "Select a server first".
- **Restored backups came back unusable.** The backup format never stored the user id, and nearly everything keys off it — presence, RAP, Premium, launch attribution. Backups now carry it, and a restore re-validates cookies to backfill it for files written by older versions.
- Refreshing account data spent one request per **dead** cookie on the Robux endpoint every time; invalid accounts are now skipped, as the economy pass already did.
- The debounced Place ID lookup leaked a `CancellationTokenSource` on every keystroke.

### Security

- The backup password minimum is **12 characters**, deliberately stricter than the master password on `accounts.dat`. That file keeps every cookie individually DPAPI-wrapped, so cracking its password still leaves the cookies bound to the original Windows user. A backup stores raw cookies by design — that is what makes it portable — so its password is the only thing between the file and full account takeover, and the file is meant to travel.
- API tokens are generated from a cryptographic RNG (192 bits). The control server stays bound to `127.0.0.1`, every route but `/ping` requires the token, and the settings page says plainly that anyone able to run code as you can read it.
- *Test proxy* deliberately sends no cookie, so pointing it at an untrusted proxy cannot leak a session.

### Under the hood
- `FFlagsService` gained per-launch application, tolerant raw-JSON parsing and validation
- `RobloxApi` builds its HTTP client lazily and rebuilds it when the proxy configuration changes
- Clean build: 0 warnings, 0 errors

**Full Changelog**: https://github.com/Vaelixx/Roblox-Account-Manager/compare/v1.4.0...v1.5.0
