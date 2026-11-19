# Satchel

[![CI](https://github.com/coryfinnegan/Satchel/actions/workflows/ci.yml/badge.svg)](https://github.com/coryfinnegan/Satchel/actions/workflows/ci.yml)
[![Release](https://img.shields.io/github/v/release/coryfinnegan/Satchel?include_prereleases)](https://github.com/coryfinnegan/Satchel/releases)

An ad-free World of Warcraft addon manager for Windows. It does what WowUp does day to day (finds your
game installs, tracks your addons, and keeps them updated) with no ads, no telemetry and no Electron.

- **Native and light.** WPF on .NET 10. Opens in well under a second, a fraction of an Electron app's memory.
- **One-click switch from WowUp / WowUp-CF.** On first launch it imports your game installs and tracked
  addons (read-only; WowUp isn't touched).
- **Sources:** CurseForge (with your own free API key), WoWInterface, GitHub releases, Tukui (ElvUI / Tukui).
- **Every flavor:** Retail, PTR/XPTR/Beta, Classic (MoP), Classic Era, Anniversary, Forever, and their PTRs.
- **Safe updates.** New files are staged next to your AddOns folder and swapped in with renames. If anything
  fails, the old version is put back. Removing an addon sends it to the Recycle Bin.
- **Hands-off if you want.** Per-addon auto-update, beta/alpha channels, ignore, background checks and a tray icon.

## Download

Grab the latest zip from [Releases](https://github.com/coryfinnegan/Satchel/releases/latest), unzip it anywhere, and run
`Satchel.exe`:

- `Satchel-vX.Y.Z-win-x64.zip`: small; needs the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
- `Satchel-vX.Y.Z-win-x64-self-contained.zip`: runs with nothing else installed.

Builds aren't code-signed, so SmartScreen may warn on first run. Each release has SHA-256 checksums and a signed build
provenance attestation: `gh attestation verify <zip> --repo coryfinnegan/Satchel`.

## Build from source

Requires Windows 10/11 and the .NET 10 SDK.

```powershell
.\build.ps1          # publishes to .\dist\Satchel
.\install.ps1        # copies to %LOCALAPPDATA%\Programs\Satchel and adds a Start menu shortcut
```

`.\install.ps1 -Desktop` also adds a desktop shortcut; `.\install.ps1 -Uninstall` removes it again.
`.\build.ps1 -SelfContained` bundles .NET so no runtime install is needed.

## CurseForge API key

CurseForge only serves third-party apps that use their own API key. WowUp-CF shows ads because its key comes
with that condition. Satchel uses **your** key instead:

1. Apply at <https://console.curseforge.com> (free; CurseForge reviews requests).
2. In Satchel, open **Settings → Addon sources**, paste the key, then **Save key**.

The key is encrypted with Windows DPAPI for your account and is only ever sent to `api.curseforge.com` and
CurseForge's download CDN. WoWInterface, GitHub and Tukui need no key. Addons from those sources work
immediately.

## Using it

| | |
|---|---|
| **My addons** | Everything installed for the selected game. **Update all**, **Check for updates** (F5), **Rescan**, filter (Ctrl+F). Right-click or ⋯ on a row for channel, auto-update, ignore, reinstall, open website/folder, remove. |
| **Get addons** | Popular addons, search across sources, or paste a CurseForge / WoWInterface / GitHub / Tukui link. |
| **Settings** | API keys, game installs (add, auto-detect, import from WowUp, per-install defaults), background checks, tray, start with Windows. |

Manually installed addons are identified by CurseForge fingerprint (when a key is set), by the
`X-Curse-Project-ID` / `X-WoWI-ID` TOC fields, or by folder name (ElvUI/Tukui). Anything else is listed as
"Not tracked".

## Privacy

No ads, analytics, telemetry, accounts or cookies. Network traffic goes only to the addon sources you use and
their download and image servers. Data lives in `%APPDATA%\Satchel` (settings, tracked addons) and
`%LOCALAPPDATA%\Satchel` (caches, logs).

## Development

```powershell
dotnet build Satchel.slnx
dotnet test tests\Satchel.Tests                         # unit tests
$env:SATCHEL_LIVE = "1"; dotnet test tests\Satchel.Tests  # + live API / sandbox install tests
$env:SATCHEL_HOME = "C:\temp\satchel-sandbox"           # run the app against a throwaway data folder
```

- `src/Satchel.Core`: providers, TOC parsing, fingerprinting, scanning, install engine, storage (no UI).
- `src/Satchel`: WPF app (MVVM, dark theme, tray).
- `tests/Satchel.Tests`: xUnit tests.

CI (`.github/workflows/ci.yml`) builds, tests and publishes on every push and pull request. To release, push a tag:

```powershell
git tag v1.2.3; git push origin v1.2.3     # v1.2.3-beta.1 makes a pre-release
```

`.github/workflows/release.yml` tests, builds both zips, attests them and publishes the GitHub release. To rebuild an
existing tag, run the workflow by hand from the Actions tab.

## Credits and license

Satchel is an independent project and is not affiliated with WowUp, CurseForge/Overwolf or Blizzard. The
CurseForge folder-fingerprint scanner is adapted from [WowUp](https://github.com/WowUp/WowUp) (GPL-3.0),
so Satchel is licensed under the GPL-3.0 as well. See [LICENSE](LICENSE).
