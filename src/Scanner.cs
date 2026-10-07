using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OrclWAMP
{
    internal static class Scanner
    {
        // Runtimes / frameworks: installers pull these in as dependencies, so they are unticked by default.
        static readonly string[] RuntimePrefixes =
        {
            "Microsoft.VCRedist.", "Microsoft.DotNet.", "Microsoft.WindowsAppRuntime", "Microsoft.VCLibs",
            "Microsoft.UI.Xaml", "Microsoft.DirectX", "Microsoft.VSTOR", "Microsoft.WindowsSDK", "Microsoft.NetFramework"
        };

        // Shipped with Windows already – reinstalling usually fails or is pointless.
        static readonly string[] PreinstalledIds =
        {
            "Microsoft.AppInstaller", "Microsoft.Edge", "Microsoft.EdgeWebView2Runtime", "Microsoft.OneDrive",
            "Microsoft.Edge.Update", "Microsoft.UpdateHealthTools", "Microsoft.WindowsTerminal.Preinstalled"
        };

        static readonly string[] SystemMsixPrefixes =
        {
            "Microsoft.", "MicrosoftWindows.", "MicrosoftCorporationII.", "Windows.", "NcsiUwpApp", "DolbyLaboratories.",
            "RealtekSemiconductorCorp.", "AppUp.", "NVIDIACorp.", "AdvancedMicroDevicesInc", "Clipchamp.", "MicrosoftTeams",
            "SynapticsIncorporated.", "WavesAudio.", "ELANMicroelectronicsCorpo", "E046963F.", "AD2F1837.", "DellInc."
        };

        static readonly string[] SystemArpNamePrefixes =
        {
            "Microsoft Visual C++", "Microsoft .NET", "Microsoft ASP.NET", "Microsoft Windows Desktop Runtime",
            "Windows Software Development Kit", "Windows SDK", "Microsoft Update Health Tools", "Microsoft Edge Update",
            "Update for ", "Security Update for", "Microsoft Windows SDK", "Universal CRT", "vs_", "Microsoft GameInput",
            "Microsoft Edge WebView2", "Windows Driver Package", "Microsoft Windows Desktop Targeting Pack",
            "Microsoft Windows Application Compatibility", "Microsoft Visual Studio Installer"
        };

        static readonly Regex LocaleSuffix = new Regex(@"\s+-\s+[a-z]{2,3}-[a-z]{2,4}$", RegexOptions.IgnoreCase);
        // Installed website (PWA) packages look like MSIX\host.name-1A2B3C4D_1.0.0.0_neutral__xxxx
        static readonly Regex WebApp = new Regex(@"^MSIX\\([^_\\]+)-[0-9A-F]{8}_", RegexOptions.IgnoreCase);

        public static async Task<List<AppEntry>> ScanAsync(Action<string> status, CancellationToken ct)
        {
            status?.Invoke("Reading installed apps (winget list)…");
            var list = await Winget.RunAsync("list --accept-source-agreements" + Winget.NoInteract, null, ct, TimeSpan.FromMinutes(10));
            ct.ThrowIfCancellationRequested();
            if (list.StartFailed) throw new InvalidOperationException(list.Error);
            var rows = TableParser.Parse(list.Output);
            if (rows.Count == 0)
                throw new InvalidOperationException("winget did not return a list of installed apps.\r\n\r\n" + Tail(list.Output, 1500));

            status?.Invoke("Verifying package IDs (winget export)…");
            var exported = await ExportAsync(ct);
            ct.ThrowIfCancellationRequested();

            status?.Invoke("Classifying apps…");
            var info = await Task.Run(() => ReadUninstallInfo());
            return Build(rows, exported, info);
        }

        /// <summary>Id -> source name from "winget export" (gives exact, untruncated IDs).</summary>
        static async Task<Dictionary<string, PackageRef>> ExportAsync(CancellationToken ct)
        {
            var map = new Dictionary<string, PackageRef>(StringComparer.OrdinalIgnoreCase);
            var tmp = Path.Combine(Path.GetTempPath(), "orclwamp-export-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                await Winget.RunAsync("export -o " + Winget.Quote(tmp) + " --include-versions --accept-source-agreements" + Winget.NoInteract, null, ct, TimeSpan.FromMinutes(10));
                if (!File.Exists(tmp)) return map;
                var m = Manifest.Load(tmp);
                foreach (var p in m.Packages) map[p.Id] = p;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* export is only used for verification */ }
            finally { try { File.Delete(tmp); } catch { } }
            return map;
        }

        internal sealed class UninstallInfo { public string Publisher = "", Homepage = ""; }

        /// <summary>Normalised display name -> publisher / homepage from the Windows "Apps & features" registry entries.</summary>
        static Dictionary<string, UninstallInfo> ReadUninstallInfo()
        {
            var map = new Dictionary<string, UninstallInfo>();
            const string path = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
            foreach (var hive in new[] { Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryHive.CurrentUser })
            foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
            {
                try
                {
                    using (var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, view))
                    using (var key = root.OpenSubKey(path))
                    {
                        if (key == null) continue;
                        foreach (var sub in key.GetSubKeyNames())
                        {
                            try
                            {
                                using (var k = key.OpenSubKey(sub))
                                {
                                    var name = k?.GetValue("DisplayName") as string;
                                    if (string.IsNullOrWhiteSpace(name)) continue;
                                    var norm = Norm(name);
                                    if (map.ContainsKey(norm)) continue;
                                    var url = new[] { "URLUpdateInfo", "URLInfoAbout", "HelpLink" }
                                        .Select(v => (k.GetValue(v) as string ?? "").Trim())
                                        .FirstOrDefault(Downloads.IsWebUrl) ?? "";
                                    map[norm] = new UninstallInfo { Publisher = (k.GetValue("Publisher") as string ?? "").Trim(), Homepage = url };
                                }
                            }
                            catch { }
                        }
                    }
                }
                catch { }
            }
            return map;
        }

        /// <summary>Lower-case letters and digits only ("xplorer² Pro 64-bit" == "xplorer2 pro 64bit").</summary>
        public static string Norm(string s) =>
            new string((s ?? "").Replace("²", "2").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        internal static List<AppEntry> Build(List<TableParser.Row> rows, Dictionary<string, PackageRef> exported, Dictionary<string, UninstallInfo> info = null)
        {
            var result = new List<AppEntry>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenManual = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var r in rows)
            {
                string id = r.Id, source = r.Source;

                if (id.EndsWith("…") && exported.Count > 0)
                {
                    var prefix = id.TrimEnd('…');
                    var match = exported.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (match.Count == 1) { id = match[0]; if (source.Length == 0) source = exported[id].Source; }
                }
                if (source.Length == 0 && exported.TryGetValue(id, out var exPkg)) source = exPkg.Source;

                var e = new AppEntry { Name = r.Name.Length > 0 ? r.Name : id, Id = id, Version = r.Version, Source = source };

                if (source.Length > 0 && id.EndsWith("…"))
                {
                    // Several exported IDs share this prefix: the export loop below adds them with exact IDs.
                    var prefix = id.TrimEnd('…');
                    if (exported.Keys.Any(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
                    // winget cut the ID off and export could not resolve it – we cannot install it reliably.
                    e.Category = AppCategory.NotAvailable;
                    e.Selected = true;
                    e.Note = "Package ID could not be read – install manually";
                }
                else if (source.Length > 0)
                {
                    if (!seenIds.Add(id)) continue;
                    e.Category = source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? AppCategory.Store : AppCategory.Winget;
                    if (IsRuntime(id)) { e.Selected = false; e.Note = "Runtime – installed automatically when needed"; }
                    else if (IsPreinstalled(id)) { e.Selected = false; e.Note = "Included with Windows"; }
                    else e.Selected = true;
                }
                else
                {
                    // Office & co. register one entry per language ("Microsoft 365 - de-de") – show them once.
                    var lang = LocaleSuffix.Match(e.Name);
                    if (lang.Success) e.Name = e.Name.Substring(0, lang.Index) + " (all languages)";
                    if (!seenManual.Add(e.Name + "|" + (lang.Success ? "" : e.Version))) continue;

                    e.Category = IsSystem(id, e.Name) ? AppCategory.System : AppCategory.NotAvailable;
                    e.Selected = e.Category == AppCategory.NotAvailable;
                    if (e.Category == AppCategory.System) e.Note = "Windows / driver component";
                    else if (WebApp.IsMatch(id)) e.Note = "Web app – reinstall it from the browser";
                    if (e.Category == AppCategory.NotAvailable)
                    {
                        var webAddress = WebAppAddress(id);
                        if (webAddress != null) e.Homepage = webAddress;
                        else if (!id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase) && info != null && info.TryGetValue(Norm(r.Name), out var ui))
                        { e.Publisher = ui.Publisher; e.Homepage = ui.Homepage; }
                    }
                    else if (e.Name.IndexOf("Driver", StringComparison.OrdinalIgnoreCase) >= 0) e.Note = "Driver – get it from the hardware maker";
                    else e.Note = "Not in winget – goes to the manual-install list";
                }
                result.Add(e);
            }

            // Anything export found that list did not show (rare).
            foreach (var kv in exported)
            {
                if (seenIds.Contains(kv.Key)) continue;
                seenIds.Add(kv.Key);
                // Borrow the display name from a cut-off list row ("Mozilla.Firefox.Dev…") if there is one.
                var row = rows.FirstOrDefault(x => x.Id.EndsWith("…") && kv.Key.StartsWith(x.Id.TrimEnd('…'), StringComparison.OrdinalIgnoreCase));
                result.Add(new AppEntry
                {
                    Name = row != null && row.Name.Length > 0 ? row.Name : kv.Key, Id = kv.Key, Version = kv.Value.Version ?? "", Source = kv.Value.Source,
                    Selected = !IsRuntime(kv.Key) && !IsPreinstalled(kv.Key),
                    Category = kv.Value.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? AppCategory.Store : AppCategory.Winget
                });
            }
            return result;
        }

        /// <summary>
        /// The https address of an installed web app (PWA), derived from its package name
        /// (MSIX\host.name-1A2B3C4D_...). Null for anything else, and for IP addresses / local names
        /// where the scheme (http or https) isn't known.
        /// </summary>
        public static string WebAppAddress(string id)
        {
            var web = WebApp.Match(id ?? "");
            if (!web.Success) return null;
            var host = web.Groups[1].Value;
            return Regex.IsMatch(host, @"^(?!\d+(\.\d+){3}$)[a-z0-9-]+(\.[a-z0-9-]+)+$", RegexOptions.IgnoreCase) ? "https://" + host : null;
        }

        public static bool IsRuntime(string id) => RuntimePrefixes.Any(p => id.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        public static bool IsPreinstalled(string id) => PreinstalledIds.Any(p => id.Equals(p, StringComparison.OrdinalIgnoreCase));

        static bool IsSystem(string id, string name)
        {
            if (id.StartsWith(@"MSIX\", StringComparison.OrdinalIgnoreCase))
            {
                var pkg = id.Substring(5);
                if (SystemMsixPrefixes.Any(p => pkg.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
            }
            if (name.IndexOf("Experience Pack", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (name.EndsWith(" Extension", StringComparison.OrdinalIgnoreCase) || name.EndsWith(" Extensions", StringComparison.OrdinalIgnoreCase)) return true;
            if (name.StartsWith("KB", StringComparison.Ordinal) && name.Length > 2 && char.IsDigit(name[2])) return true;
            return SystemArpNamePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
        }

        static string Tail(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : "…" + s.Substring(s.Length - max);
        }

        internal sealed class WingetMatch
        {
            public AppEntry Entry;
            public TableParser.Row Row;
            public bool Unique; // the only package whose name equals the app's name
        }

        /// <summary>
        /// Looks up apps winget didn't link to a package (e.g. Store or renamed installs) by exact name.
        /// Only identical names are returned; the user confirms every match.
        /// </summary>
        public static async Task<List<WingetMatch>> FindWingetMatchesAsync(IList<AppEntry> apps, Action<int, int, string> progress, CancellationToken ct)
        {
            var result = new List<WingetMatch>();
            for (int i = 0; i < apps.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var e = apps[i];
                progress?.Invoke(i + 1, apps.Count, e.Name);
                var q = Downloads.CleanName(e.Name);
                if (q.Length < 2) continue;
                var r = await Winget.RunAsync("search --name " + Winget.Quote(q) + " --accept-source-agreements" + Winget.NoInteract, null, ct, TimeSpan.FromMinutes(2));
                var nq = Norm(q);
                var exact = TableParser.Parse(r.Output)
                    .Where(x => !x.Id.EndsWith("…") && Norm(x.Name) == nq)
                    .GroupBy(x => x.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
                    .ToList();
                foreach (var x in exact) result.Add(new WingetMatch { Entry = e, Row = x, Unique = exact.Count == 1 });
            }
            ct.ThrowIfCancellationRequested();
            return result;
        }

        /// <summary>Set of installed package IDs (used by restore mode to skip what is already there).</summary>
        public static async Task<HashSet<string>> InstalledIdsAsync(CancellationToken ct)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var list = await Winget.RunAsync("list --accept-source-agreements" + Winget.NoInteract, null, ct, TimeSpan.FromMinutes(4));
            foreach (var r in TableParser.Parse(list.Output))
                if (r.Source.Length > 0 && !r.Id.EndsWith("…")) set.Add(r.Id);
            ct.ThrowIfCancellationRequested();
            foreach (var id in (await ExportAsync(ct)).Keys) set.Add(id); // exact IDs, covers truncated list rows
            return set;
        }
    }
}
