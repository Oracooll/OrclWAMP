# OrclWAMP – Feature Planning

Notes from the design passes that shaped OrclWAMP (Oracooll Winget App Migration Tool).

## Round 1 – Core idea (the minimum that works)
1. Scan the old PC for installed apps.
2. Work out which of them winget can install.
3. Write an installer to a USB stick.
4. On the new PC, run it and install everything.

## Round 2 – "What would a real user trip over?"
| Problem | Feature |
|---|---|
| A script on its own is hard to review and gives no feedback | The **same portable exe** is copied to the USB. On the new PC it opens in **Restore mode** with a checklist, live progress and a log |
| The new PC may have no .NET runtime | Built on **.NET Framework 4.8**, which ships with Windows 10/11. One small exe, no installer, nothing else needed |
| Runtimes clutter the list (VC++ redists, .NET, WindowsAppRuntime, VCLibs, UI.Xaml) | These are **unticked by default**. Installers bring them in as dependencies anyway |
| Apps that winget can't install get forgotten | A **"Manual install" report** (HTML) lists every app winget couldn't find, with a search link for each |
| Fresh Windows may have an old or missing winget | Restore mode **checks for winget** and offers to install App Installer from Microsoft (aka.ms/getwinget) |
| Apps that are already installed shouldn't be reinstalled | Restore mode **skips anything already installed** (the target PC is scanned before installing) |
| One broken installer shouldn't stop the run | **Keeps going after errors**, has a per-package **timeout**, and a **Retry failed** button |
| Lots of UAC prompts | **Run as administrator** option (Install.cmd asks for elevation once) |
| The PC goes to sleep partway through a long install | Sleep is **blocked** while installing |
| No fallback if the exe gets blocked by SmartScreen or policy | The package also contains a standard winget **`packages.json`** (for `winget import`) and a plain **`Install-Fallback.ps1`** script |

## Round 3 – Power-user and polish
- **Search and filter** the list, plus a category filter (Winget / Store / Not available / All).
- **Select all / none / invert**, and click a column header to sort.
- **Pin versions** (install the exact old version) or install the latest (the default).
- **Add packages by hand**: search the winget catalog from inside the app and add apps the old PC never had.
- **Save and load profiles** (`.orclwamp.json`) so you can reuse a curated list, for example a "standard office PC" set. Standard `winget export` JSON files can also be imported.
- **Export the list to CSV.**
- **Unattended mode** (`OrclWAMP.exe /restore /unattended`) for a fully automatic run.
- **Restart when finished** option.
- **Logs** are written next to the manifest on the USB, so you can check them later.
- **Internet check** before installing.
- **Re-scan** button and right-click actions (open winget.run / the publisher's page, copy ID).

## Round 4 – User requests
- **Light / Dark / System theme** toggle in the header of every window. The choice is saved and System follows Windows live.
- **Manual apps & downloads** window: download apps winget can't install one by one or all at once into `Downloads\OrclWAMP`. If there's no direct file, the official download page opens instead. You can set your own link and run downloaded installers.
- **Only sure links**: no link is shown unless OrclWAMP is sure of it (checked official links, user links, web-app addresses). Publisher homepages from the registry are shown as "Publisher website" only.
- **Find winget packages for manual apps**: an exact-name winget search turns many "manual" apps (Store and renamed installs) into automatic installs. You confirm every match.

## Rejected / deferred
- Offline installers (downloading every installer onto the USB): many installers don't allow redistribution, it needs lots of space, and winget's `download` support is uneven. *Deferred.*
- Migrating app settings and data: out of scope and risky.
- Choosing per-package scope or install location: too few users need it, and installers often ignore it anyway.
