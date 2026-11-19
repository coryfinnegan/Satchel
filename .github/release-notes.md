An ad-free World of Warcraft addon manager for Windows. There are no ads, telemetry or accounts.

### Download

| File | Pick this if |
|---|---|
| **`Satchel-Setup-{TAG}.exe`** | **Recommended.** One-click installer with everything included: Start menu entry, uninstall from Windows Settings, upgrades in place. No admin rights needed. |
| `Satchel-{TAG}-win-x64.zip` | You have the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed, or don't mind installing it (small download, ~1 MB app). |
| `Satchel-{TAG}-win-x64-self-contained.zip` | You want it to run with nothing else installed (larger download). |

Run the installer, or unzip anywhere and run `Satchel.exe`. On first launch it finds your game installs and imports WowUp / WowUp-CF data
automatically. For CurseForge addons, add your own free API key under **Settings → Addon sources**.
WoWInterface, GitHub and Tukui (ElvUI) work without a key.

The app isn't code-signed yet, so Windows SmartScreen may warn the first time; choose **More info → Run anyway**.
To check a download really came from this repository's build:

```
gh attestation verify Satchel-Setup-{TAG}.exe --repo coryfinnegan/Satchel
```

SHA-256 checksums are in `SHA256SUMS.txt`.
