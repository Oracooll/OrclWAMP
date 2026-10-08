using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;

namespace OrclWAMP
{
    /// <summary>Writes the migration folder that the user carries to the new PC.</summary>
    internal static class PackageWriter
    {
        public const string FolderName = "OrclWAMP-Migration";
        public const string ExeName = "OrclWAMP.exe";
        public const string ReportName = "Manual-Install-Report.html";

        static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        static readonly Encoding Utf8Bom = new UTF8Encoding(true);

        public static void Write(string dir, Manifest m, string exePath, ICollection<string> settingsIds = null, Action<string> log = null)
        {
            Directory.CreateDirectory(dir);

            // Windows settings (optional): always start from a clean folder so old captures don't linger.
            var settingsDir = Path.Combine(dir, WinSettings.FolderName);
            if (Directory.Exists(settingsDir)) Directory.Delete(settingsDir, true);
            if (settingsIds != null && settingsIds.Count > 0) WinSettings.Capture(settingsIds, settingsDir, log);

            var destExe = Path.Combine(dir, ExeName);
            if (!string.Equals(Path.GetFullPath(exePath), Path.GetFullPath(destExe), StringComparison.OrdinalIgnoreCase))
                File.Copy(exePath, destExe, true);

            m.Save(Path.Combine(dir, Program.ManifestFileName));
            File.WriteAllText(Path.Combine(dir, "Install.cmd"), InstallCmd(), Encoding.ASCII);
            File.WriteAllText(Path.Combine(dir, "winget-packages.json"), WingetImportJson(m), Utf8NoBom);
            File.WriteAllText(Path.Combine(dir, "Install-Fallback.ps1"), FallbackScript(m), Utf8Bom);
            File.WriteAllText(Path.Combine(dir, ReportName), Report(m), Utf8NoBom);
            File.WriteAllText(Path.Combine(dir, "README.txt"), Readme(m), Utf8Bom);
        }

        static string InstallCmd() =>
            "@echo off\r\n" +
            "rem OrclWAMP - starts the installer on the new PC.\r\n" +
            "cd /d \"%~dp0\"\r\n" +
            "start \"\" \"%~dp0" + ExeName + "\" /restore \"%~dp0" + Program.ManifestFileName + "\"\r\n";

