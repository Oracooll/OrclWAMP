using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>
    /// App updates shared by the App updates window and the unattended run (/upgrade /unattended,
    /// used by the weekly schedule). An app is updated automatically exactly when the window would tick it.
    /// </summary>
    internal static class AppUpdater
    {
        /// <summary>Held while updates run, so the window and a scheduled run never update at the same time.</summary>
        public const string MutexName = @"Local\OrclWAMP-AppUpdates";
        public static readonly TimeSpan PerAppTimeout = TimeSpan.FromMinutes(60);
        /// <summary>Unattended: don't start another app after this long, so Task Scheduler's 24-hour limit never cuts one off.</summary>
        static readonly TimeSpan RunBudget = TimeSpan.FromHours(21) - PerAppTimeout - PerAppTimeout; // counted from process start

        /// <summary>
        /// Takes the update lock. A mutex made by an elevated process can't even be opened from a normal one
        /// (and the other way round) – that also means another OrclWAMP holds it.
        /// </summary>
        public static Mutex TryLock()
        {
            Mutex m = null;
            try
            {
                m = new Mutex(false, MutexName);
                bool owned;
                try { owned = m.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
                if (owned) return m;
            }
            catch (UnauthorizedAccessException) { }
            catch (WaitHandleCannotBeOpenedException) { }
            m?.Dispose();
            return null;
        }

        public static void Unlock(Mutex m)
        {
            if (m == null) return;
            try { m.ReleaseMutex(); } catch { }
            m.Dispose();
        }
        const int KeepLogs = 20;

        public static string LogDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrclWAMP", "logs");

        // ---- which apps are updated automatically ----
        /// <summary>The "leave out" list and the runtimes option, read from settings.ini in one go.</summary>
        internal sealed class AutoRules
        {
            public HashSet<string> Excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public bool Runtimes;

            /// <summary>True if the app is ticked by default and updated by the unattended run.</summary>
            public bool IsAuto(string id) => !Excluded.Contains(id) && (Runtimes || !Scanner.IsRuntime(id));
        }

        /// <summary>The current rules, or null if settings.ini exists but can't be read right now.</summary>
        public static AutoRules LoadRules()
        {
            var snap = AppSettings.Snapshot();
            if (snap == null) return null;
            snap.TryGetValue("UpdateExclude", out var ex);
            snap.TryGetValue("UpdateRuntimes", out var rt);
            var r = new AutoRules { Runtimes = rt == "1" };
            foreach (var id in (ex ?? "").Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0))
                r.Excluded.Add(id);
            return r;
        }

        /// <summary>Adds an app to or removes it from the "leave out" list. False if it could not be saved.</summary>
        public static bool SetExcluded(IEnumerable<string> ids, bool excluded)
        {
            var r = LoadRules();
            if (r == null) return false; // don't lose the list because the file was busy
            foreach (var id in ids) { if (excluded) r.Excluded.Add(id); else r.Excluded.Remove(id); }
            return AppSettings.Set("UpdateExclude", string.Join("|", r.Excluded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)));
        }

        public static bool IncludeRuntimes => AppSettings.Get("UpdateRuntimes") == "1";

        // ---- winget ----
        /// <summary>Apps with a newer version. Throws if winget could not produce the list.</summary>
        public static async Task<List<TableParser.Row>> ListAsync(CancellationToken ct, List<TableParser.Row> truncated = null)
        {
            var r = await Winget.RunAsync("upgrade --accept-source-agreements" + Winget.NoInteract, null, ct, TimeSpan.FromMinutes(5));
            ct.ThrowIfCancellationRequested();
            if (r.StartFailed) throw new InvalidOperationException(F("Could not start winget: {0}", r.Error));
            if (r.TimedOut) throw new TimeoutException(T("winget did not answer in time."));
            var rows = TableParser.Parse(r.Output);
            // "No installed package found" is a normal answer; any other error with no table is a real failure.
            if (rows.Count == 0 && r.ExitCode != 0 && unchecked((uint)r.ExitCode) != 0x8A150014 && unchecked((uint)r.ExitCode) != 0x8A15002B)
                throw new InvalidOperationException(F("winget could not check for updates (exit code 0x{0}).", unchecked((uint)r.ExitCode).ToString("X8")));
            // Truncated IDs ("Microsoft.VisualStudio…") can't be upgraded with --exact; rows without a source aren't winget packages.
            truncated?.AddRange(rows.Where(x => x.Source.Length > 0 && x.Id.EndsWith("…")));
            return rows.Where(x => x.Source.Length > 0 && !x.Id.EndsWith("…")).ToList();
        }

        public static bool IsGood(InstallOutcome o) => o == InstallOutcome.Installed || o == InstallOutcome.AlreadyInstalled || o == InstallOutcome.RebootRequired;

        /// <summary>Updates one app; waits and tries once more if another installer was busy.</summary>
        public static async Task<ProcResult> UpgradeAsync(TableParser.Row row, Action<string> log, CancellationToken ct)
        {
            ProcResult r = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                r = await Winget.RunAsync(Winget.UpgradeArgs(row.Id, row.Source), line =>
                {
                    if (!Winget.IsNoise(line, out _)) log?.Invoke("   " + line.Trim());
                }, ct, PerAppTimeout);
                if (attempt == 0 && Winget.IsTransient(r) && !ct.IsCancellationRequested)
                {
                    log?.Invoke("   " + T("Another installation is busy – trying again in a minute…"));
                    try { await Task.Delay(TimeSpan.FromMinutes(1), ct); } catch (OperationCanceledException) { break; }
                    continue;
                }
                break;
            }
            return r;
        }

        // ---- last automatic run (shown in the schedule dialog) ----
        public static string LastRunText()
        {
            var when = AppSettings.Get("LastUpdateRun");
            if (string.IsNullOrEmpty(when) || !DateTime.TryParse(when, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var t)) return T("never");
            var parts = (AppSettings.Get("LastUpdateResult") ?? "").Split('|');
            if (parts.Length == 1 && parts[0].Length > 0) return t.ToString("g") + " – " + T(parts[0]);
            if (parts.Length < 3) return t.ToString("g");
            return t.ToString("g") + " – " + F("{0} updated, {1} failed, {2} up to date or skipped", parts[0], parts[1], parts[2]);
        }

        static void SaveLastRun(string result) => AppSettings.Set(new Dictionary<string, string>
        {
            { "LastUpdateRun", DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture) },
            { "LastUpdateResult", result }
        });

        /// <summary>True when Task Scheduler (svchost / taskeng) started this process.</summary>
        static bool ParentIsTaskScheduler()
        {
            var parent = ParentProcessName();
            return parent != null && (parent.Equals("svchost", StringComparison.OrdinalIgnoreCase) || parent.Equals("taskeng", StringComparison.OrdinalIgnoreCase) || parent.Equals("taskhostw", StringComparison.OrdinalIgnoreCase));
        }

        [System.Runtime.InteropServices.DllImport("ntdll.dll")]
        static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ProcessBasicInformation
        {
            public IntPtr Reserved1, PebBaseAddress, Reserved2a, Reserved2b, UniqueProcessId, InheritedFromUniqueProcessId;
        }

        static string ParentProcessName()
        {
            try
            {
                var pbi = new ProcessBasicInformation();
                using (var me = System.Diagnostics.Process.GetCurrentProcess())
                    if (NtQueryInformationProcess(me.Handle, 0, ref pbi, System.Runtime.InteropServices.Marshal.SizeOf(pbi), out _) != 0) return null;
                using (var parent = System.Diagnostics.Process.GetProcessById(pbi.InheritedFromUniqueProcessId.ToInt32()))
                    return parent.ProcessName;
            }
            catch { return null; }
        }

        // ---- unattended run ----
        /// <summary>/upgrade /unattended: updates every app the window would tick, with a tray icon and a log. Returns the exit code.</summary>
        public static int RunUnattended()
        {
            // The App updates window may be busy right now: wait for it (up to 2 hours) instead of losing this week's run.
            Mutex mutex = TryLock();
            bool waited = mutex == null;
            for (int i = 0; mutex == null && i < 240; i++) { Thread.Sleep(30000); mutex = TryLock(); }
            if (mutex == null)
            {
                SaveLastRun("Skipped – app updates were already running.");
                return 4;
            }
            // Started by the schedule but it was turned off while we waited: don't run.
            if (waited && ParentIsTaskScheduler())
            {
                try { var q = UpdateSchedule.Query(); if (q == null || !q.Enabled) { Unlock(mutex); return 0; } } catch { }
            }
            try
            {
                var ctx = new UnattendedRun();
                Application.Run(ctx);
                return ctx.ExitCode;
            }
            finally { Unlock(mutex); }
        }

        /// <summary>Runs the updates on the UI thread (winget itself runs in the background) with a tray icon.</summary>
        sealed class UnattendedRun : ApplicationContext
        {
            public int ExitCode;
            readonly CancellationTokenSource _cts = new CancellationTokenSource();
            readonly NotifyIcon _icon = new NotifyIcon();
            readonly StreamWriter _log;
            readonly string _logPath;
            readonly ToolStripMenuItem _stopItem;
            readonly DateTime _started = StartTimeUtc();

            static DateTime StartTimeUtc()
            {
                try { using (var me = System.Diagnostics.Process.GetCurrentProcess()) return me.StartTime.ToUniversalTime(); }
                catch { return DateTime.UtcNow; }
            }

            public UnattendedRun()
            {
                try
                {
                    Directory.CreateDirectory(LogDir);
                    _logPath = Path.Combine(LogDir, "updates-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                    _log = new StreamWriter(_logPath, false, new UTF8Encoding(true)) { AutoFlush = true };
                    foreach (var old in new DirectoryInfo(LogDir).GetFiles("updates-*.log").OrderByDescending(f => f.LastWriteTimeUtc).Skip(KeepLogs))
                        try { old.Delete(); } catch { }
                }
                catch { _log = null; }

                _icon.Icon = Ui.AppIcon ?? SystemIcons.Application;
                _icon.Text = Program.AppName + " – " + T("App updates");
                var menu = new ContextMenuStrip();
                menu.Items.Add(T("Open log"), null, (s, e) => OpenLog());
                _stopItem = new ToolStripMenuItem(T("Stop after the current app"), null, (s, e) => { _cts.Cancel(); _stopItem.Enabled = false; });
                menu.Items.Add(_stopItem);
                _icon.ContextMenuStrip = menu;
                _icon.BalloonTipClicked += (s, e) => OpenLog();
                _icon.DoubleClick += (s, e) => OpenLog();

                // Start once the message loop runs.
                var start = new System.Windows.Forms.Timer { Interval = 50 };
                start.Tick += async (s, e) => { start.Stop(); start.Dispose(); await RunAsync(); };
                start.Start();
            }

            // winget output arrives on its reader threads.
            void Log(string line) { lock (_icon) { try { _log?.WriteLine(line.Length == 0 ? "" : DateTime.Now.ToString("HH:mm:ss") + "  " + line); } catch { } } }

            void OpenLog() { if (_logPath != null && File.Exists(_logPath)) Ui.Open(_logPath); }

            void SetTip(string text)
            {
                var t = Program.AppName + ": " + text;
                _icon.Text = t.Length > 63 ? t.Substring(0, 62) + "…" : t; // NotifyIcon.Text is limited to 63 characters
            }

            async Task RunAsync()
            {
                Log($"OrclWAMP {Program.VersionText} – unattended app updates ({(Ui.IsAdmin() ? "administrator" : "standard rights")})");
                Ui.KeepAwake(true);
                int ok = 0, failed = 0, skipped = 0;
                try
                {
                    if (!Winget.IsAvailable) { Log("winget was not found on this PC."); SaveLastRun("winget was not found on this PC."); ExitCode = 3; return; }
                    // A scheduled run can start right after boot or wake-up: give the network up to 10 minutes.
                    for (int i = 0; i < 120 && !NetworkInterface.GetIsNetworkAvailable(); i++)
                    {
                        if (i == 0) Log("Waiting for a network connection…");
                        await Task.Delay(5000);
                    }

                    // Right after waking up the network may look connected before it really works: try a few times.
                    List<TableParser.Row> rows = null;
                    var truncated = new List<TableParser.Row>();
                    for (int attempt = 1; rows == null; attempt++)
                    {
                        try { truncated.Clear(); rows = await ListAsync(_cts.Token, truncated); }
                        catch (Exception ex) when (!(ex is OperationCanceledException))
                        {
                            Log(ex.Message);
                            if (attempt >= 5 || _cts.IsCancellationRequested)
                            {
                                SaveLastRun("Could not check for updates.");
                                ExitCode = 1;
                                return;
                            }
                            Log("Trying again in 2 minutes…");
                            await Task.Delay(TimeSpan.FromMinutes(2));
                        }
                    }

                    foreach (var r in truncated) { skipped++; Log($"Skipped: {r.Name} ({r.Id}) – winget shortened the package ID, update it from the App updates window or with winget"); }
                    var rules = LoadRules();
                    if (rules == null)
                    {
                        Log("Your OrclWAMP settings could not be read, so the list of apps to leave out is unknown – nothing was updated.");
                        SaveLastRun("Your OrclWAMP settings could not be read – nothing was updated.");
                        ExitCode = 1;
                        return;
                    }
                    var queue = rows.Where(r => rules.IsAuto(r.Id)).ToList();
                    Log($"{rows.Count} update(s) available, {queue.Count} to install automatically.");
                    foreach (var r in rows.Where(r => !rules.IsAuto(r.Id)))
                    {
                        skipped++;
                        Log($"Skipped: {r.Name} ({r.Id}) – {(rules.Excluded.Contains(r.Id) ? "excluded from automatic updates" : "runtime")}");
                    }
                    if (queue.Count == 0) { SaveLastRun($"0|0|{skipped}"); return; }

                    _icon.Visible = true;
                    _icon.ShowBalloonTip(5000, Program.AppName, F("Updating {0} app(s) in the background…", queue.Count), ToolTipIcon.Info);
                    int n = 0;
                    foreach (var row in queue)
                    {
                        n++;
                        if (_cts.IsCancellationRequested) { skipped++; Log($"Skipped (stopped): {row.Name}"); continue; }
                        // The user may have left an app out in the App updates window while this run was going.
                        var now = LoadRules();
                        if (now == null) { skipped++; Log($"Skipped: {row.Name} ({row.Id}) – your OrclWAMP settings could not be read"); continue; }
                        if (!now.IsAuto(row.Id)) { skipped++; Log($"Skipped: {row.Name} ({row.Id}) – left out of automatic updates in the meantime"); continue; }
                        SetTip(F("updating {0} of {1}", n, queue.Count) + " – " + row.Name);
                        Log("");
                        Log($"{row.Name} ({row.Id}) {row.Version} -> {row.Available}");
                        if (DateTime.UtcNow - _started > RunBudget) { skipped++; Log($"Skipped (out of time for this run): {row.Name}"); continue; }
                        // The "stop" menu item lets the current app finish – killing an installer half way can break the app.
                        var r = await UpgradeAsync(row, Log, CancellationToken.None);
                        var outcome = Winget.Classify(r);
                        Log("   => " + (outcome == InstallOutcome.AlreadyInstalled ? "Up to date" : Winget.Describe(r)) + $" (exit code 0x{unchecked((uint)r.ExitCode):X8})");
                        if (outcome == InstallOutcome.AlreadyInstalled) skipped++;
                        else if (IsGood(outcome)) ok++;
                        else failed++;
                    }
                    var summary = $"{ok} updated, {failed} failed, {skipped} up to date or skipped.";
                    Log("");
                    Log("=== " + summary + " ===");
                    SaveLastRun($"{ok}|{failed}|{skipped}");
                    ExitCode = failed > 0 ? 1 : 0;
                    SetTip(T("App updates finished"));
                    _icon.ShowBalloonTip(15000, Program.AppName,
                        F("{0} updated, {1} failed, {2} up to date or skipped", ok, failed, skipped) + (failed > 0 ? "\r\n" + T("Click to open the log.") : ""),
                        failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
                    _stopItem.Enabled = false;
                    await Task.Delay(20000); // leave the balloon (and the "Open log" menu) up for a moment
                }
                catch (Exception ex)
                {
                    Log("Unexpected error: " + ex);
                    ExitCode = 1;
                }
                finally
                {
                    Ui.KeepAwake(false);
                    _icon.Visible = false;
                    _icon.Dispose();
                    try { _log?.Dispose(); } catch { }
                    ExitThread();
                }
            }

        }
    }
}
