## What's new in v1.5.1

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

## Everything from v1.5.0 (never published separately)

Several complete features shipped in earlier builds but could never be switched on: their flags
defaulted to off and no page bound to them. Some had never run in any release.

| Feature | Where |
|---|---|
| **Anti-AFK** — interval, key, restore-focus, run-one-pass-now | Settings → Automation |
| **Crash watchdog** + per-account **Auto-rejoin** | Settings → Automation / account details |
| **RAM monitor** — live per-client readout, optional cap | Settings → Automation |
| **FastFlags** — 4 presets, raw JSON editor with validation | Settings → FastFlags |
| **Per-account FastFlags**, merged over the global set | Account details |
| **Two-factor codes** — local TOTP with countdown and copy | Account details |
| **Global hotkeys** — click a row, press the chord | Settings → Global hotkeys |
| **Local control API** — token generation, port, endpoint list | Settings → Local API |
| **Proxy** — address, credentials, connection test | Settings → Proxy |
| **Encrypted backup / restore** — portable, merges on restore | Settings → Security |
| Economy toggle, presence cadence, cookie validation, rotation capture, audit log | Settings |

### Fixes from v1.5.0

- **Unlock FPS never took effect** — it wrote `ClientAppSettings.json` to a path the modern client
  does not read. (v1.5.1 finishes this: it now also reaches bootstrapper-managed installs.)
- **The FastFlags engine had no caller at all.** It now runs on the launch path.
- **Proxy settings were inert** — saved and displayed, but the HTTP client was built once with no
  proxy, so every Roblox call went out over the direct connection regardless.
- **Presence could not be re-enabled without restarting** — the poll loop refuses to start while
  the setting is off, so no timer existed to resume.
- **The Friends tab never auto-selected an account** — its picker was seeded before the account
  store finished decrypting, so it always landed on nothing.
- **The server browser dropped your selected server** when the sort mode changed.
- **Restored backups came back unusable** — the format never stored the user id, which nearly
  everything keys off.
- Refreshing spent one Robux request per **dead** cookie; invalid accounts are now skipped.
- The debounced Place ID lookup leaked a `CancellationTokenSource` on every keystroke.

### Security

- The backup password minimum is **12 characters**, deliberately stricter than the master password.
  `accounts.dat` keeps every cookie individually DPAPI-wrapped, so cracking its password still
  leaves the cookies bound to the original Windows user. A backup stores them raw by design — that
  is what makes it portable — so its password is the only barrier, and the file is meant to travel.
- API tokens come from a 192-bit cryptographic RNG. The control server stays bound to `127.0.0.1`
  and every route except `/ping` requires the token.
- *Test proxy* sends no cookie, so pointing it at an untrusted proxy cannot leak a session.

**Full Changelog**: https://github.com/Vaelixx/Roblox-Account-Manager/compare/v1.4.0...v1.5.1
