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
        static readonly Regex WebApp = new Regex(@"^MSIX\\[^_\\]+-[0-9A-F]{8}_", RegexOptions.IgnoreCase);

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
            return Build(rows, exported);
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

        internal static List<AppEntry> Build(List<TableParser.Row> rows, Dictionary<string, PackageRef> exported)
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
