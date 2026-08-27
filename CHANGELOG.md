# Changelog

Every release, newest first. One file — the section matching the version being built is compiled
into the exe and shown in **What's new**, so a release no longer needs its own notes file.

**Releasing a new version**

1. Add a `## vX.Y.Z — YYYY-MM-DD` section at the top of this file.
2. Bump `<Version>` in `src/RobloxAccountManager.csproj` to the same number.
3. Publish. The build embeds this file; the app slices out the matching section at runtime.

---

## v1.6.0 — 2026-08-27

### Sign in with a username and password

Adding an account used to mean pasting a `.ROBLOSECURITY` cookie, which means opening browser
developer tools and knowing which value to copy. That single step is where most people got stuck.

**Add account** now opens on a **Sign in** tab: type the Roblox username and password, and the
session cookie is fetched automatically and validated before the account is stored.

- **Two-step verification is handled in the dialog.** When the account has 2FA on, a code field
  appears and says where the code comes from — the authenticator app or the email Roblox just sent.
- **Captchas fall back to a real browser.** Roblox answers some sign-ins with an Arkose puzzle or a
  device confirmation, which nothing but a browser can display. *Sign in in a browser window
  instead* opens the genuine Roblox login page in a throw-away profile, waits for you to finish, and
  picks the session up over the DevTools protocol. The profile is wiped the moment the window closes.
- **Pasting a cookie still works** — it moved to the second tab and is unchanged.

The password is posted to Roblox's own login endpoint and to nothing else. It is never written to
disk, never logged, and never kept once the dialog closes; only the cookie Roblox hands back is
stored, encrypted exactly like every other account.

### Changelog handling

The repository carried one `release_notes_vX.Y.Z.md` per release, and the build embedded whichever
file matched the version. Four releases in, that was four files, and a version bump silently shipped
with no changelog at all if the matching file was missing.

There is now a single `CHANGELOG.md`. The whole file is embedded and the app slices out the section
for the version it is running, so releasing means adding a section and bumping the version — nothing
else. **What's new** gained a **Full changelog** toggle that shows every past version from the same
embedded copy, offline.

### Fixes

- **Cookie validation marked healthy accounts as dead.** The startup check treated *any* failed
  lookup as a rejected cookie, so an HTTP 429 — which validating several accounts at once provokes
  by itself — or simply being offline flagged good accounts invalid and sent people off to re-add
  them. Only an explicit 401/403 from Roblox counts now; anything inconclusive leaves the previous
  state alone.
- **The browser download prompt could open behind the dialog that asked for it**, which was
  indistinguishable from the app freezing, since a modal window blocks input everywhere else.

---

## v1.5.1

> Coming from **v1.4.0**? This release contains the whole v1.5 line — v1.5.0 was tagged but never
> published, so everything below is new to you.

### Works with Bloxstrap, Froststrap, Fishstrap and friends

The app assumed Roblox always lives in `%LOCALAPPDATA%\Roblox\Versions`. On a machine where a
third-party bootstrapper owns the install that folder can be **absent entirely** — the client sits
under the launcher's own directory instead. Three things were wrong as a result:

- the self-check reported **"Roblox is not installed"** on a perfectly working machine
- FastFlags and the FPS cap were written to a folder no client reads, so they did nothing
- even when written to the right version folder, a bootstrapper regenerates that file on every
  launch from its own copy — so the flags were wiped before the client ever saw them

Install discovery now follows the `roblox-player://` protocol handler, which is the authoritative
answer to "what actually starts when we launch", and falls back to every known layout. Flags go to
the launcher's **managed** settings file where one exists, and are **merged** rather than
overwritten, so the flags you set in Bloxstrap/Froststrap yourself survive.

Settings → About now shows which client is installed and who handles launches.

### Fixes

- **"What's new" said "Release notes could not be loaded."** The changelog had exactly one source:
  the GitHub release for the running version. That fails when the release isn't published yet, the
  machine is offline, or the unauthenticated GitHub API limit (60 requests/hour per IP) is spent.
  The notes are now compiled into the exe, so the window always has content; GitHub is a fallback.
- **No way to check for updates on demand.** The background poll runs every 5 minutes but is silent
  when there is nothing new, so "up to date" and "the check is broken" looked identical. Settings →
  About has a **Check for updates** button that always reports an outcome, a line saying when the
  last check ran and what it found, and a **What's new in this version** button to reopen the
  changelog at any time.

### New

- **RAM trimming.** *Trim now* hands every tracked client's idle memory back to Windows, with an
  optional automatic trim on an interval. The status line reports how much was released. Note this
  is not a permanent saving — the pages return when a client touches them again; it helps when
  several clients are open and the machine starts to run short.

---

## v1.5.0

