using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Text.RegularExpressions;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    [DataContract]
    internal sealed class AppConfigPack
    {
        [DataMember(Order = 1)] public string Tool = "OrclWAMP";
        [DataMember(Order = 2)] public string CreatedUtc = "";
        [DataMember(Order = 3)] public string SourceComputer = "";
        [DataMember(Order = 4)] public List<AppConfigData> Apps = new List<AppConfigData>();
        [OnDeserializing] void OnDeserializing(StreamingContext c) { Tool = CreatedUtc = SourceComputer = ""; Apps = new List<AppConfigData>(); }
    }

    [DataContract]
    internal sealed class AppConfigData
    {
        [DataMember(Order = 1)] public string Id = "";
        [DataMember(Order = 2)] public List<ConfigEntry> Entries = new List<ConfigEntry>();
        [DataMember(Order = 3)] public List<string> Extra = new List<string>();   // VS Code extension IDs, Firefox backup file name
        [OnDeserializing] void OnDeserializing(StreamingContext c) { Id = ""; Entries = new List<ConfigEntry>(); Extra = new List<string>(); }
    }

    [DataContract]
    internal sealed class ConfigEntry
    {
        [DataMember(Order = 1)] public int Index;
        [DataMember(Order = 2)] public string Token = "";
        [DataMember(Order = 3)] public string Rel = "";
        [DataMember(Order = 4)] public bool Dir;
        [DataMember(Order = 5)] public bool Existed;   // backups only
        [DataMember(Order = 6, EmitDefaultValue = false)] public List<string> Files; // backups of folders: the files Apply wrote (relative)
    }

    internal sealed class AppConfigDef
    {
        public string Id, Name, Description, Note = "";
        public bool Sensitive;
        public bool RunsCode;   // contains commands that run on the new PC – never applied without a person confirming
        public string[] Processes = new string[0];
        public List<(string Token, string Rel, bool Dir)> Paths = new List<(string, string, bool)>();
        public bool VsCodeExtensions, FirefoxBookmarks;
    }

    /// <summary>Settings of popular apps (config files) and browser bookmarks.</summary>
    internal static class AppConfigs
    {
        public const string FolderName = "AppConfigs";
        public const string FileName = "appconfigs.json";
        const long MaxDirBytes = 50L * 1024 * 1024;

        const string Terminal = @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json";
        const string TerminalPreview = @"Packages\Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe\LocalState\settings.json";

        public static readonly AppConfigDef[] Catalog =
        {
            new AppConfigDef { Id = "edge", Name = "Microsoft Edge bookmarks", Description = "Favourites bar and all bookmarks (default profile)",
                Note = "If you sign in to Edge with sync on, synced favourites are merged later", Processes = new[] { "msedge" },
                Paths = { ("LOCALAPPDATA", @"Microsoft\Edge\User Data\Default\Bookmarks", false) } },
            new AppConfigDef { Id = "chrome", Name = "Google Chrome bookmarks", Description = "Bookmarks bar and all bookmarks (default profile)", Processes = new[] { "chrome" },
                Paths = { ("LOCALAPPDATA", @"Google\Chrome\User Data\Default\Bookmarks", false) } },
            new AppConfigDef { Id = "brave", Name = "Brave bookmarks", Description = "Bookmarks bar and all bookmarks (default profile)", Processes = new[] { "brave" },
                Paths = { ("LOCALAPPDATA", @"BraveSoftware\Brave-Browser\User Data\Default\Bookmarks", false) } },
            new AppConfigDef { Id = "firefox", Name = "Firefox bookmarks", Description = "Latest bookmark backup – placed on the Desktop to restore in Firefox",
                Note = "Firefox: Bookmarks > Manage bookmarks > Import and Backup > Restore > Choose File", FirefoxBookmarks = true },
            new AppConfigDef { Id = "terminal", RunsCode = true, Name = "Windows Terminal", Description = "Profiles, colour schemes, key bindings (settings.json)", Processes = new[] { "WindowsTerminal" },
                Paths = { ("LOCALAPPDATA", Terminal, false), ("LOCALAPPDATA", TerminalPreview, false) } },
            new AppConfigDef { Id = "vscode", RunsCode = true, Name = "Visual Studio Code", Description = "Settings, key bindings, snippets and the list of extensions (reinstalled)", Processes = new[] { "Code" },
                VsCodeExtensions = true,
                Paths = { ("APPDATA", @"Code\User\settings.json", false), ("APPDATA", @"Code\User\keybindings.json", false), ("APPDATA", @"Code\User\snippets", true) } },
            new AppConfigDef { Id = "notepadpp", RunsCode = true, Name = "Notepad++", Description = "Preferences, shortcuts, styles, context menu, user languages and themes", Processes = new[] { "notepad++" },
                Paths = { ("APPDATA", @"Notepad++\config.xml", false), ("APPDATA", @"Notepad++\shortcuts.xml", false), ("APPDATA", @"Notepad++\stylers.xml", false),
                          ("APPDATA", @"Notepad++\contextMenu.xml", false), ("APPDATA", @"Notepad++\userDefineLangs", true), ("APPDATA", @"Notepad++\themes", true) } },
            new AppConfigDef { Id = "doublecmd", RunsCode = true, Name = "Double Commander", Description = "All settings, hotkeys, favourite folders", Processes = new[] { "doublecmd" },
                Paths = { ("APPDATA", "doublecmd", true) } },
            new AppConfigDef { Id = "totalcmd", RunsCode = true, Name = "Total Commander", Description = "Main settings (wincmd.ini)", Processes = new[] { "TOTALCMD64", "TOTALCMD" },
                Paths = { ("APPDATA", @"GHISLER\wincmd.ini", false) } },
            new AppConfigDef { Id = "powershell", RunsCode = true, Name = "PowerShell profiles", Description = "Your PowerShell start-up scripts (Windows PowerShell and PowerShell 7)",
                Paths = { ("DOCUMENTS", @"WindowsPowerShell\Microsoft.PowerShell_profile.ps1", false), ("DOCUMENTS", @"WindowsPowerShell\profile.ps1", false),
                          ("DOCUMENTS", @"PowerShell\Microsoft.PowerShell_profile.ps1", false), ("DOCUMENTS", @"PowerShell\profile.ps1", false) } },
            new AppConfigDef { Id = "git", RunsCode = true, Name = "Git", Description = "Your name, e-mail and Git options (.gitconfig)",
                Paths = { ("USERPROFILE", ".gitconfig", false) } },
            new AppConfigDef { Id = "ssh", Name = "SSH keys", Description = "Your SSH keys and config (.ssh) – only in a password-protected package", Sensitive = true,
                Paths = { ("USERPROFILE", ".ssh", true) } },
        };

        public static AppConfigDef Find(string id) => Catalog.FirstOrDefault(d => d.Id == id);

        public static string Resolve(string token)
        {
            switch (token)
            {
                case "APPDATA": return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                case "LOCALAPPDATA": return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                case "USERPROFILE": return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                case "DOCUMENTS": return Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                default: return null;
            }
        }

        /// <summary>Which catalog apps have something to take on this PC.</summary>
        public static List<AppConfigDef> Detect()
        {
            var list = new List<AppConfigDef>();
            foreach (var d in Catalog)
            {
                bool found = d.Paths.Any(p => Exists(Path.Combine(Resolve(p.Token), p.Rel), p.Dir));
                if (d.FirefoxBookmarks) found = FirefoxBackup() != null;
                if (found) list.Add(d);
            }
            return list;
        }

        static bool Exists(string path, bool dir) => dir ? Directory.Exists(path) : File.Exists(path);

        // ---------------------------------------------------------------- capture (old PC)

        public static AppConfigPack Capture(IEnumerable<string> ids, string dir, Action<string> log)
        {
            var pack = new AppConfigPack { CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = Environment.MachineName };
            Directory.CreateDirectory(dir);
            foreach (var id in ids)
            {
                var def = Find(id);
                if (def == null) continue;
                var data = new AppConfigData { Id = id };
                try
                {
                    for (int i = 0; i < def.Paths.Count; i++)
                    {
                        var p = def.Paths[i];
                        var src = Path.Combine(Resolve(p.Token), p.Rel);
                        if (!Exists(src, p.Dir)) continue;
                        var store = Path.Combine(dir, id, i.ToString());
                        if (p.Dir)
                        {
                            if (PersonalFiles.Measure(src, System.Threading.CancellationToken.None).Bytes > MaxDirBytes)
                            {
                                log?.Invoke(F("{0}: {1} is larger than 50 MB – skipped", def.Name, p.Rel));
                                continue;
                            }
                            PersonalFiles.Copy(src, store, ExistingFile.Overwrite, "", 0, null, log, System.Threading.CancellationToken.None, null, exact: true);
                        }
                        else
                        {
                            Directory.CreateDirectory(store);
                            CopyShared(src, Path.Combine(store, Path.GetFileName(p.Rel)));
                        }
                        data.Entries.Add(new ConfigEntry { Index = i, Token = p.Token, Rel = p.Rel, Dir = p.Dir });
                    }
                    if (def.VsCodeExtensions)
                    {
                        var code = CodeCli();
                        if (code != null)
                            foreach (var line in Run(code, "--list-extensions", 60000).Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                                if (SafeExtension.IsMatch(line.Trim())) data.Extra.Add(line.Trim());
                    }
                    if (def.FirefoxBookmarks)
                    {
                        var backup = FirefoxBackup();
                        if (backup != null)
                        {
                            var store = Path.Combine(dir, id);
                            Directory.CreateDirectory(store);
                            var name = Path.GetFileName(backup);
                            CopyShared(backup, Path.Combine(store, name));
                            data.Extra.Add(name);
                        }
                    }
                }
                catch (Exception ex) { log?.Invoke(def.Name + ": " + ex.Message); }
                if (data.Entries.Count > 0 || data.Extra.Count > 0) pack.Apps.Add(data);
            }
            File.WriteAllText(Path.Combine(dir, FileName), Json.Serialize(pack), new UTF8Encoding(false));
            return pack;
        }

        /// <summary>Package-relative paths of files that must go into the password-protected container.</summary>
        public static IEnumerable<string> SensitiveFiles(string packageDir)
        {
            foreach (var d in Catalog.Where(x => x.Sensitive))
            {
                var dir = Path.Combine(packageDir, FolderName, d.Id);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    yield return f.Substring(packageDir.TrimEnd('\\').Length + 1);
            }
        }

        static void CopyShared(string src, string dst)
        {
            // Browsers keep their files open – read with sharing so this works while they run.
            using (var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var o = File.Create(dst)) i.CopyTo(o);
        }

        static readonly Regex SafeExtension = new Regex(@"^[A-Za-z0-9][A-Za-z0-9\-]*\.[A-Za-z0-9][A-Za-z0-9\-.]*$");

        static string CodeCli()
        {
            foreach (var p in new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\bin\code.cmd"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\bin\code.cmd")
            })
                if (File.Exists(p)) return p;
            return null;
        }

        static string FirefoxBackup()
        {
            try
            {
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), @"Mozilla\Firefox");
                var ini = Path.Combine(root, "profiles.ini");
                if (!File.Exists(ini)) return null;
                // Prefer the profile marked as default by the installer ([Install…] Default=…), else any Default=1 profile.
                string profile = null;
                var lines = File.ReadAllLines(ini);
                string section = "";
                string lastPath = null; bool lastRelative = true;
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.StartsWith("[")) { section = line; continue; }
                    if (section.StartsWith("[Install", StringComparison.OrdinalIgnoreCase) && line.StartsWith("Default=")) { profile = Path.Combine(root, line.Substring(8).Replace('/', '\\')); break; }
                    if (line.StartsWith("Path=")) lastPath = line.Substring(5).Replace('/', '\\');
                    if (line.StartsWith("IsRelative=")) lastRelative = line.EndsWith("1");
                    if (line == "Default=1" && lastPath != null && profile == null) profile = lastRelative ? Path.Combine(root, lastPath) : lastPath;
                }
                if (profile == null) return null;
                var dir = Path.Combine(profile, "bookmarkbackups");
                return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.jsonlz4").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- apply (new PC)

        /// <summary>Adds the apps of <paramref name="extra"/> to the appconfigs.json in <paramref name="dir"/> (creating it if needed).</summary>
        public static void Merge(string dir, AppConfigPack extra)
        {
            if (extra == null || extra.Apps.Count == 0) return;
            Directory.CreateDirectory(dir);
            var pack = Load(dir) ?? new AppConfigPack { CreatedUtc = extra.CreatedUtc, SourceComputer = extra.SourceComputer };
            pack.Apps.RemoveAll(a => extra.Apps.Any(x => x.Id == a.Id));
            pack.Apps.AddRange(extra.Apps);
            File.WriteAllText(Path.Combine(dir, FileName), Json.Serialize(pack), new UTF8Encoding(false));
        }

        public static AppConfigPack Load(string dir)
        {
            try
            {
                var file = Path.Combine(dir, FileName);
                if (!File.Exists(file)) return null;
                var p = Json.Parse<AppConfigPack>(File.ReadAllText(file, Encoding.UTF8));
                if (p == null) return null;
                p.Apps = (p.Apps ?? new List<AppConfigData>()).Where(a => a != null && Find(a.Id ?? "") != null).ToList();
                foreach (var a in p.Apps)
                {
                    a.Entries = (a.Entries ?? new List<ConfigEntry>()).Where(e => e != null && e.Token != null && e.Rel != null).ToList();
                    a.Extra = (a.Extra ?? new List<string>()).Where(x => x != null).ToList();
                }
                p.SourceComputer = p.SourceComputer ?? "";
                return p;
            }
            catch { return null; }
        }

        /// <summary>Names of the chosen apps that are running right now (their files would be overwritten when they close).</summary>
        public static List<string> Running(IEnumerable<string> ids) =>
            ids.Select(Find).Where(d => d != null && d.Processes.Any(p => Process.GetProcessesByName(p).Length > 0)).Select(d => d.Name).ToList();

        public static string BackupRoot => Path.Combine(WinSettings.BackupDir, "apps");

        /// <summary>Copies the configs into place. Existing files are backed up first (see <see cref="Undo"/>).</summary>
        public static ApplyResult Apply(AppConfigPack pack, string dir, IEnumerable<string> ids, string secretsRoot, Action<string> log)
        {
            var res = new ApplyResult();
            var backupDir = Path.Combine(BackupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss-fff"));
            Directory.CreateDirectory(backupDir);
            var backup = new AppConfigPack { Tool = "OrclWAMP backup", CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = Environment.MachineName };
            void SaveBackup() => File.WriteAllText(Path.Combine(backupDir, FileName), Json.Serialize(backup), new UTF8Encoding(false));
            SaveBackup();
            res.BackupFile = Path.Combine(backupDir, FileName);
            var chosen = new HashSet<string>(ids);
            try
            {
                foreach (var app in pack.Apps.Where(a => chosen.Contains(a.Id)))
                {
                    var def = Find(app.Id);
                    var bak = new AppConfigData { Id = app.Id };
                    backup.Apps.Add(bak);
                    log?.Invoke(T(def.Name) + "…");
                    var root = def.Sensitive ? (secretsRoot == null ? null : Path.Combine(secretsRoot, FolderName)) : dir;
                    if (root == null) { log?.Invoke("   " + T("skipped – the package password was not entered")); res.Skipped++; continue; }

                    foreach (var e in app.Entries)
                    {
                        // Only catalog locations may be written – the package could have been edited.
                        if (e.Index < 0 || e.Index >= def.Paths.Count) { res.Skipped++; continue; }
                        var p = def.Paths[e.Index];
                        if (p.Token != e.Token || !string.Equals(p.Rel, e.Rel, StringComparison.OrdinalIgnoreCase) || p.Dir != e.Dir) { res.Skipped++; log?.Invoke("   " + T("skipped (not an allowed location)")); continue; }
                        var store = Path.Combine(root, app.Id, e.Index.ToString());
                        var source = p.Dir ? store : Path.Combine(store, Path.GetFileName(p.Rel));
                        var target = Path.Combine(Resolve(p.Token), p.Rel);
                        if (!Exists(source, p.Dir)) { res.Skipped++; continue; }
                        try
                        {
                            var be = new ConfigEntry { Index = e.Index, Token = p.Token, Rel = p.Rel, Dir = p.Dir, Existed = Exists(target, p.Dir) };
                            if (be.Existed)
                            {
                                var keep = Path.Combine(backupDir, app.Id, e.Index.ToString());
                                if (p.Dir)
                                {
                                    // Links or unreadable sub-folders can't be backed up faithfully – then don't replace the folder at all.
                                    if (PersonalFiles.HasSpecialEntries(target)) { res.Skipped++; log?.Invoke("   " + F("{0}: not replaced – it contains links or folders that can't be backed up", p.Rel)); continue; }
                                    var b = PersonalFiles.Copy(target, keep, ExistingFile.Overwrite, "", 0, null, log, System.Threading.CancellationToken.None, null, exact: true);
                                    // Never replace a folder we could not back up completely – Undo would lose files.
                                    if (b.Failed > 0) { res.Skipped++; log?.Invoke("   " + F("{0}: not replaced – {1} file(s) could not be backed up first", p.Rel, b.Failed)); continue; }
                                }
                                else { Directory.CreateDirectory(keep); File.Copy(target, Path.Combine(keep, Path.GetFileName(p.Rel)), true); }
                            }
                            if (p.Dir)
                                be.Files = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Select(f => f.Substring(source.TrimEnd('\\').Length + 1)).ToList();
                            bak.Entries.Add(be);
                            SaveBackup();
                            if (p.Dir) PersonalFiles.Copy(source, target, ExistingFile.Overwrite, "", 0, null, log, System.Threading.CancellationToken.None, null, exact: true);
                            else { Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Copy(source, target, true); }
                            res.Applied++;
                        }
                        catch (Exception ex) { res.Skipped++; log?.Invoke($"   {p.Rel}: {ex.Message}"); }
                    }

                    if (def.VsCodeExtensions && app.Extra.Count > 0)
                    {
                        var code = CodeCli();
                        if (code == null) log?.Invoke("   " + T("VS Code is not installed yet – extensions were not installed"));
                        else
                        {
                            int n = 0;
                            foreach (var ext in app.Extra.Where(x => SafeExtension.IsMatch(x)))
                                if (RunExit(code, "--install-extension " + ext + " --force") == 0) n++;
                            log?.Invoke("   " + F("{0} of {1} VS Code extensions installed", n, app.Extra.Count));
                            res.Applied += n;
                        }
                    }

                    if (def.FirefoxBookmarks)
                        foreach (var name in app.Extra)
                        {
                            if (name != Path.GetFileName(name) || !name.EndsWith(".jsonlz4", StringComparison.OrdinalIgnoreCase)) continue;
                            var src = Path.Combine(dir, app.Id, name);
                            if (!File.Exists(src)) continue;
                            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                            var target = Path.Combine(desktop, F("Firefox bookmarks from {0}", PersonalFiles.SafeName(pack.SourceComputer)) + ".jsonlz4");
                            File.Copy(src, target, true);
                            log?.Invoke("   " + F("Firefox bookmark backup placed on the Desktop: {0}", Path.GetFileName(target)));
                            log?.Invoke("   " + T(def.Note));
                            res.Applied++;
                        }
                    if (def.Sensitive && bak.Entries.Any(x => x.Existed))
                        log?.Invoke("   " + F("Your previous files were backed up to {0} (needed for Undo) – delete that folder when you no longer need it.", Path.Combine(backupDir, app.Id)));
                    SaveBackup();
                }
            }
            finally { try { SaveBackup(); } catch { } }
            return res;
        }

        public static string LatestBackup()
        {
            try
            {
                return Directory.GetDirectories(BackupRoot).OrderByDescending(d => d)
                    .Select(d => Path.Combine(d, FileName)).FirstOrDefault(File.Exists);
            }
            catch { return null; }
        }

        /// <summary>Puts back the files that were replaced by the last <see cref="Apply"/>.</summary>
        public static ApplyResult Undo(string backupFile, Action<string> log)
        {
            var res = new ApplyResult();
            var backupDir = Path.GetDirectoryName(backupFile);
            var pack = Load(backupDir);
            if (pack == null) return res;
            foreach (var app in pack.Apps)
            {
                var def = Find(app.Id);
                foreach (var e in app.Entries)
                {
                    if (e.Index < 0 || e.Index >= def.Paths.Count) continue;
                    var p = def.Paths[e.Index];
                    if (p.Token != e.Token || !string.Equals(p.Rel, e.Rel, StringComparison.OrdinalIgnoreCase) || p.Dir != e.Dir) continue;
                    var target = Path.Combine(Resolve(p.Token), p.Rel);
                    var keep = Path.Combine(backupDir, app.Id, e.Index.ToString());
                    try
                    {
                        if (p.Dir)
                        {
                            if (e.Existed) RestoreFolder(keep, target, log);
                            else RemoveWrittenFiles(target, e.Files, log);
                        }
                        else if (e.Existed) File.Copy(Path.Combine(keep, Path.GetFileName(p.Rel)), target, true);
                        else if (File.Exists(target)) File.Delete(target);
                        res.Applied++;
                    }
                    catch (Exception ex) { res.Skipped++; log?.Invoke($"   {p.Rel}: {ex.Message}"); }
                }
            }
            return res;
        }

        /// <summary>
        /// Puts a backed-up folder back without risking data: the current folder is first moved aside and only
        /// deleted once the backup was copied back completely (otherwise it is kept next to it).
        /// </summary>
        static void RestoreFolder(string keep, string target, Action<string> log)
        {
            if (!Directory.Exists(keep)) return;
            string aside = null;
            if (Directory.Exists(target))
            {
                aside = target.TrimEnd('\\') + ".orclwamp-undo-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                Directory.Move(target, aside);
            }
            var r = PersonalFiles.Copy(keep, target, ExistingFile.Overwrite, "", 0, null, log, System.Threading.CancellationToken.None, null, exact: true);
            if (aside == null) return;
            if (r.Failed == 0) { try { Directory.Delete(aside, true); } catch { } }
            else log?.Invoke("   " + F("Some files could not be restored – the folder as it was before Undo is kept in {0}", aside));
        }

        /// <summary>Folder that did not exist before Apply: delete only the files Apply wrote, then empty sub-folders.</summary>
        static void RemoveWrittenFiles(string target, List<string> files, Action<string> log)
        {
            if (files == null || !Directory.Exists(target)) return;
            var root = Path.GetFullPath(target).TrimEnd('\\') + "\\";
            foreach (var rel in files)
            {
                if (string.IsNullOrEmpty(rel) || Path.IsPathRooted(rel)) continue;
                var f = Path.GetFullPath(Path.Combine(target, rel));
                if (!f.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // a tampered backup must not delete elsewhere
                try { if (File.Exists(f)) File.Delete(f); } catch (Exception ex) { log?.Invoke("   " + rel + ": " + ex.Message); }
            }
            foreach (var d in Directory.GetDirectories(target, "*", SearchOption.AllDirectories).OrderByDescending(x => x.Length).Concat(new[] { target }))
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
        }

        static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    var o = p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } }
                    return o.Wait(5000) ? o.Result : "";
                }
            }
            catch { return ""; }
        }

        static int RunExit(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true }))
                {
                    p.StandardOutput.ReadToEndAsync();
                    p.StandardError.ReadToEndAsync();
                    if (!p.WaitForExit(180000)) { try { p.Kill(); } catch { } return -1; }
                    return p.ExitCode;
                }
            }
            catch { return -1; }
        }
    }
}
