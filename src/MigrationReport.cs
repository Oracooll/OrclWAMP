using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>What happened during the restore on the new PC – turned into one HTML page.</summary>
    internal sealed class ReportData
    {
        public string SourceComputer = "", PackageCreated = "";
        public List<(string Name, string Id, string Status, int State)> Apps = new List<(string, string, string, int)>(); // State: 1 ok, 0 not run, -1 failed
        public List<ManualApp> ManualApps = new List<ManualApp>();
        public List<string> SettingsGroups = new List<string>();
        public ApplyResult Settings, AppConfigs;
        public List<string> AppConfigNames = new List<string>();
        public CopyResult Files;
    }

    internal static class MigrationReport
    {
        public const string FileName = "OrclWAMP-Migration-Report.html";

        /// <summary>Writes the report next to the package (or to %TEMP% if the stick is read-only) and returns its path.</summary>
        public static string Write(ReportData d, string packageDir)
        {
            var html = Build(d);
            foreach (var dir in new[] { packageDir, Path.GetTempPath() })
            {
                try
                {
                    var path = Path.Combine(dir, FileName);
                    File.WriteAllText(path, html, new UTF8Encoding(false));
                    return path;
                }
                catch { }
            }
            return null;
        }

        public static string Build(ReportData d)
        {
            string H(string s) => WebUtility.HtmlEncode(s ?? "");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"").Append(Lang.Code).Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>").Append(H(T("OrclWAMP – Migration report"))).Append("</title><style>");
            sb.Append(":root{color-scheme:light dark}body{font-family:Segoe UI,Arial,sans-serif;margin:24px auto;max-width:1000px;padding:0 16px}");
            sb.Append("h1{font-size:22px}h2{font-size:17px;margin-top:30px;border-bottom:1px solid #8885;padding-bottom:4px}");
            sb.Append("table{border-collapse:collapse;width:100%}th,td{text-align:left;padding:5px 10px;border-bottom:1px solid #8883;vertical-align:top}");
            sb.Append("th{background:#8882}.ok{color:#1a8a3a}.bad{color:#c62828}.muted{color:#888}.cards{display:flex;flex-wrap:wrap;gap:12px}");
            sb.Append(".card{border:1px solid #8885;border-radius:8px;padding:10px 14px;min-width:150px}.card b{font-size:22px;display:block}</style></head><body>");
            sb.Append("<h1>").Append(H(T("OrclWAMP – Migration report"))).Append("</h1>");
            sb.Append("<p class=\"muted\">").Append(H(F("From \"{0}\" (package created {1}) to \"{2}\" – {3}", d.SourceComputer, d.PackageCreated, Environment.MachineName, DateTime.Now.ToString("yyyy-MM-dd HH:mm")))).Append("</p>");

            int ok = d.Apps.Count(a => a.State == 1), failed = d.Apps.Count(a => a.State == -1), open = d.Apps.Count(a => a.State == 0);
            sb.Append("<div class=\"cards\">");
            Card(sb, ok.ToString(), T("apps installed or already there"));
            Card(sb, failed.ToString(), T("apps failed"));
            Card(sb, d.ManualApps.Count.ToString(), T("apps to install manually"));
            Card(sb, d.Settings != null ? d.Settings.Applied.ToString() : "–", T("Windows settings applied"));
            Card(sb, d.AppConfigs != null ? d.AppConfigs.Applied.ToString() : "–", T("app settings applied"));
            Card(sb, d.Files != null ? PersonalFiles.Size(d.Files.Bytes) : "–", T("personal files copied"));
            sb.Append("</div>");

            sb.Append("<h2>").Append(H(T("Apps"))).Append("</h2>");
            if (d.Apps.Count == 0) sb.Append("<p class=\"muted\">").Append(H(T("No apps in this package."))).Append("</p>");
            else
            {
                if (open > 0) sb.Append("<p class=\"muted\">").Append(H(F("{0} app(s) were not installed in this session (unticked or not started).", open))).Append("</p>");
                sb.Append("<table><tr><th>").Append(H(T("App"))).Append("</th><th>").Append(H(T("Package ID"))).Append("</th><th>").Append(H(T("Result"))).Append("</th></tr>");
                foreach (var a in d.Apps.OrderBy(x => x.State).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                    sb.Append("<tr><td>").Append(H(a.Name)).Append("</td><td class=\"muted\">").Append(H(a.Id)).Append("</td><td class=\"")
                      .Append(a.State == 1 ? "ok" : a.State == -1 ? "bad" : "muted").Append("\">").Append(H(a.Status)).Append("</td></tr>");
                sb.Append("</table>");
            }

            sb.Append("<h2>").Append(H(T("Apps to install manually"))).Append("</h2>");
            if (d.ManualApps.Count == 0) sb.Append("<p class=\"ok\">").Append(H(T("Nothing to do."))).Append("</p>");
            else
            {
                sb.Append("<table><tr><th>").Append(H(T("App"))).Append("</th><th>").Append(H(T("Download"))).Append("</th><th>").Append(H(T("Note"))).Append("</th></tr>");
                foreach (var m in d.ManualApps.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var kind = Downloads.Resolve(m, out var url, out var note); // only links OrclWAMP is sure about
                    sb.Append("<tr><td>").Append(H(m.Name)).Append("</td><td>");
                    if (kind == LinkKind.None) sb.Append("<span class=\"muted\">").Append(H(T("no known link"))).Append("</span>");
                    else sb.Append("<a href=\"").Append(H(url)).Append("\" target=\"_blank\" rel=\"noopener\">").Append(H(Downloads.KindText(kind))).Append("</a>");
                    sb.Append("</td><td class=\"muted\">").Append(H(Lang.Note(note))).Append("</td></tr>");
                }
                sb.Append("</table>");
            }

            Section(sb, T("Windows settings"), d.Settings, d.SettingsGroups.Select(id => T(WinSettings.Find(id)?.Name ?? id)));
            Section(sb, T("App settings & bookmarks"), d.AppConfigs, d.AppConfigNames);

            sb.Append("<h2>").Append(H(T("Personal files"))).Append("</h2>");
            if (d.Files == null) sb.Append("<p class=\"muted\">").Append(H(T("Not copied in this session."))).Append("</p>");
            else sb.Append("<p>").Append(H(F("{0} files copied ({1}), {2} identical files skipped, {3} kept as a renamed copy, {4} failed.",
                                              d.Files.Copied, PersonalFiles.Size(d.Files.Bytes), d.Files.Skipped, d.Files.Renamed, d.Files.Failed))).Append("</p>");

            sb.Append("<p class=\"muted\" style=\"margin-top:30px\">").Append(H("OrclWAMP " + Program.VersionText + " – " + Program.RepoUrl)).Append("</p></body></html>");
            return sb.ToString();
        }

        static void Card(StringBuilder sb, string big, string text) =>
            sb.Append("<div class=\"card\"><b>").Append(WebUtility.HtmlEncode(big)).Append("</b>").Append(WebUtility.HtmlEncode(text)).Append("</div>");

        static void Section(StringBuilder sb, string title, ApplyResult r, IEnumerable<string> names)
        {
            string H(string s) => WebUtility.HtmlEncode(s ?? "");
            sb.Append("<h2>").Append(H(title)).Append("</h2>");
            if (r == null) { sb.Append("<p class=\"muted\">").Append(H(T("Not applied in this session."))).Append("</p>"); return; }
            sb.Append("<p>").Append(H(F("{0} applied, {1} skipped.", r.Applied, r.Skipped))).Append("</p>");
            var list = names.ToList();
            if (list.Count > 0) sb.Append("<p class=\"muted\">").Append(H(string.Join(", ", list))).Append("</p>");
        }
    }
}