        public static string WingetImportJson(Manifest m)
        {
            var sb = new StringBuilder();
            sb.Append("{\n  \"$schema\": \"https://aka.ms/winget-packages.schema.2.0.json\",\n");
            sb.Append("  \"CreationDate\": ").Append(Json.Str(DateTime.UtcNow.ToString("o"))).Append(",\n");
            sb.Append("  \"Sources\": [");
            bool firstSource = true;
            // Only the two built-in sources: winget import needs full source details, which custom sources would lack.
            foreach (var g in m.Packages.Where(p => IsBuiltInSource(p.Source)).GroupBy(p => p.Source, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append(firstSource ? "\n" : ",\n");
                firstSource = false;
                sb.Append("    {\n      \"Packages\": [");
                bool first = true;
                foreach (var p in g)
                {
                    sb.Append(first ? "\n" : ",\n");
                    first = false;
                    sb.Append("        { \"PackageIdentifier\": ").Append(Json.Str(p.Id));
                    if (m.PinVersions && Winget.IsPinnableVersion(p.Version)) sb.Append(", \"Version\": ").Append(Json.Str(p.Version));
                    sb.Append(" }");
                }
                sb.Append("\n      ],\n      \"SourceDetails\": ");
                if (g.Key.Equals("msstore", StringComparison.OrdinalIgnoreCase))
                    sb.Append("{ \"Argument\": \"https://storeedgefd.dsx.mp.microsoft.com/v9.0\", \"Identifier\": \"StoreEdgeFD\", \"Name\": \"msstore\", \"Type\": \"Microsoft.Rest\" }");
                else
                    sb.Append("{ \"Argument\": \"" + Winget.WingetSourceArgument + "\", \"Identifier\": \"Microsoft.Winget.Source_8wekyb3d8bbwe\", \"Name\": \"winget\", \"Type\": \"Microsoft.PreIndexed.Package\" }");
                sb.Append("\n    }");
            }
            sb.Append("\n  ]\n}\n");
            return sb.ToString();
        }

        static bool IsBuiltInSource(string s) =>
            s.Equals("winget", StringComparison.OrdinalIgnoreCase) || s.Equals("msstore", StringComparison.OrdinalIgnoreCase);

        static string Ps(string s) => "'" + (s ?? "").Replace("'", "''").Replace("‘", "‘‘").Replace("’", "’’").Replace("‚", "‚‚").Replace("‛", "‛‛") + "'";

        static string FallbackScript(Manifest m)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# OrclWAMP fallback installer - generated " + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + " on " + m.SourceComputer);
            sb.AppendLine("# Use this only if OrclWAMP.exe cannot run on the new PC.");
            sb.AppendLine("# Run:  powershell -ExecutionPolicy Bypass -File .\\Install-Fallback.ps1");
            sb.AppendLine("$ErrorActionPreference = 'Continue'");
            sb.AppendLine("if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {");
            sb.AppendLine("    Write-Host 'winget was not found. Install \"App Installer\" from the Microsoft Store or https://aka.ms/getwinget and run this script again.' -ForegroundColor Red");
            sb.AppendLine("    Read-Host 'Press Enter to exit'; exit 1");
            sb.AppendLine("}");
            sb.AppendLine("$pinVersions = $" + (m.PinVersions ? "true" : "false"));
            sb.AppendLine("$packages = @(");
            foreach (var p in m.Packages)
                sb.AppendLine("    @{ Id = " + Ps(p.Id) + "; Source = " + Ps(p.Source) + "; Version = " + Ps(Winget.IsPinnableVersion(p.Version) ? p.Version : "") + "; Name = " + Ps(p.Name) + " }");
            sb.AppendLine(")");
            sb.AppendLine("$okCodes = @(0, " + unchecked((int)0x8A15002Bu) + ", " + unchecked((int)0x8A150061u) + ", " + unchecked((int)0x8A15010Du) + ", " + unchecked((int)0x8A15010Eu) + ", " + unchecked((int)0x8A150109u) + ", " + unchecked((int)0x8A15010Bu) + ", 3010, 1641)");
            sb.AppendLine("$failed = @(); $i = 0");
            sb.AppendLine("foreach ($p in $packages) {");
            sb.AppendLine("    $i++");
            sb.AppendLine("    Write-Host \"[$i/$($packages.Count)] $($p.Name)  ($($p.Id))\" -ForegroundColor Cyan");
            sb.AppendLine("    $a = @('install', '--id', $p.Id, '--exact', '--source', $p.Source, '--silent', '--accept-package-agreements', '--accept-source-agreements')");
            sb.AppendLine("    if ($pinVersions -and $p.Version) { $a += @('--version', $p.Version) }");
            sb.AppendLine("    & winget @a");
            sb.AppendLine("    if ($okCodes -notcontains $LASTEXITCODE) { $failed += $p.Name; Write-Host \"   failed (exit code $LASTEXITCODE)\" -ForegroundColor Yellow }");
            sb.AppendLine("}");
            sb.AppendLine("Write-Host ''");
            sb.AppendLine("Write-Host \"Done. $($packages.Count - $failed.Count) of $($packages.Count) packages OK.\" -ForegroundColor Green");
            sb.AppendLine("if ($failed.Count) { Write-Host 'Failed:' -ForegroundColor Yellow; $failed | ForEach-Object { Write-Host \"  - $_\" } }");
            sb.AppendLine("Read-Host 'Press Enter to exit'");
            return sb.ToString();
        }

