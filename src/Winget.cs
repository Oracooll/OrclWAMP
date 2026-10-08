using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    internal sealed class ProcResult
    {
        public int ExitCode = -1;
        public string Output = "";
        public bool TimedOut;
        public bool Cancelled;
        public bool StartFailed;
        public string Error = "";
    }

    internal enum InstallOutcome { Installed, AlreadyInstalled, RebootRequired, NotFound, Failed, TimedOut, Cancelled }

    internal static class Winget
    {
        static string _exe;
        static Version _version;
        static bool _resolved;

        public const string WingetSourceArgument = "https://cdn.winget.microsoft.com/cache";

        /// <summary>Full path (or alias) of winget.exe, or null when winget is not installed.</summary>
        public static string Exe
        {
            get
            {
                if (!_resolved) { _exe = Resolve(); _resolved = true; } // "not found" is cached too, until Reset()
                return _exe;
            }
        }

        public static bool IsAvailable => Exe != null;
        public static Version Version => _version;

        public static void Reset() { _exe = null; _version = null; _resolved = false; }

        /// <summary>Older App Installer builds (pre-1.6) have known bugs and missing flags.</summary>
        public static bool IsOutdated => _version != null && _version < new Version(1, 6);

        /// <summary>--disable-interactivity exists since winget 1.4.</summary>
        public static string NoInteract => _version == null || _version >= new Version(1, 4) ? " --disable-interactivity" : "";

        static string Resolve()
        {
            var candidates = new List<string>();
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            {
                if (string.IsNullOrWhiteSpace(dir)) continue;
                try { candidates.Add(Path.Combine(dir.Trim().Trim('"'), "winget.exe")); } catch { }
            }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\winget.exe"));

            foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                bool exists;
                try { exists = File.Exists(c); } catch { exists = false; }
                if (!exists) continue;
                var v = TryGetVersion(c);
                if (v != null) { _version = v; return c; }
            }
            return null;
        }

        static Version TryGetVersion(string exe)
        {
            var r = RunSync(exe, "--version", null, CancellationToken.None, TimeSpan.FromSeconds(30));
            if (r.StartFailed || r.ExitCode != 0) return null;
            var text = (r.Output ?? "").Trim().TrimStart('v', 'V');
            var digits = new string(text.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
            return Version.TryParse(digits, out var v) ? v : new Version(0, 0);
        }

        public static Task<ProcResult> RunAsync(string args, Action<string> onLine, CancellationToken ct, TimeSpan? timeout)
        {
            var exe = Exe;
            if (exe == null) return Task.FromResult(new ProcResult { StartFailed = true, Error = T("winget was not found on this PC.") });
            return Task.Run(() => RunSync(exe, args, onLine, ct, timeout));
        }

        public static Task<ProcResult> RunProcessAsync(string exe, string args, Action<string> onLine, CancellationToken ct, TimeSpan? timeout)
            => Task.Run(() => RunSync(exe, args, onLine, ct, timeout));

        static ProcResult RunSync(string exe, string args, Action<string> onLine, CancellationToken ct, TimeSpan? timeout)
        {
            var result = new ProcResult();
            var sb = new StringBuilder();
            var gate = new object();
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            using (var outDone = new ManualResetEvent(false))
            using (var errDone = new ManualResetEvent(false))
            using (var p = new Process { StartInfo = psi })
            {
                void Handle(string data, ManualResetEvent done)
                {
                    if (data == null) { try { done.Set(); } catch { } return; }
                    lock (gate) sb.AppendLine(data);
                    try { onLine?.Invoke(data); } catch { }
                }
                p.OutputDataReceived += (s, e) => Handle(e.Data, outDone);
                p.ErrorDataReceived += (s, e) => Handle(e.Data, errDone);
                try
                {
                    p.Start();
                }
                catch (Win32Exception ex)
                {
                    result.StartFailed = true;
                    result.Error = ex.Message;
                    return result;
                }
                try { p.StandardInput.Close(); } catch { }
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();

                var sw = Stopwatch.StartNew();
                while (!p.WaitForExit(250))
                {
                    if (ct.IsCancellationRequested) { result.Cancelled = true; KillTree(p); break; }
                    if (timeout.HasValue && sw.Elapsed > timeout.Value) { result.TimedOut = true; KillTree(p); break; }
                }
                if (p.WaitForExit(10000))
                {
                    result.ExitCode = p.ExitCode;
                    // Let the async readers flush, but don't wait forever: a child process that inherited
                    // the pipes (e.g. an app the installer launched) would otherwise keep them open.
                    var flush = Stopwatch.StartNew();
                    outDone.WaitOne(5000);
                    errDone.WaitOne(Math.Max(0, 5000 - (int)flush.ElapsedMilliseconds));
                }
                lock (gate) result.Output = sb.ToString();
            }
            return result;
        }

        static void KillTree(Process p)
        {
            try
            {
                using (var tk = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }))
                    tk?.WaitForExit(10000);
            }
            catch { }
            try { if (!p.HasExited) p.Kill(); } catch { }
        }

        /// <summary>Quotes one argument using the Windows (CommandLineToArgvW) rules.</summary>
        public static string Quote(string s)
        {
            s = s ?? "";
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in s)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }

        public static string UpgradeArgs(string id, string source)
        {
            var sb = new StringBuilder("upgrade --id ").Append(Quote(id)).Append(" --exact");
            if (!string.IsNullOrWhiteSpace(source)) sb.Append(" --source ").Append(Quote(source));
            sb.Append(" --silent --accept-package-agreements --accept-source-agreements").Append(NoInteract);
            return sb.ToString();
        }

        public static string InstallArgs(string id, string source, string version)
        {
            var sb = new StringBuilder("install --id ").Append(Quote(id)).Append(" --exact");
            if (!string.IsNullOrWhiteSpace(source)) sb.Append(" --source ").Append(Quote(source));
            if (!string.IsNullOrWhiteSpace(version)) sb.Append(" --version ").Append(Quote(version));
            sb.Append(" --silent --accept-package-agreements --accept-source-agreements").Append(NoInteract);
            return sb.ToString();
        }

        public static bool IsPinnableVersion(string v)
        {
            if (string.IsNullOrWhiteSpace(v)) return false;
            v = v.Trim();
            return !v.Equals("Unknown", StringComparison.OrdinalIgnoreCase)
                   && !v.StartsWith("<") && !v.StartsWith(">") && v.IndexOf(' ') < 0 && v.IndexOf('…') < 0;
        }

        public static InstallOutcome Classify(ProcResult r)
        {
            if (r.Cancelled) return InstallOutcome.Cancelled;
            if (r.TimedOut) return InstallOutcome.TimedOut;
            if (r.StartFailed) return InstallOutcome.Failed;
            switch (unchecked((uint)r.ExitCode))
            {
                case 0: return InstallOutcome.Installed;
                case 0x8A15002B: // no applicable upgrade (already installed, up to date)
                case 0x8A150061: // package already installed
                case 0x8A15010D: // installer: already installed
                case 0x8A15010E: // installer: newer version already installed
                    return InstallOutcome.AlreadyInstalled;
                case 0x8A150109: // reboot required to finish
                case 0x8A15010B: // reboot initiated
                case 3010:
                case 1641:
                    return InstallOutcome.RebootRequired;
                case 0x8A150014: // no package found
                    return InstallOutcome.NotFound;
                default: return InstallOutcome.Failed; // includes 0x8A15010A: "restart your PC, then try again"
            }
        }

        /// <summary>Errors that usually go away on their own (Windows Update / another installer busy).</summary>
        public static bool IsTransient(ProcResult r)
        {
            if (r.Cancelled || r.TimedOut || r.StartFailed) return false;
            switch (unchecked((uint)r.ExitCode))
            {
                case 0x8A150101: case 0x8A150102: case 0x8A150103: case 0x8A150111:
                case 0x8A150008: case 0x8A150107: case 1618:
                    return true;
                default: return false;
            }
        }

        /// <summary>The installer refuses to run elevated / is a per-user package.</summary>
        public static bool NeedsNonAdmin(ProcResult r)
        {
            uint c = unchecked((uint)r.ExitCode);
            return c == 0x8A150056 || c == 0x8A15007D;
        }

        public static string Describe(ProcResult r)
        {
            if (r.Cancelled) return T("Cancelled");
            if (r.TimedOut) return T("Timed out");
            if (r.StartFailed) return F("Could not start winget: {0}", r.Error);
            uint c = unchecked((uint)r.ExitCode);
            switch (c)
            {
                case 0: return T("Installed");
                case 0x8A15002B: case 0x8A150061: case 0x8A15010D: return T("Already installed");
                case 0x8A15010E: return T("A newer version is already installed");
                case 0x8A150109: case 3010: return T("Installed – restart required");
                case 0x8A15010B: case 1641: return T("Installed – restart started by installer");
                case 0x8A15010A: return T("Failed – restart the PC, then click \"Retry failed\"");
                case 0x8A150014: return T("Package not found in source");
                case 0x8A150016: return T("More than one package matched");
                case 0x8A150010: return T("No installer for this system (architecture / OS)");
                case 0x8A150008: return T("Failed – download failed");
                case 0x8A150011: return T("Failed – installer hash mismatch (package being updated, try later)");
                case 0x8A15001B: case 0x8A15001C: return T("Microsoft Store is blocked by policy");
                case 0x8A15001E: return T("Failed – Microsoft Store install failed");
                case 0x8A150045: return T("Failed – could not open the package source");
                case 0x8A150046: return T("Failed – source agreements not accepted");
                case 0x8A150056: return T("Can't install as administrator – run OrclWAMP without admin rights and retry");
                case 0x8A15007D: return T("Per-user app – run OrclWAMP without admin rights and retry");
                case 0x8A150110: return T("Failed – a dependency could not be installed");
                case 0x8A150111: return T("Failed – app is in use by another application");
                case 1618: return T("Failed – another installation is in progress");
                case 0x8A150101: return T("Failed – application is in use");
                case 0x8A150102: return T("Failed – another installation is in progress");
                case 0x8A150103: return T("Failed – a file is in use");
                case 0x8A150104: return T("Failed – missing dependency");
                case 0x8A150105: return T("Failed – disk full");
                case 0x8A150106: return T("Failed – not enough memory");
                case 0x8A150107: return T("Failed – no network connection");
                case 0x8A15010C: return T("Cancelled by user (installer)");
                case 0x8A15010F: return T("Blocked by policy");
                default: return F("Failed (exit code 0x{0})", c.ToString("X8"));
            }
        }

        /// <summary>True for spinner / progress-bar lines winget writes when output is redirected.</summary>
        public static bool IsNoise(string line, out string progress)
        {
            progress = null;
            if (line == null) return true;
            var t = line.Trim();
            if (t.Length == 0) return true;
            if (t.Length <= 2 && t.Trim('-', '\\', '|', '/', ' ').Length == 0) return true;
            if (t.IndexOf('█') >= 0 || t.IndexOf('▒') >= 0)
            {
                progress = t.Trim('█', '▒', ' ');
                return true;
            }
            return false;
        }
    }

    /// <summary>Parses the fixed-width tables printed by "winget list" and "winget search".</summary>
    internal static class TableParser
    {
        internal sealed class Row
        {
            public string Name = "", Id = "", Version = "", Source = "", Available = "";
        }

        public static List<Row> Parse(string output)
        {
            var rows = new List<Row>();
            if (string.IsNullOrEmpty(output)) return rows;
            var lines = output.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            int dash = -1;
            for (int i = 1; i < lines.Length; i++)
            {
                var t = lines[i].Trim();
                if (t.Length >= 10 && t.Trim('-').Length == 0) { dash = i; break; }
            }
            if (dash < 1) return rows;

            var header = lines[dash - 1];
            var starts = TokenStarts(header);
            if (starts.Count < 3) return rows;
            bool hasSource = starts.Count >= 4;

            for (int i = dash + 1; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Trim().Length == 0) break;
                // A real row has whitespace right before every column start (no text running across boundaries).
                if (!BoundariesClean(line, starts)) continue;

                var cols = new List<string>();
                for (int c = 0; c < starts.Count; c++)
                    cols.Add(Slice(line, starts[c], c + 1 < starts.Count ? starts[c + 1] : -1));

                var row = new Row { Name = cols[0], Id = cols[1], Version = cols[2] };
                if (starts.Count >= 5) row.Available = cols[3]; // winget list / upgrade: Name Id Version Available Source
                // Source names never contain spaces or colons; the "Match" column of a single-source
                // search ("Tag: firefox") does, so it is not mistaken for a source.
                if (hasSource)
                {
                    var last = cols[cols.Count - 1];
                    if (last.IndexOf(' ') < 0 && last.IndexOf(':') < 0) row.Source = last;
                }
                if (row.Id.Length == 0) continue;
                rows.Add(row);
            }
            return rows;
        }

        static List<int> TokenStarts(string header)
        {
            var starts = new List<int>();
            int cell = 0;
            bool prevSpace = true;
            for (int i = 0; i < header.Length; i++)
            {
                int w = Width(header, i, out int len);
                bool space = header[i] == ' ';
                if (!space && prevSpace) starts.Add(cell);
                prevSpace = space;
                cell += w;
                i += len - 1;
            }
            return starts;
        }

        static bool BoundariesClean(string line, List<int> starts)
        {
            var spaceCells = new HashSet<int>();
            int cell = 0;
            for (int i = 0; i < line.Length; i++)
            {
                int w = Width(line, i, out int len);
                if (line[i] == ' ') spaceCells.Add(cell);
                cell += w;
                i += len - 1;
            }
            int total = cell;
            for (int c = 1; c < starts.Count; c++)
            {
                int b = starts[c] - 1;
                if (b >= total) break; // trailing empty columns
                if (!spaceCells.Contains(b)) return false;
            }
            return true;
        }

        static string Slice(string line, int startCell, int endCell)
        {
            var sb = new StringBuilder();
            int cell = 0;
            for (int i = 0; i < line.Length; i++)
            {
                int w = Width(line, i, out int len);
                if (cell >= startCell && (endCell < 0 || cell < endCell)) sb.Append(line, i, len);
                cell += w;
                i += len - 1;
            }
            return sb.ToString().Trim();
        }

        /// <summary>Console display width of the character at i (CJK / emoji are double width).</summary>
        static int Width(string s, int i, out int len)
        {
            char c = s[i];
            if (char.IsHighSurrogate(c) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) { len = 2; return 2; }
            len = 1;
            if ((c >= 0x1100 && c <= 0x115F) || (c >= 0x2E80 && c <= 0xA4CF && c != 0x303F) ||
                (c >= 0xAC00 && c <= 0xD7A3) || (c >= 0xF900 && c <= 0xFAFF) || (c >= 0xFE30 && c <= 0xFE4F) ||
                (c >= 0xFF00 && c <= 0xFF60) || (c >= 0xFFE0 && c <= 0xFFE6))
                return 2;
            return 1;
        }
    }
}
