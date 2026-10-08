using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using static OrclWAMP.Lang;

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

        /// <summary>What else goes into the package besides the app list.</summary>
        internal sealed class Options
        {
            public ICollection<string> SettingsIds;
            public ICollection<string> AppConfigIds;
            public IList<FolderData> Folders;
            public string Password;                       // protects Wi-Fi passwords / SSH keys
            public Action<string> Log;
            public Action<long, long, string> FileProgress;
            public System.Threading.CancellationToken Cancel;
        }

        public static void Write(string dir, Manifest m, string exePath, Options opt = null)
        {
            opt = opt ?? new Options();
            var log = opt.Log;
            Directory.CreateDirectory(dir);

            // A folder to copy must not lie inside this package folder (it would be deleted below before being copied).
            var pkgFull = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            var nested = opt.Folders?.FirstOrDefault(f => (Path.GetFullPath(f.SourcePath).TrimEnd('\\') + "\\").StartsWith(pkgFull, StringComparison.OrdinalIgnoreCase));
            if (nested != null) throw new IOException(Lang.F("\"{0}\" is inside the package folder. Choose another folder to copy, or save the package somewhere else.", nested.DisplayName));

            // Until the very end there is no package file, so a cancelled/failed rewrite never looks like a valid package.
            var manifestFile = Path.Combine(dir, Program.ManifestFileName);
            if (File.Exists(manifestFile)) File.Delete(manifestFile);

            // Optional parts: always start from clean folders so old captures don't linger.
            foreach (var sub in new[] { WinSettings.FolderName, AppConfigs.FolderName, PersonalFiles.FolderName })
            {
                var p = Path.Combine(dir, sub);
                if (Directory.Exists(p)) Directory.Delete(p, true);
            }
            var secretsFile = Path.Combine(dir, Secrets.FileName);
            if (File.Exists(secretsFile)) File.Delete(secretsFile);
            bool protect = !string.IsNullOrEmpty(opt.Password);

            // Sensitive items (Wi-Fi passwords, SSH keys) only ever travel encrypted: without a password they are left out,
            // with a password they are captured into a private local temp folder and encrypted straight into the container,
            // so they are never written readable onto the USB stick.
            var settingsIds = (opt.SettingsIds ?? new string[0]).ToList();
            var appIds = (opt.AppConfigIds ?? new string[0]).Where(id => AppConfigs.Find(id) != null).ToList();
            var sensitiveSettings = settingsIds.Where(id => WinSettings.Find(id)?.Sensitive == true).ToList();
            var sensitiveApps = appIds.Where(id => AppConfigs.Find(id).Sensitive).ToList();
            settingsIds = settingsIds.Except(sensitiveSettings).ToList();
            appIds = appIds.Except(sensitiveApps).ToList();
            if (!protect && (sensitiveSettings.Count > 0 || sensitiveApps.Count > 0))
                log?.Invoke(Lang.T("Wi-Fi passwords and SSH keys were left out – they are only included in a password-protected package."));

            var settingsDir = Path.Combine(dir, WinSettings.FolderName);
            var appDir = Path.Combine(dir, AppConfigs.FolderName);
            if (settingsIds.Count > 0) WinSettings.Capture(settingsIds, settingsDir, log);
            opt.Cancel.ThrowIfCancellationRequested();
            if (appIds.Count > 0) AppConfigs.Capture(appIds, appDir, log);
            opt.Cancel.ThrowIfCancellationRequested();

            if (protect && (sensitiveSettings.Count > 0 || sensitiveApps.Count > 0))
            {
                var tmp = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrclWAMP", "tmp", Guid.NewGuid().ToString("N"));
                try
                {
                    if (sensitiveSettings.Count > 0)
                    {
                        var sp = WinSettings.Capture(sensitiveSettings, Path.Combine(tmp, WinSettings.FolderName), log);
                        WinSettings.Merge(settingsDir, sp);
                    }
                    if (sensitiveApps.Count > 0)
                    {
                        var ap = AppConfigs.Capture(sensitiveApps, Path.Combine(tmp, AppConfigs.FolderName), log);
                        AppConfigs.Merge(appDir, ap);
                    }
                    // Container entry names are package-relative ("Settings/wifi/x.xml", "AppConfigs/ssh/0/id_ed25519").
                    var items = Directory.Exists(tmp)
                        ? Directory.GetFiles(tmp, "*", SearchOption.AllDirectories)
                            // the two index files were merged into the package already – everything else is secret
                            .Where(f => !string.Equals(f, Path.Combine(tmp, WinSettings.FolderName, WinSettings.FileName), StringComparison.OrdinalIgnoreCase) &&
                                        !string.Equals(f, Path.Combine(tmp, AppConfigs.FolderName, AppConfigs.FileName), StringComparison.OrdinalIgnoreCase))
                            .Select(f => (f.Substring(tmp.Length + 1).Replace('\\', '/'), File.ReadAllBytes(f)))
                            .ToList()
                        : new List<(string, byte[])>();
                    if (items.Count > 0) Secrets.Write(secretsFile, opt.Password, items);
                    log?.Invoke(Lang.F("{0} sensitive file(s) encrypted with the package password.", items.Count));
                }
                finally
                {
                    try { if (Directory.Exists(tmp)) Directory.Delete(tmp, true); } catch { }
                }
            }

            if (opt.Folders != null && opt.Folders.Count > 0)
            {
                var filesDir = Path.Combine(dir, PersonalFiles.FolderName);
                Directory.CreateDirectory(filesDir);
                PersonalFiles.Capture(opt.Folders, filesDir, opt.FileProgress, log, opt.Cancel);
            }

            opt.Cancel.ThrowIfCancellationRequested();
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
            sb.Append("<!doctype html><html lang=\"").Append(IsBg ? "bg" : "en").Append("\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">");
            sb.Append("<title>OrclWAMP – ").Append(H(T("Manual install list"))).Append("</title><style>");
            sb.Append(":root{color-scheme:light dark}body{font-family:Segoe UI,Arial,sans-serif;margin:24px;max-width:1000px}");
            sb.Append("h1{font-size:22px}h2{font-size:17px;margin-top:28px}table{border-collapse:collapse;width:100%}");
            sb.Append("th,td{text-align:left;padding:6px 10px;border-bottom:1px solid #8884}th{background:#8882}");
            sb.Append("td.v{white-space:nowrap;color:#888}.muted{color:#888}input{transform:scale(1.2)}</style></head><body>");
            sb.Append("<h1>OrclWAMP – ").Append(H(T("apps to install manually"))).Append("</h1>");
            sb.Append("<p class=\"muted\">").Append(H(F("Source PC: {0} · created {1}", m.SourceComputer, FormatDate(m.CreatedUtc)))).Append("</p>");
            sb.Append("<p>").Append(H(T("These apps were installed on the old PC but winget has no package for them, so they have to be installed by hand. Tick them off as you go."))).Append("</p>");
            if (m.ManualApps.Count == 0) sb.Append("<p><b>").Append(H(T("Nothing to do – every selected app can be installed by winget."))).Append("</b></p>");
            else
            {
                sb.Append("<table><tr><th></th><th>").Append(H(T("App"))).Append("</th><th>").Append(H(T("Version on old PC"))).Append("</th><th>").Append(H(T("Download"))).Append("</th><th>").Append(H(T("Notes"))).Append("</th></tr>");
                foreach (var a in m.ManualApps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    // Only links OrclWAMP is sure about; otherwise no link is shown.
                    var kind = Downloads.Resolve(a, out var url, out var note);
                    sb.Append("<tr><td><input type=\"checkbox\"></td><td>").Append(H(a.Name));
                    if (a.Publisher.Length > 0) sb.Append("<br><span class=\"muted\">").Append(H(a.Publisher)).Append("</span>");
                    sb.Append("</td><td class=\"v\">").Append(H(a.Version)).Append("</td><td>");
                    if (kind == LinkKind.None) sb.Append("<span class=\"muted\">").Append(H(T("no known link"))).Append("</span>");
                    else sb.Append("<a href=\"").Append(H(url)).Append("\" target=\"_blank\" rel=\"noopener\">").Append(H(Downloads.KindText(kind))).Append("</a>");
                    sb.Append("</td><td class=\"muted\">").Append(H(Lang.Note(note))).Append("</td></tr>");
                }
                sb.Append("</table>");
                sb.Append("<p class=\"muted\">").Append(H(T("Tip: OrclWAMP.exe (button \"Manual apps & downloads\") can download these one by one or all at once."))).Append("</p>");
            }
            sb.Append("<h2>").Append(H(F("Installed automatically by OrclWAMP ({0})", m.Packages.Count))).Append("</h2><table><tr><th>").Append(H(T("App"))).Append("</th><th>").Append(H(T("Package ID"))).Append("</th><th>").Append(H(T("Source"))).Append("</th></tr>");
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
F("This folder was created on {0} ({1}).", m.SourceComputer, FormatDate(m.CreatedUtc)) + "\r\n" +
F("It contains {0} apps that winget can install and {1} apps to install by hand.", m.Packages.Count, m.ManualApps.Count) + "\r\n\r\n" +
T("ON THE NEW PC") + "\r\n" +
"  " + T("1. Connect it to the internet.") + "\r\n" +
"  " + T("2. Copy this folder to the new PC (or run it straight from the USB stick).") + "\r\n" +
"  " + T("3. Double-click Install.cmd (or OrclWAMP.exe).") + "\r\n" +
"  " + T("4. Check the list and click \"Start installation\".") + "\r\n" +
"  " + F("5. Click \"Manual apps & downloads\" (or open {0}) for the apps that need a manual install.", ReportName) + "\r\n\r\n" +
T("If Windows SmartScreen warns about the exe: click \"More info\" > \"Run anyway\".") + "\r\n\r\n" +
T("FILES") + "\r\n" +
"  OrclWAMP.exe              " + T("the installer (the same portable tool that made this folder)") + "\r\n" +
"  " + Program.ManifestFileName + "     " + T("the app list - can be opened and edited in OrclWAMP") + "\r\n" +
"  Install.cmd               " + T("starts OrclWAMP in restore mode") + "\r\n" +
"  winget-packages.json      " + T("standard winget file:") + "\r\n" +
"                            winget import -i winget-packages.json --accept-package-agreements --accept-source-agreements --ignore-unavailable\r\n" +
"  Install-Fallback.ps1      " + T("plain PowerShell fallback:") + "  powershell -ExecutionPolicy Bypass -File Install-Fallback.ps1\r\n" +
"  Settings\\                 " + T("Windows settings from the old PC (button \"Windows settings\" applies them, with Undo)") + "\r\n" +
"  " + ReportName + "  " + T("apps winget can't install (download links only where certain)") + "\r\n\r\n" +
T("COMMAND LINE") + "\r\n" +
"  OrclWAMP.exe /restore [file]   " + T("open restore mode") + "\r\n" +
"  OrclWAMP.exe /unattended       " + T("install everything without asking (use with /restore)") + "\r\n\r\n" +
T("Logs from each run are saved in this folder (OrclWAMP-restore-*.log).") + "\r\n";
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