        public static string Report(Manifest m)
        {
            string H(string s) => WebUtility.HtmlEncode(s ?? "");
            var sb = new StringBuilder();
            sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>OrclWAMP – Manual install list</title><style>");
            sb.Append(":root{color-scheme:light dark}body{font-family:Segoe UI,Arial,sans-serif;margin:24px;max-width:1000px}");
            sb.Append("h1{font-size:22px}h2{font-size:17px;margin-top:28px}table{border-collapse:collapse;width:100%}");
            sb.Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #8884}th{background:#8882}");
            sb.Append("td.v{white-space:nowrap;color:#888}.muted{color:#888}input{transform:scale(1.2)}</style></head><body>");
            sb.Append("<h1>OrclWAMP – apps to install manually</h1>");
            sb.Append("<p class=\"muted\">Source PC: ").Append(H(m.SourceComputer)).Append(" · created ").Append(H(FormatDate(m.CreatedUtc))).Append("</p>");
            sb.Append("<p>These apps were installed on the old PC but winget has no package for them, so they have to be installed by hand. Tick them off as you go.</p>");
            if (m.ManualApps.Count == 0) sb.Append("<p><b>Nothing to do – every selected app can be installed by winget.</b></p>");
            else
            {
                sb.Append("<table><tr><th></th><th>App</th><th>Version on old PC</th><th>Download</th><th>Notes</th></tr>");
                foreach (var a in m.ManualApps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    // Only links OrclWAMP is sure about; otherwise no link is shown.
                    var kind = Downloads.Resolve(a, out var url, out var note);
                    sb.Append("<tr><td><input type=\"checkbox\"></td><td>").Append(H(a.Name));
                    if (a.Publisher.Length > 0) sb.Append("<br><span class=\"muted\">").Append(H(a.Publisher)).Append("</span>");
                    sb.Append("</td><td class=\"v\">").Append(H(a.Version)).Append("</td><td>");
                    if (kind == LinkKind.None) sb.Append("<span class=\"muted\">no known link</span>");
                    else sb.Append("<a href=\"").Append(H(url)).Append("\" target=\"_blank\" rel=\"noopener\">").Append(H(Downloads.KindText(kind))).Append("</a>");
                    sb.Append("</td><td class=\"muted\">").Append(H(note)).Append("</td></tr>");
                }
                sb.Append("</table>");
                sb.Append("<p class=\"muted\">Tip: OrclWAMP.exe (button \"Manual apps &amp; downloads\") can download these one by one or all at once.</p>");
            }
            sb.Append("<h2>Installed automatically by OrclWAMP (").Append(m.Packages.Count).Append(")</h2><table><tr><th>App</th><th>Package ID</th><th>Source</th></tr>");
            foreach (var p in m.Packages.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
                sb.Append("<tr><td>").Append(H(p.Name)).Append("</td><td class=\"v\">").Append(H(p.Id)).Append("</td><td class=\"v\">").Append(H(p.Source)).Append("</td></tr>");
            sb.Append("</table></body></html>");
            return sb.ToString();
        }

        static string Readme(Manifest m)
        {
            return
"OrclWAMP - Oracooll Winget App Migration Program\r\n" +
"=================================================\r\n\r\n" +
"This folder was created on " + m.SourceComputer + " (" + FormatDate(m.CreatedUtc) + ").\r\n" +
"It contains " + m.Packages.Count + " apps that winget can install and " + m.ManualApps.Count + " apps to install by hand.\r\n\r\n" +
"ON THE NEW PC\r\n" +
"  1. Connect it to the internet.\r\n" +
"  2. Copy this folder to the new PC (or run it straight from the USB stick).\r\n" +
"  3. Double-click Install.cmd (or OrclWAMP.exe).\r\n" +
"  4. Check the list and click \"Start installation\".\r\n" +
"  5. Click \"Manual apps & downloads\" (or open " + ReportName + ") for the apps that need a manual install.\r\n\r\n" +
"If Windows SmartScreen warns about the exe: click \"More info\" > \"Run anyway\".\r\n\r\n" +
"FILES\r\n" +
"  OrclWAMP.exe              the installer (the same portable tool that made this folder)\r\n" +
"  " + Program.ManifestFileName + "     the app list - can be opened and edited in OrclWAMP\r\n" +
"  Install.cmd               starts OrclWAMP in restore mode\r\n" +
"  winget-packages.json      standard winget file:\r\n" +
"                            winget import -i winget-packages.json --accept-package-agreements --accept-source-agreements --ignore-unavailable\r\n" +
"  Install-Fallback.ps1      plain PowerShell fallback:  powershell -ExecutionPolicy Bypass -File Install-Fallback.ps1\r\n" +
"  Settings\\                 Windows settings from the old PC (button \"Windows settings\" applies them, with Undo)\r\n" +
"  " + ReportName + "  apps winget can't install (download links only where certain)\r\n\r\n" +
"COMMAND LINE\r\n" +
"  OrclWAMP.exe /restore [file]   open restore mode\r\n" +
"  OrclWAMP.exe /unattended       install everything without asking (use with /restore)\r\n\r\n" +
"Logs from each run are saved in this folder (OrclWAMP-restore-*.log).\r\n";
        }

        public static string FormatDate(string isoUtc)
        {
            return DateTime.TryParse(isoUtc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var d)
                ? d.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : isoUtc;
        }

        public static void WriteCsv(string path, IEnumerable<AppEntry> entries)
        {
            string C(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";
            var sb = new StringBuilder("Name,Id,Version,Source,Category,Selected,Note\r\n");
            foreach (var e in entries)
                sb.Append(C(e.Name)).Append(',').Append(C(e.Id)).Append(',').Append(C(e.Version)).Append(',').Append(C(e.Source)).Append(',')
                  .Append(e.Category).Append(',').Append(e.Selected ? "yes" : "no").Append(',').Append(C(e.Note)).Append("\r\n");
            File.WriteAllText(path, sb.ToString(), Utf8Bom);
        }
    }
}
