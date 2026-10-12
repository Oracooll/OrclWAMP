# Changelog

## 1.4.002
- **Unattended app updates**: `OrclWAMP.exe /upgrade /unattended` updates every app that has a newer winget version and isn't left out, without asking. When there is something to update it shows a tray icon (right-click it to open the log or stop after the current app) and a summary when it's done and writes a log to `%LOCALAPPDATA%\OrclWAMP\logs`.
- **Weekly schedule**: App updates → **Weekly schedule…** creates a Windows Task Scheduler task that runs the unattended update once a week on the day and time you choose.
  - It runs only while you are signed in, and catches up at the next chance if the PC was off or asleep.
  - It always runs with your normal rights, never silently as administrator, so no other program can use it to get admin rights. Apps that need admin rights to update may show a UAC prompt.
  - If OrclWAMP runs from a USB stick or network drive, it offers to copy itself to `%LOCALAPPDATA%\OrclWAMP` first.
  - Shows the last automatic run and opens the log folder. **Run now** starts it straight away.
- **Choose what updates automatically**: right-click an app in App updates → **Leave out of weekly updates**. Runtimes (VC++, .NET…) are left out unless you tick **Also update runtimes**. A new **Weekly update** column shows what will happen to each app.
- The App updates window and a scheduled run never update at the same time, and a busy installer is retried once after a minute.
- App updates now report an error instead of "Everything is up to date" when winget can't check.

## 1.4.001
- **Personal files**: copy Desktop, Documents, Pictures, Music, Videos, Downloads and your own folders into the package and back onto the new PC.
  - Shows sizes first.
  - Leaves out OneDrive folders.
  - Skips identical files, and keeps both copies on conflicts.
  - Warns about FAT32 and free space.
- **App settings & bookmarks**: Edge, Chrome and Brave bookmarks, the Firefox bookmark backup, Windows Terminal, VS Code (settings, key bindings, snippets and its extensions reinstalled), Notepad++, Double Commander, Total Commander, PowerShell profiles, Git and SSH keys. Applied with a backup and Undo, and only to known locations.
- **Password-protected package**: Wi-Fi passwords and SSH keys are encrypted with your password (AES-256, PBKDF2-SHA256, HMAC check). They're asked for only when needed on the new PC.
- **App updates** (upgrade mode): see which apps on any PC have newer versions and update the ticked ones. Also available as `OrclWAMP.exe /upgrade`.
- **Migration report**: one page on the new PC covering installed and failed apps, manual apps with sure links, settings, app settings and copied files. Unattended restores write it automatically.
- **Update check**: a link appears in the header when a newer OrclWAMP is on GitHub. It checks at most once a day and never downloads anything by itself.
- **Bulgarian interface**: EN / BG switch in the header of every window. The choice is remembered.
- Unattended restore now also applies app settings and copies personal files after installing the apps.

## 1.3.002
- Long name changed to **Oracooll Winget App Migration Program** so it matches the OrclWAMP acronym. This covers the window titles, header, About box, exe properties, README and package README.

## 1.3.001
- **Windows settings migration**: take personal Windows settings to the new PC along with the apps:
  - **Touchpad & gestures**: taps, scroll direction, three- and four-finger swipes and taps, sensitivity
  - **Mouse & pointer**: speed, double-click, buttons, wheel, pointer scheme, size and colour
  - **Keyboard & languages**: input languages and keyboard layouts, switch hotkey, repeat rate, NumLock, Sticky Keys prompt
  - **Taskbar, Start & File Explorer**: file extensions, hidden files, taskbar alignment, buttons and auto-hide, Snap, Alt+Tab, search box
  - **Colours & theme**, **Wallpaper**, **Desktop & multitasking** (clipboard history, visual effects…), **Regional formats**
  - **Power**: screen-off, sleep and hibernate timers, lid and power-button actions
  - **User-installed fonts**, and **Wi-Fi networks** (opt-in, because the passwords are stored readable)
- Settings are applied on the new PC with a **backup and Undo**. Only settings from the built-in list can ever be written, even from an edited package.
- Unattended restore applies the settings before installing the apps.
- **Log pane button**: move the log between the bottom and the right side. The choice is remembered.

## 1.2.001
- **Light / Dark / System theme** toggle in the header of every window. It's saved per user, System follows Windows live, and title bars, menus, lists and scrollbars are themed.
- **Manual apps & downloads** window (scanner and restore mode): download apps winget can't install one by one or all at once into `Downloads\OrclWAMP`, open official download pages, set your own links, and run downloaded installers.
- **Only sure links**: a download link is shown only when OrclWAMP is sure of it (a checked official link, your own link, or a web app's address). Otherwise it says "No link".
- **Find winget packages for manual apps**: an exact-name winget search turns Store and renamed installs into automatic installs. You confirm every match.
- Links you set are saved in the migration package. The printable checklist shows the same links.
- Publisher and website are read from the Windows uninstall entries.

## 1.1.001
- First public release: scan, migration package, restore mode with winget repair, retries, logs, elevation handling, profiles, CSV export, fallback scripts.
- The app name and version appear at the top left. Oracooll icon.
