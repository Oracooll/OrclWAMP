# Changelog

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
