using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OrclWAMP
{
    // ------------------------------------------------------------------ file format (Settings\settings.json)

    [DataContract]
    internal sealed class SettingsPack
    {
        [DataMember(Order = 1)] public string Tool = "OrclWAMP";
        [DataMember(Order = 2)] public int FormatVersion = 1;
        [DataMember(Order = 3)] public string CreatedUtc = "";
        [DataMember(Order = 4)] public string SourceComputer = "";
        [DataMember(Order = 5)] public string SourceOs = "";
        [DataMember(Order = 6)] public int SourceBuild;
        [DataMember(Order = 7)] public List<SettingsGroupData> Groups = new List<SettingsGroupData>();

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Tool = ""; CreatedUtc = SourceComputer = SourceOs = ""; Groups = new List<SettingsGroupData>(); }
    }

    [DataContract]
    internal sealed class SettingsGroupData
    {
        [DataMember(Order = 1)] public string Id = "";
        [DataMember(Order = 2)] public List<RegValueData> Values = new List<RegValueData>();
        [DataMember(Order = 3)] public List<NameValue> Extra = new List<NameValue>();

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Id = ""; Values = new List<RegValueData>(); Extra = new List<NameValue>(); }
        public int Count => Values.Count + Extra.Count;
    }

    [DataContract]
    internal sealed class RegValueData
    {
        [DataMember(Order = 1)] public string Key = "";   // under HKEY_CURRENT_USER
        [DataMember(Order = 2)] public string Name = "";
        [DataMember(Order = 3)] public string Kind = "";  // RegistryValueKind name; "Absent" in backups
        [DataMember(Order = 4, EmitDefaultValue = false)] public string Text;
        [DataMember(Order = 5, EmitDefaultValue = false)] public string Base64;
        [DataMember(Order = 6, EmitDefaultValue = false)] public string[] Multi;
    }

    [DataContract]
    internal sealed class NameValue
    {
        [DataMember(Order = 1)] public string Name = "";
        [DataMember(Order = 2)] public string Value = "";
    }

    // ------------------------------------------------------------------ catalog

    /// <summary>One group of Windows settings that can be carried to the new PC.</summary>
    internal sealed class SettingDef
    {
        public string Id, Name, Description, ApplyNote = "";
        public bool DefaultOn = true, Sensitive, RestartsExplorer, NeedsSignOut;
        /// <summary>Registry rules (HKCU). Names == null means every value directly in that key.</summary>
        public List<(string Key, string[] Names)> Reg = new List<(string, string[])>();
    }

    internal sealed class ApplyResult
    {
        public int Applied, Skipped;
        public bool RestartExplorer, SignOut;
        public string BackupFile;
    }

    /// <summary>Captures selected per-user Windows settings on the old PC and applies them on the new one.</summary>
    internal static class WinSettings
    {
        public const string FolderName = "Settings";
        public const string FileName = "settings.json";

        const string Adv = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";

        public static readonly SettingDef[] Catalog =
        {
            new SettingDef
            {
                Id = "touchpad", Name = "Touchpad & gestures",
                Description = "Taps, two-finger scroll direction, three- and four-finger swipes and taps, sensitivity, cursor speed",
                NeedsSignOut = true, ApplyNote = "Takes full effect after signing out",
                Reg = { (@"Software\Microsoft\Windows\CurrentVersion\PrecisionTouchPad", null) }
            },
            new SettingDef
            {
                Id = "mouse", Name = "Mouse & pointer",
                Description = "Pointer speed and acceleration, double-click speed, primary button, wheel scroll lines, pointer scheme, size and colour",
                Reg =
                {
                    (@"Control Panel\Mouse", null),
                    (@"Control Panel\Cursors", null),
                    (@"Software\Microsoft\Accessibility", new[] { "CursorSize", "CursorType", "CursorColor" }),
                    (@"Control Panel\Desktop", new[] { "WheelScrollLines", "WheelScrollChars" })
                }
            },
            new SettingDef
            {
                Id = "keyboard", Name = "Keyboard & languages",
                Description = "Input languages and keyboard layouts, layout-switch hotkey, key repeat delay and rate, NumLock at start-up, Sticky/Toggle/Filter Keys shortcuts",
                NeedsSignOut = true, ApplyNote = "Some parts take effect after signing out",
                Reg =
                {
                    (@"Control Panel\Keyboard", null),
                    (@"Keyboard Layout\Toggle", null),
                    (@"Control Panel\Accessibility\StickyKeys", new[] { "Flags" }),
                    (@"Control Panel\Accessibility\ToggleKeys", new[] { "Flags" }),
                    (@"Control Panel\Accessibility\Keyboard Response", new[] { "Flags" })
                }
            },
            new SettingDef
            {
                Id = "explorer", Name = "Taskbar, Start & File Explorer",
                Description = "File extensions, hidden files, open-to folder, taskbar alignment/buttons/combining/auto-hide, search box, Start recommendations, Snap and Alt+Tab, clock seconds",
                RestartsExplorer = true, ApplyNote = "Restarts File Explorer",
                Reg =
                {
                    (Adv, new[]
                    {
                        "Hidden", "HideFileExt", "ShowSuperHidden", "ShowCompColor", "ShowInfoTip", "ShowStatusBar", "SeparateProcess", "LaunchTo",
                        "NavPaneExpandToCurrentFolder", "NavPaneShowAllFolders", "AutoCheckSelect", "HideDrivesWithNoMedia", "ShowEncryptCompressedColor",
                        "ShowTaskViewButton", "TaskbarAl", "TaskbarGlomLevel", "MMTaskbarEnabled", "MMTaskbarGlomLevel", "MMTaskbarMode", "TaskbarSmallIcons",
                        "TaskbarMn", "TaskbarDa", "TaskbarSi", "TaskbarAnimations", "TaskbarEndTask", "ShowSecondsInSystemClock", "IsBatteryPercentageEnabled",
                        "Start_TrackDocs", "Start_TrackProgs", "Start_IrisRecommendations", "Start_AccountNotifications", "Start_Layout",
                        "SnapAssist", "EnableSnapAssistFlyout", "EnableSnapBar", "SnapFill", "JointResize", "MultiTaskingAltTabFilter",
                        "TaskbarAutoHideInTabletMode", "DisablePreviewDesktop"
                    }),
                    (@"Software\Microsoft\Windows\CurrentVersion\Search", new[] { "SearchboxTaskbarMode" }),
                    (@"Software\Microsoft\Windows\CurrentVersion\Explorer", new[] { "ShowFrequent", "ShowRecent", "ShowCloudFilesInQuickAccess" }),
                    (@"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3", new[] { "Settings" })
                }
            },
            new SettingDef
            {
                Id = "colors", Name = "Colours & theme",
                Description = "Light or dark mode for apps and Windows, accent colour, accent on Start/taskbar and title bars, transparency",
                RestartsExplorer = true,
                Reg =
                {
                    (@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", null),
                    (@"Software\Microsoft\Windows\DWM", new[] { "AccentColor", "ColorizationColor", "ColorizationAfterglow", "ColorPrevalence", "EnableWindowColorization" }),
                    (@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", new[] { "AccentPalette", "AccentColorMenu", "StartColorMenu" })
                }
            },
            new SettingDef
            {
                Id = "wallpaper", Name = "Wallpaper",
                Description = "The desktop picture (copied into the package) and how it is fitted",
                Reg = { (@"Control Panel\Desktop", new[] { "WallpaperStyle", "TileWallpaper" }) }
            },
            new SettingDef
            {
                Id = "desktop", Name = "Desktop & multitasking",
                Description = "Clipboard history, menu delay, cursor blink rate, show window contents while dragging, font smoothing, visual effects choice",
                Reg =
                {
                    (@"Control Panel\Desktop", new[] { "MenuShowDelay", "CursorBlinkRate", "DragFullWindows", "FontSmoothing", "FontSmoothingType", "ActiveWndTrkTimeout" }),
                    (@"Software\Microsoft\Clipboard", new[] { "EnableClipboardHistory" }),
                    (@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", new[] { "VisualFXSetting" })
                }
            },
            new SettingDef
            {
                Id = "regional", Name = "Regional formats",
                Description = "Date, time, number and currency formats, first day of week, measurement system, region",
                Reg =
                {
                    (@"Control Panel\International", null),
                    (@"Control Panel\International\Geo", new[] { "Nation", "Name" })
                }
            },
            new SettingDef
            {
                Id = "power", Name = "Power: screen & sleep",
                Description = "Turn off screen / sleep / hibernate after (on battery and plugged in), lid close and power button actions",
                ApplyNote = "Applied to the active power plan"
            },
            new SettingDef
            {
                Id = "fonts", Name = "User-installed fonts",
                Description = "Fonts you installed yourself (\"Install for me\") – copied into the package"
            },
            new SettingDef
            {
                Id = "wifi", Name = "Wi-Fi networks (with passwords)", DefaultOn = false, Sensitive = true,
                Description = "Saved Wi-Fi networks so the new PC connects automatically. Passwords are stored readable in the package – keep the USB stick safe"
            },
        };

        public static SettingDef Find(string id) => Catalog.FirstOrDefault(d => d.Id == id);
        public static IEnumerable<string> DefaultIds => Catalog.Where(d => d.DefaultOn).Select(d => d.Id);

        // ---------------------------------------------------------------- capture (old PC)

        /// <summary>Reads the settings. With <paramref name="dir"/> = null nothing is written (used for the preview counts).</summary>
        public static SettingsPack Capture(IEnumerable<string> ids, string dir, Action<string> log)
        {
            var pack = new SettingsPack
            {
                CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = Environment.MachineName,
                SourceOs = OsName(), SourceBuild = OsBuild()
            };
            if (dir != null) Directory.CreateDirectory(dir);
            foreach (var id in ids)
            {
                var def = Find(id);
                if (def == null) continue;
                var g = new SettingsGroupData { Id = id };
                try
                {
                    foreach (var rule in def.Reg) ReadRule(rule.Key, rule.Names, g.Values);
                    switch (id)
                    {
                        case "keyboard": if (dir != null) CaptureLanguages(g); else g.Extra.Add(new NameValue { Name = "languages", Value = "" }); break;
                        case "wallpaper": CaptureWallpaper(g, dir); break;
                        case "power": if (dir != null) CapturePower(g); else g.Extra.Add(new NameValue { Name = "power", Value = "" }); break;
                        case "fonts": CaptureFonts(g, dir); break;
                        case "wifi": CaptureWifi(g, dir); break;
                    }
                }
                catch (Exception ex) { log?.Invoke(def.Name + ": " + ex.Message); }
                if (g.Count > 0) pack.Groups.Add(g);
            }
            if (dir != null) File.WriteAllText(Path.Combine(dir, FileName), Json.Serialize(pack), new UTF8Encoding(false));
            return pack;
        }

        static void ReadRule(string keyPath, string[] names, List<RegValueData> into)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                if (k == null) return;
                foreach (var name in names ?? k.GetValueNames())
                {
                    var d = ReadValue(k, keyPath, name);
                    if (d != null) into.Add(d);
                }
            }
        }

        static RegValueData ReadValue(RegistryKey k, string keyPath, string name)
        {
            object v;
            RegistryValueKind kind;
            try
            {
                v = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (v == null) return null;
                kind = k.GetValueKind(name);
            }
            catch { return null; }
            var d = new RegValueData { Key = keyPath, Name = name, Kind = kind.ToString() };
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString: d.Text = (string)v; break;
                case RegistryValueKind.DWord: d.Text = ((int)v).ToString(); break;
                case RegistryValueKind.QWord: d.Text = ((long)v).ToString(); break;
                case RegistryValueKind.Binary: d.Base64 = Convert.ToBase64String((byte[])v); break;
                case RegistryValueKind.MultiString: d.Multi = (string[])v; break;
                default: return null;
            }
            return d;
        }

        static void CaptureLanguages(SettingsGroupData g)
        {
            // Each entry: "<language tag>|<tip>,<tip>" – e.g. "bg|0402:00040402"
            var r = RunPs(LanguageListScript);
            foreach (var line in r.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (SafeLanguageLine.IsMatch(line.Trim())) g.Extra.Add(new NameValue { Name = "language", Value = line.Trim() });
        }

        // Get-WinUserLanguageList returns ONE list object, so loop over it explicitly.
        const string LanguageListScript = "foreach ($l in (Get-WinUserLanguageList)) { $l.LanguageTag + '|' + (@($l.InputMethodTips) -join ',') }";

        static readonly Regex SafeLanguageLine = new Regex(@"^[A-Za-z0-9\-]{2,40}\|[A-Za-z0-9:{}\-,]*$");

        static void CaptureWallpaper(SettingsGroupData g, string dir)
        {
            string path = null;
            using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                path = k?.GetValue("WallPaper") as string;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                var t = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Microsoft\Windows\Themes\TranscodedWallpaper");
                path = File.Exists(t) ? t : null;
            }
            if (path == null) return;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!ImageExt.Contains(ext)) ext = ".jpg"; // TranscodedWallpaper has no extension (it is a JPEG)
            var name = "wallpaper" + ext;
            if (dir != null) File.Copy(path, Path.Combine(dir, name), true);
            g.Extra.Add(new NameValue { Name = "wallpaperFile", Value = name });
        }

        static readonly string[] ImageExt = { ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".jfif", ".webp" };

        // powercfg aliases we read/write: (subgroup, setting)
        static readonly (string Sub, string Setting)[] PowerSettings =
        {
            ("SUB_VIDEO", "VIDEOIDLE"), ("SUB_SLEEP", "STANDBYIDLE"), ("SUB_SLEEP", "HIBERNATEIDLE"),
            ("SUB_BUTTONS", "LIDACTION"), ("SUB_BUTTONS", "PBUTTONACTION")
        };

        static void CapturePower(SettingsGroupData g)
        {
            foreach (var (sub, setting) in PowerSettings)
            {
                var output = Run("powercfg.exe", $"/q SCHEME_CURRENT {sub} {setting}", 20000);
                // The last two hex numbers are the current AC and DC values (the labels are localised, the numbers are not).
                var hex = Regex.Matches(output, @"0x[0-9a-fA-F]{8}").Cast<Match>().Select(m => m.Value).ToList();
                if (hex.Count < 2) continue;
                long ac = Convert.ToInt64(hex[hex.Count - 2], 16), dc = Convert.ToInt64(hex[hex.Count - 1], 16);
                g.Extra.Add(new NameValue { Name = $"power:{sub}:{setting}", Value = ac + "," + dc });
            }
        }

        static string UserFontDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Fonts");
        const string FontsKey = @"Software\Microsoft\Windows NT\CurrentVersion\Fonts";
        static readonly string[] FontExt = { ".ttf", ".otf", ".ttc", ".fon", ".fnt" };

        static void CaptureFonts(SettingsGroupData g, string dir)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(FontsKey))
            {
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    var file = k.GetValue(name) as string;
                    if (string.IsNullOrEmpty(file)) continue;
                    if (!Path.IsPathRooted(file)) file = Path.Combine(UserFontDir, file);
                    if (!File.Exists(file) || !FontExt.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                    var fileName = Path.GetFileName(file);
                    if (dir != null)
                    {
                        var fontDir = Path.Combine(dir, "fonts");
                        Directory.CreateDirectory(fontDir);
                        File.Copy(file, Path.Combine(fontDir, fileName), true);
                    }
                    g.Extra.Add(new NameValue { Name = "font:" + name, Value = fileName });
                }
            }
        }

        static void CaptureWifi(SettingsGroupData g, string dir)
        {
            if (dir == null)
            {
                var list = Run("netsh.exe", "wlan show profiles", 20000);
                int n = Regex.Matches(list, @":\s*\S.*$", RegexOptions.Multiline).Count;
                if (list.IndexOf("wlan", StringComparison.OrdinalIgnoreCase) >= 0 && n > 0) g.Extra.Add(new NameValue { Name = "wifi", Value = "" });
                return;
            }
            var wifiDir = Path.Combine(dir, "wifi");
            Directory.CreateDirectory(wifiDir);
            Run("netsh.exe", "wlan export profile key=clear folder=" + Winget.Quote(wifiDir), 60000);
            foreach (var f in Directory.GetFiles(wifiDir, "*.xml"))
                g.Extra.Add(new NameValue { Name = "wifiProfile", Value = Path.GetFileName(f) });
            if (!Directory.EnumerateFileSystemEntries(wifiDir).Any()) Directory.Delete(wifiDir);
        }

        // ---------------------------------------------------------------- apply (new PC)

        public static SettingsPack Load(string dir)
        {
            var file = Path.Combine(dir, FileName);
            return File.Exists(file) ? Sanitize(Json.Parse<SettingsPack>(File.ReadAllText(file, Encoding.UTF8))) : null;
        }

        /// <summary>A damaged or hand-edited file may contain nulls – never let that crash the app.</summary>
        static SettingsPack Sanitize(SettingsPack p)
        {
            if (p == null) return null;
            p.Groups = (p.Groups ?? new List<SettingsGroupData>()).Where(g => g != null && !string.IsNullOrEmpty(g.Id)).ToList();
            foreach (var g in p.Groups)
            {
                g.Values = (g.Values ?? new List<RegValueData>()).Where(v => v != null && v.Key != null && v.Name != null && v.Kind != null).ToList();
                g.Extra = (g.Extra ?? new List<NameValue>()).Where(e => e != null && e.Name != null && e.Value != null).ToList();
            }
            p.SourceComputer = p.SourceComputer ?? ""; p.SourceOs = p.SourceOs ?? ""; p.CreatedUtc = p.CreatedUtc ?? "";
            return p;
        }

        public static string BackupDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrclWAMP", "Backups");

        public static string LatestBackup()
        {
            try { return Directory.GetFiles(BackupDir, "settings-backup-*.json").OrderByDescending(f => f).FirstOrDefault(); }
            catch { return null; }
        }

        /// <summary>Applies the chosen groups. A backup of the current values is written first (see <see cref="Undo"/>).</summary>
        public static ApplyResult Apply(SettingsPack pack, string dir, IEnumerable<string> ids, Action<string> log)
        {
            var res = new ApplyResult();
            var chosen = new HashSet<string>(ids);
            pack = Sanitize(pack);
            var backup = new SettingsPack { Tool = "OrclWAMP backup", CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = Environment.MachineName, SourceOs = OsName(), SourceBuild = OsBuild() };

            // The backup must be writable BEFORE anything is changed – otherwise nothing is applied.
            Directory.CreateDirectory(BackupDir);
            res.BackupFile = Path.Combine(BackupDir, "settings-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff") + ".json");
            void SaveBackup() => File.WriteAllText(res.BackupFile, Json.Serialize(backup), new UTF8Encoding(false));
            try { SaveBackup(); }
            catch (Exception ex) { throw new IOException("A backup of the current settings could not be saved, so nothing was changed. " + ex.Message, ex); }

            var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
            foreach (var g in pack.Groups.Where(x => chosen.Contains(x.Id)))
            {
                var def = Find(g.Id);
                if (def == null) continue;
                var bg = new SettingsGroupData { Id = g.Id };
                backup.Groups.Add(bg);
                log?.Invoke(def.Name + "…");

                foreach (var v in g.Values)
                {
                    // Only values from the built-in list are ever written – the file on the USB stick could have been edited.
                    if (!IsAllowed(def, v)) { res.Skipped++; log?.Invoke($"   skipped (not an allowed setting): {v.Key}\\{v.Name}"); continue; }
                    try
                    {
                        // Back up the ORIGINAL value only once, even if the file lists a value twice.
                        if (backedUp.Add(v.Key + "\\" + v.Name)) { bg.Values.Add(CurrentOrAbsent(v.Key, v.Name)); SaveBackup(); }
                        WriteValue(v);
                        res.Applied++;
                    }
                    catch (Exception ex)
                    {
                        res.Skipped++;
                        log?.Invoke($"   skipped {v.Name}: " + (ex is UnauthorizedAccessException || ex is System.Security.SecurityException ? "protected by Windows" : ex.Message));
                    }
                }

                try
                {
                    switch (g.Id)
                    {
                        case "keyboard": res.Applied += ApplyLanguages(g, bg, log); break;
                        case "wallpaper": res.Applied += ApplyWallpaper(g, bg, dir, log); break;
                        case "power": res.Applied += ApplyPower(g, bg, log); break;
                        case "fonts": res.Applied += ApplyFonts(g, dir, log); break;
                        case "wifi": res.Applied += ApplyWifi(g, dir, log); break;
                    }
                }
                catch (Exception ex) { log?.Invoke("   " + ex.Message); }

                if (def.RestartsExplorer) res.RestartExplorer = true;
                if (def.NeedsSignOut) res.SignOut = true;
                SaveBackup();
            }
            }
            finally
            {
                // Whatever happened, keep the backup of everything changed so far (Undo).
                try { SaveBackup(); } catch (Exception ex) { log?.Invoke("Could not update the backup: " + ex.Message); }
            }

            ApplyLive(chosen);
            return res;
        }

        /// <summary>Restores the values saved by the last <see cref="Apply"/>.</summary>
        public static ApplyResult Undo(string backupFile, Action<string> log)
        {
            var res = new ApplyResult();
            var pack = Sanitize(Json.Parse<SettingsPack>(File.ReadAllText(backupFile, Encoding.UTF8)));
            var ids = new HashSet<string>();
            foreach (var g in pack.Groups)
            {
                var def = Find(g.Id);
                if (def == null) continue;
                ids.Add(g.Id);
                foreach (var v in g.Values)
                {
                    if (!IsAllowed(def, v)) continue;
                    try
                    {
                        if (v.Kind == "Absent")
                            using (var k = Registry.CurrentUser.OpenSubKey(v.Key, true)) k?.DeleteValue(v.Name, false);
                        else WriteValue(v);
                        res.Applied++;
                    }
                    catch (Exception ex) { res.Skipped++; log?.Invoke($"   {v.Name}: {ex.Message}"); }
                }
                foreach (var e in g.Extra)
                {
                    try
                    {
                        if (e.Name == "wallpaperPath" && File.Exists(e.Value)) SetWallpaper(e.Value);
                        else if (e.Name == "language") { /* collected below */ }
                        else if (e.Name.StartsWith("power:")) ApplyPowerValue(e.Name, e.Value);
                    }
                    catch (Exception ex) { log?.Invoke("   " + ex.Message); }
                }
                if (g.Extra.Any(x => x.Name.StartsWith("power:"))) Run("powercfg.exe", "/setactive SCHEME_CURRENT", 20000);
                var langs = g.Extra.Where(x => x.Name == "language").Select(x => x.Value).ToList();
                if (langs.Count > 0) SetLanguages(langs, log);
                if (def.RestartsExplorer) res.RestartExplorer = true;
                if (def.NeedsSignOut) res.SignOut = true;
            }
            ApplyLive(ids);
            return res;
        }

        static bool IsAllowed(SettingDef def, RegValueData v) =>
            v != null && v.Key != null && v.Name != null &&
            def.Reg.Any(r => string.Equals(r.Key, v.Key, StringComparison.OrdinalIgnoreCase) &&
                             (r.Names == null || r.Names.Contains(v.Name, StringComparer.OrdinalIgnoreCase)));

        static RegValueData CurrentOrAbsent(string key, string name)
        {
            using (var k = Registry.CurrentUser.OpenSubKey(key))
                return (k == null ? null : ReadValue(k, key, name)) ?? new RegValueData { Key = key, Name = name, Kind = "Absent" };
        }

        static void WriteValue(RegValueData v)
        {
            if (!Enum.TryParse(v.Kind, out RegistryValueKind kind)) throw new InvalidDataException("unknown value type");
            object value;
            switch (kind)
            {
                case RegistryValueKind.String:
                case RegistryValueKind.ExpandString: value = Limit(v.Text ?? ""); break;
                case RegistryValueKind.DWord: value = int.Parse(v.Text); break;
                case RegistryValueKind.QWord: value = long.Parse(v.Text); break;
                case RegistryValueKind.Binary:
                    var bytes = Convert.FromBase64String(v.Base64 ?? "");
                    if (bytes.Length > 4096) throw new InvalidDataException("value too large");
                    value = bytes; break;
                case RegistryValueKind.MultiString: value = (v.Multi ?? new string[0]).Select(Limit).ToArray(); break;
                default: throw new InvalidDataException("unsupported value type");
            }
            using (var k = Registry.CurrentUser.CreateSubKey(v.Key)) k.SetValue(v.Name, value, kind);
        }

        static string Limit(string s) => s.Length > 4096 ? throw new InvalidDataException("value too long") : s;

        static int ApplyLanguages(SettingsGroupData g, SettingsGroupData backup, Action<string> log)
        {
            var langs = g.Extra.Where(x => x.Name == "language" && SafeLanguageLine.IsMatch(x.Value)).Select(x => x.Value).ToList();
            if (langs.Count == 0) return 0;
            foreach (var line in RunPs(LanguageListScript)
                         .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                if (SafeLanguageLine.IsMatch(line.Trim())) backup.Extra.Add(new NameValue { Name = "language", Value = line.Trim() });
            return SetLanguages(langs, log) ? 1 : 0;
        }

        static bool SetLanguages(List<string> langs, Action<string> log)
        {
            langs = langs.Where(l => SafeLanguageLine.IsMatch(l)).ToList();
            if (langs.Count == 0) return false;
            var sb = new StringBuilder("$ErrorActionPreference='Stop'; $l = $null; ");
            foreach (var line in langs)
            {
                var parts = line.Split('|');
                sb.Append($"if ($l -eq $null) {{ $l = New-WinUserLanguageList '{parts[0]}' }} else {{ $l.Add('{parts[0]}') }}; ");
                sb.Append("$e = $l[$l.Count-1]; ");
                var tips = parts[1].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                if (tips.Length > 0)
                {
                    sb.Append("$e.InputMethodTips.Clear(); ");
                    foreach (var t in tips) sb.Append($"$e.InputMethodTips.Add('{t}'); ");
                }
            }
            sb.Append("Set-WinUserLanguageList $l -Force; 'ok'");
            var r = RunPs(sb.ToString());
            bool ok = r.Contains("ok");
            log?.Invoke(ok ? "   input languages and keyboard layouts set: " + string.Join(", ", langs.Select(x => x.Split('|')[0]))
                           : "   could not set the input languages: " + r.Trim());
            return ok;
        }

        static int ApplyWallpaper(SettingsGroupData g, SettingsGroupData backup, string dir, Action<string> log)
        {
            var name = g.Extra.FirstOrDefault(x => x.Name == "wallpaperFile")?.Value;
            if (string.IsNullOrEmpty(name) || name != Path.GetFileName(name) || !ImageExt.Contains(Path.GetExtension(name).ToLowerInvariant())) return 0;
            var src = Path.Combine(dir, name);
            if (!File.Exists(src)) return 0;
            // Keep a copy of the current picture so Undo works even if Windows only has its internal copy.
            var keep = new SettingsGroupData();
            CaptureWallpaper(keep, BackupDir);
            var kept = keep.Extra.FirstOrDefault()?.Value;
            if (kept != null)
            {
                var keptPath = Path.Combine(BackupDir, "wallpaper-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + Path.GetExtension(kept));
                try { File.Move(Path.Combine(BackupDir, kept), keptPath); backup.Extra.Add(new NameValue { Name = "wallpaperPath", Value = keptPath }); } catch { }
            }
            var targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrclWAMP", "Wallpaper");
            Directory.CreateDirectory(targetDir);
            var target = Path.Combine(targetDir, name);
            File.Copy(src, target, true);
            SetWallpaper(target);
            log?.Invoke("   wallpaper set");
            return 1;
        }

        static void SetWallpaper(string path) => SystemParametersInfo(0x0014 /* SPI_SETDESKWALLPAPER */, 0, path, 0x01 | 0x02);

        static int ApplyPower(SettingsGroupData g, SettingsGroupData backup, Action<string> log)
        {
            int n = 0;
            var current = new SettingsGroupData();
            CapturePower(current);
            backup.Extra.AddRange(current.Extra);
            foreach (var e in g.Extra.Where(x => x.Name.StartsWith("power:")))
            {
                if (ApplyPowerValue(e.Name, e.Value)) n++;
                else log?.Invoke("   could not set " + e.Name.Substring(6));
            }
            Run("powercfg.exe", "/setactive SCHEME_CURRENT", 20000);
            if (n > 0) log?.Invoke($"   {n} power setting(s) applied");
            return n;
        }

        static bool ApplyPowerValue(string name, string value)
        {
            var parts = name.Split(':');
            if (parts.Length != 3 || !PowerSettings.Any(p => p.Sub == parts[1] && p.Setting == parts[2])) return false;
            var nums = value.Split(',');
            if (nums.Length != 2 || !long.TryParse(nums[0], out var ac) || !long.TryParse(nums[1], out var dc) || ac < 0 || dc < 0) return false;
            var r1 = RunExit("powercfg.exe", $"/setacvalueindex SCHEME_CURRENT {parts[1]} {parts[2]} {ac}");
            var r2 = RunExit("powercfg.exe", $"/setdcvalueindex SCHEME_CURRENT {parts[1]} {parts[2]} {dc}");
            return r1 == 0 || r2 == 0;
        }

        static int ApplyFonts(SettingsGroupData g, string dir, Action<string> log)
        {
            int n = 0;
            Directory.CreateDirectory(UserFontDir);
            foreach (var e in g.Extra.Where(x => x.Name.StartsWith("font:")))
            {
                var file = e.Value;
                if (file != Path.GetFileName(file) || !FontExt.Contains(Path.GetExtension(file).ToLowerInvariant())) continue;
                var src = Path.Combine(dir, "fonts", file);
                if (!File.Exists(src)) continue;
                var target = Path.Combine(UserFontDir, file);
                try
                {
                    if (!File.Exists(target)) File.Copy(src, target);
                    using (var k = Registry.CurrentUser.CreateSubKey(FontsKey)) k.SetValue(Limit(e.Name.Substring(5)), target, RegistryValueKind.String);
                    AddFontResource(target);
                    n++;
                }
                catch (Exception ex) { log?.Invoke($"   font {file}: {ex.Message}"); }
            }
            if (n > 0)
            {
                SendMessageTimeout(new IntPtr(0xFFFF), 0x001D /* WM_FONTCHANGE */, IntPtr.Zero, null, 0x0002, 3000, out _);
                log?.Invoke($"   {n} font(s) installed");
            }
            return n;
        }

        static int ApplyWifi(SettingsGroupData g, string dir, Action<string> log)
        {
            int n = 0;
            foreach (var e in g.Extra.Where(x => x.Name == "wifiProfile"))
            {
                var file = e.Value;
                if (file != Path.GetFileName(file) || !file.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                var path = Path.Combine(dir, "wifi", file);
                if (!File.Exists(path)) continue;
                if (RunExit("netsh.exe", "wlan add profile filename=" + Winget.Quote(path) + " user=current") == 0) n++;
                else log?.Invoke("   could not add Wi-Fi profile " + file);
            }
            if (n > 0) log?.Invoke($"   {n} Wi-Fi network(s) added");
            return n;
        }

        /// <summary>Makes changes visible without signing out where Windows allows it.</summary>
        static void ApplyLive(ICollection<string> ids)
        {
            try
            {
                if (ids.Contains("mouse"))
                {
                    SystemParametersInfo(0x0057 /* SPI_SETCURSORS */, 0, null, 0x03);
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Mouse"))
                    {
                        if (int.TryParse(k?.GetValue("MouseSensitivity") as string, out var speed)) SystemParametersInfo(0x0071 /* SPI_SETMOUSESPEED */, 0, new IntPtr(speed), 0x03);
                        if (int.TryParse(k?.GetValue("DoubleClickSpeed") as string, out var dbl)) SystemParametersInfo(0x0020 /* SPI_SETDOUBLECLICKTIME */, (uint)dbl, IntPtr.Zero, 0x03);
                        if (int.TryParse(k?.GetValue("SwapMouseButtons") as string, out var swap)) SystemParametersInfo(0x0021 /* SPI_SETMOUSEBUTTONSWAP */, (uint)swap, IntPtr.Zero, 0x03);
                    }
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop"))
                        if (int.TryParse(k?.GetValue("WheelScrollLines") as string, out var lines)) SystemParametersInfo(0x0069 /* SPI_SETWHEELSCROLLLINES */, (uint)lines, IntPtr.Zero, 0x03);
                }
                if (ids.Contains("keyboard"))
                    using (var k = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"))
                    {
                        if (uint.TryParse(k?.GetValue("KeyboardDelay") as string, out var delay)) SystemParametersInfo(0x0017 /* SPI_SETKEYBOARDDELAY */, delay, IntPtr.Zero, 0x03);
                        if (uint.TryParse(k?.GetValue("KeyboardSpeed") as string, out var rate)) SystemParametersInfo(0x000B /* SPI_SETKEYBOARDSPEED */, rate, IntPtr.Zero, 0x03);
                    }
                if (ids.Contains("colors")) Broadcast("ImmersiveColorSet");
                if (ids.Contains("regional")) Broadcast("intl");
                Broadcast(null);
            }
            catch { }
        }

        static void Broadcast(string what) =>
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A /* WM_SETTINGCHANGE */, IntPtr.Zero, what, 0x0002 /* SMTO_ABORTIFHUNG */, 3000, out _);

        public static void RestartExplorer()
        {
            try
            {
                int session = Process.GetCurrentProcess().SessionId; // only this user's desktop, never other signed-in users
                foreach (var p in Process.GetProcessesByName("explorer").Where(x => x.SessionId == session)) { try { p.Kill(); p.WaitForExit(5000); } catch { } finally { p.Dispose(); } }
                for (int i = 0; i < 10; i++) // Windows usually restarts the shell by itself
                {
                    System.Threading.Thread.Sleep(500);
                    if (Process.GetProcessesByName("explorer").Any(x => x.SessionId == session)) return;
                }
                Process.Start("explorer.exe")?.Dispose();
            }
            catch { }
        }

        // ---------------------------------------------------------------- helpers

        public static string OsName()
        {
            try
            {
                using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
                {
                    var build = OsBuild();
                    var product = (k?.GetValue("ProductName") as string ?? "Windows").Trim();
                    if (build >= 22000) product = product.Replace("Windows 10", "Windows 11"); // ProductName still says 10 on Windows 11
                    return $"{product} {k?.GetValue("DisplayVersion")} (build {build})".Replace("  ", " ");
                }
            }
            catch { return "Windows"; }
        }

        public static int OsBuild()
        {
            try { using (var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion")) return int.Parse(k?.GetValue("CurrentBuild") as string ?? "0"); }
            catch { return Environment.OSVersion.Version.Build; }
        }

        static string RunPs(string script) =>
            Run("powershell.exe", "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script)), 120000);

        static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
                using (var p = Process.Start(psi))
                {
                    var outTask = p.StandardOutput.ReadToEndAsync();
                    var errTask = p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                    return (outTask.Wait(5000) ? outTask.Result : "") + (errTask.Wait(1000) ? errTask.Result : "");
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static int RunExit(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(30000)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool SystemParametersInfo(uint action, uint param, string vparam, uint winIni);
        [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint param, IntPtr vparam, uint winIni);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wp, string lp, uint flags, uint timeout, out IntPtr result);
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern int AddFontResource(string file);
    }
}
