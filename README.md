<img src="assets/logo.png" width="64" align="left" alt="OrclWAMP logo">

# OrclWAMP – Oracooll Winget App Migration Tool

**Version 1.3.001**

Moving to a new PC? OrclWAMP scans your old PC, finds every app that **winget** can install, and writes a migration folder to a USB stick. On the new PC you double-click one file and your apps install themselves.

![OrclWAMP main window](docs/main.png)

- **Portable.** One ~140 KB exe with no installer and no dependencies. It uses .NET Framework 4.8, which is built into Windows 10 and 11.
- **One exe for both jobs.** The tool that scans the old PC is also the installer on the new one.

## Download

Get **OrclWAMP.exe** from the [latest release](https://github.com/Oracooll/OrclWAMP/releases/latest).

> Windows SmartScreen may warn you because the exe isn't code-signed. Click **More info → Run anyway**.

## How to use it

### On the old PC
1. Run `OrclWAMP.exe`. It scans the PC automatically.
2. Check the list:
   - **Apps winget can install** are ticked. Runtimes (VC++, .NET, WindowsAppRuntime…) and apps that ship with Windows (Edge, OneDrive…) start unticked.
   - **Apps to install manually** have no winget package. Ticked ones go into an HTML checklist so you don't forget them.
3. Optionally use **Add apps from winget…** to add apps this PC doesn't have.
4. Leave **Include Windows settings** ticked (or click **Choose settings…**) to take touchpad gestures, taskbar options, wallpaper, keyboard layouts and more along.
5. Click **Create migration package…** and choose your USB drive.

### On the new PC
1. Connect to the internet.
2. Open the `OrclWAMP-Migration` folder on the USB stick and double-click **Install.cmd** (or `OrclWAMP.exe`).
3. Check the list and click **Start installation**.
4. Click **Manual apps & downloads** for anything winget couldn't handle. Download the installers one by one or all at once.
5. Click **Windows settings** to apply your old PC's settings. A backup is made first, and **Undo last apply** reverses them.

![Restore mode (dark theme)](docs/restore.png)

### Apps winget can't install
**Manual apps & downloads** lists them with a download link **only where OrclWAMP is sure of it**. Download them one by one (double-click) or all at once into `Downloads\OrclWAMP`. Apps without a sure link say "No link". Right-click one to set your own link or search the web.

![Manual apps & downloads](docs/manual-apps.png)

### Windows settings
Your personal Windows settings travel with the apps: touchpad gestures, mouse, keyboard layouts, taskbar and Explorer options, colours, wallpaper, regional formats, power timers and fonts. On the new PC they're applied with a backup, so **Undo** is one click away.

![Windows settings](docs/settings.png)

## Features

| | |
|---|---|
| Smart scan | `winget list` + `winget export`. Exact package IDs, duplicates removed, per-language Office entries collapsed |
| Sensible defaults | Runtimes and apps that come with Windows start unticked. System components are hidden |
| Search, filter, sort | Live search, view filter, sortable columns, All / None / Invert, right-click actions |
| Add from winget | Search the winget catalog inside the app and add any package |
| Profiles | Save or load app lists (`.json`). Standard `winget export` files can be opened too |
| CSV export | Full inventory of the old PC |
| Find winget packages for manual apps | Searches winget by exact name for apps it couldn't link (e.g. Store versions). You confirm each match; ambiguous names stay unticked |
| Manual apps & downloads | A window listing every app winget can't install. Download them one by one (double-click) or all at once into `Downloads\OrclWAMP`, open official download pages, set your own link, run the downloaded installers |
| Only sure links | A download link is shown **only** when OrclWAMP is sure of it: a checked official link, a link you entered, or a web app's own address. Everything else says "No link" |
| Manual-install checklist | Printable HTML checklist of the manual apps, with the same sure-only links |
| Themes | Light / Dark / System toggle in every window. System follows Windows live |
| **Windows settings migration** | Takes your personal settings along: touchpad gestures, mouse, keyboard layouts & languages, taskbar/Start/Explorer options, colours, wallpaper, regional formats, power timers, user fonts, Wi-Fi (opt-in). Applied with a backup and **Undo**, and only known settings are ever written |
| Log pane position | One click moves the restore log between the bottom and the right side. Remembered |
| winget check and repair | On the new PC, re-registers App Installer or downloads it from Microsoft if winget is missing |
| Skips installed apps | Apps already on the new PC are detected and skipped |
| Robust installs | Silent installs, per-app timeout, keeps going after errors, retries automatically when another install is in progress, **Retry failed**, readable error messages |
| Elevation | Optional one-time UAC prompt at the start, so installers don't each ask. Warns if the admin account isn't the signed-in user |
| Exact versions | Optionally pin the old versions. Falls back to the latest if a version is gone |
| Keeps PC awake | Sleep is blocked while installing. Optional restart when finished |
| Logs | Each restore run writes a log next to the package |
| Fallbacks | `winget-packages.json` (for `winget import`) and a plain `Install-Fallback.ps1` |
| Command line | Unattended restore and headless scan/package for IT use |

## Command line

```
OrclWAMP.exe                        Scan this PC (or restore, if a package file is next to the exe)
OrclWAMP.exe /scan                  Always open the scanner
OrclWAMP.exe /restore [file]        Install apps from a migration package
OrclWAMP.exe /restore /unattended   Install everything without asking
OrclWAMP.exe /package <folder>      Scan and write a migration package without UI
OrclWAMP.exe /csv <file>            Scan and save the app list as CSV without UI
```

## What's in the migration folder

| File | Purpose |
|---|---|
| `OrclWAMP.exe` | The tool itself. It starts in restore mode when it finds the package file next to it |
| `OrclWAMP-packages.json` | The app list and options |
| `Install.cmd` | Starts restore mode |
| `winget-packages.json` | Standard winget file: `winget import -i winget-packages.json` |
| `Install-Fallback.ps1` | Plain PowerShell installer, if the exe can't run |
| `Manual-Install-Report.html` | Apps to install by hand |
| `README.txt` | Short instructions |

## Building from source

You need Windows and the C# compiler from Visual Studio 2019 or later, or the free *Build Tools for Visual Studio*.

```powershell
./build.ps1              # -> bin\OrclWAMP.exe
./tools/make-icon.ps1    # regenerate assets\OrclWAMP.ico + logo.png from assets\OrclWAMP.png
```

GitHub Actions builds every push and attaches the exe to tagged releases.

## Limitations
- Only apps with a winget (or Microsoft Store) package can be installed automatically.
- App settings, licences and personal files are **not** migrated. Windows settings are limited to the list above. Default apps, Start/taskbar pins and display scaling can't be transferred, because Windows protects them or they depend on the hardware.
- Some installers ignore `--silent` and show their own window, or need a restart.

## License
MIT – see [LICENSE](LICENSE).