> Tagged but never published as a separate release; everything here also shipped in v1.5.1.

### The engines that were already in there, now reachable

Several complete features shipped in earlier builds but had no way to be switched on: the flags that
enabled them defaulted to off and no page bound to them. The Anti-AFK loop, the crash watchdog, the
RAM monitor, the local control API and the FastFlag writer were all fully implemented, wired up at
startup — and unreachable. Some had never run in any release.

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

---

## v1.4.0

### Multi-instance now works for launches started outside the app

This is the headline fix. Roblox enforces single-instance with **two** guards, and the app only ever defeated one of them:

| Guard | Where it lives | Before |
|---|---|---|
| `ROBLOX_singletonMutex` | global named mutex | held open ✅ |
| `ROBLOX_singletonEvent` | **inside each client process** | untouched ❌ |

Because of the second guard, pressing **Play** on roblox.com, launching from the Roblox home screen, or opening a Discord invite while a client was already running did *not* open a new window — the new client handed its launch URL to the running one and exited. Launches from the manager itself were unaffected, since those carry a fresh auth ticket the running client accepts, which is why the problem looked inconsistent.

The new guard clears that per-client lock in every running client and keeps doing so via a lightweight watcher, so a client the app never started is treated exactly like one it did.

### New

- **Multi-instance guard panel** (Settings → Launch) — live status, a *Fix multi-instance now* button, and a *Restart as administrator* option that appears only when elevation would actually help
- **External clients are managed too** — clients started from the website or home screen are adopted into the registry, so Anti-AFK, the RAM monitor and the window grid can reach them
- **Self-check** (Settings → Diagnostics) — verifies the Roblox install, the `roblox-player` protocol handler (and warns when another launcher has hijacked it), data-folder writability, free disk space, Roblox API reachability, and the multi-instance guard
- **Diagnostics log** — one rotating, cookie-scrubbed log with an in-app error counter, *Open log folder* and *Clear log*
- **Client window controls** — arrange every client in a grid, minimize all, restore all

### Fixes

- **Data loss:** an account file that failed to decrypt (typically copied from another Windows user or PC) silently loaded as *empty*, and the next save overwrote the real file **and its backup**. The app now refuses to start and tells you where the file is.
- **Wrong client attribution:** two accounts launched close together were deterministically swapped — the wrong cookie on auto-rejoin, and "close previous client" killing the other account's window
- **Untracked clients:** attribution was a single attempt 4 s after launch; a slow client start meant it was never tracked at all — no Anti-AFK, no crash watchdog, no RAM cap, and nothing said so. Now retried for 24 s.
- **PID reuse:** tracked PIDs were never re-validated, so a recycled PID could have the RAM monitor kill, Anti-AFK type into, or the scheduler close an unrelated application
- **Rate limiting:** an HTTP 429 (exactly what launching several accounts in a row provokes) was treated as "this cookie is dead" and marked good accounts invalid. Only 401/403 do that now.
- **Anti-AFK** no longer sends its keystroke when the client window fails to take focus — it used to type Space/W into whatever you were doing
- **Scheduler auto-close** now only closes the clients that task started, instead of every client of those accounts
- **Mutex thread affinity:** the multi-instance mutex was acquired on whichever thread ran a launch and released from another, which always threw and was silently swallowed. It now lives on its own thread.
- Crash reports **append** instead of truncating — a repeating fault no longer destroys the evidence of the first one

### Under the hood

- New services: `RobloxSingletonService`, `DiagnosticsService`, `HealthCheckService`, `InstanceControlService`
- Handle-level singleton clearing via the process handle table (`NtQueryInformationProcess` → `DuplicateHandle` with `DUPLICATE_CLOSE_SOURCE`), with a system-wide fallback for older Windows
- Failures the user can act on now surface as a toast or a status line instead of an empty `catch`
- Clean build: 0 warnings, 0 errors

### Note

Roblox's anti-tamper can refuse the process handle on some systems. If that happens the app says so explicitly and offers to restart with administrator rights — it no longer just quietly does nothing.

---

## v1.3.3

### New

- Smoother UI everywhere: animated page transitions plus soft hover/press fades on cards, buttons and list items
- Buttons polished: subtle scale/opacity feedback and animated icons (hover wiggle, press shrink)
- Presence detection is much faster: shorter poll cadence and an instant refresh right after account actions

### Fixes

- Card glow toned down: smaller reach, no more bleeding deep into card content
- "Update available" state now shows reliably after background update checks

### Under the hood

- Dropped the entire WinForms framework from the bundle — tray icon and global hotkeys now run on native Win32 (Shell_NotifyIcon / RegisterHotKey via HwndSource)
- Single-file exe shrunk from 63.2 MB to 55.9 MB with zero feature loss
- Clean build: 0 warnings, 0 errors
