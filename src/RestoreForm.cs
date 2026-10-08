using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>New PC: install the apps from a migration package.</summary>
    internal sealed class RestoreForm : Form
    {
        sealed class Item
        {
            public PackageRef Pkg;
            public ListViewItem Lvi;
            public InstallOutcome? Outcome;
            public bool NeedsNonAdmin;
            public bool Installed; // already present on this PC before we started
            public Func<Color> Color = () => Theme.Text;
        }

        /// <summary>Exit code the elevated copy returns to ask the original (unelevated) window to carry on.</summary>
        public const int ExitContinueUnelevated = 42;

        readonly Manifest _m;
        readonly string _manifestPath;
        readonly bool _unattended, _elevatedLaunch;
        readonly string _launcherSid;
        readonly List<Item> _items = new List<Item>();
        readonly ListView _lv = Ui.ListView();
        readonly SplitContainer _split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        readonly TextBox _log = new TextBox();
        readonly Label _wingetLabel = Ui.Label(T("Checking winget…"));
        readonly CheckBox _pin, _skipInstalled, _restart;
        readonly NumericUpDown _timeout = new NumericUpDown();
        readonly ProgressBar _bar = new ProgressBar();
        readonly Label _status = Ui.Label("");
        readonly Button _startBtn, _cancelBtn, _retryBtn, _fixWingetBtn, _adminBtn;
        readonly object _logGate = new object();
        CancellationTokenSource _cts;
        bool _running, _busy, _wingetOk, _programmaticCheck, _closeWhenStopped, _elevating, _starting;
        bool _phaseActive, _stopRequested; // unattended: a Stop / close in one step also cancels the following steps
        string _logFile;

        public int ExitCode { get; private set; }

        // Colour roles (resolved against the current theme, so rows re-colour when the theme changes).
        static readonly Func<Color> Green = () => Theme.Ok, Red = () => Theme.Fail, Orange = () => Theme.Warn,
            Gray = () => Theme.Muted, Blue = () => Theme.Busy, Normal = () => Theme.Text;
        Func<Color> _wingetColor = () => Theme.Text;

        public RestoreForm(Manifest m, string manifestPath, bool unattended, bool elevatedLaunch, string launcherSid)
        {
            _m = m;
            _manifestPath = manifestPath;
            _unattended = unattended;
            _elevatedLaunch = elevatedLaunch;
            _launcherSid = launcherSid;
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – " + T("Install apps on this PC"), 1100, 760);
            SuspendLayout();

            // ---- list + log ----
            _lv.Columns.Add(T("Name"));
            _lv.Columns.Add(T("Package ID"));
            _lv.Columns.Add(T("Version"));
            _lv.Columns.Add(T("Source"));
            _lv.Columns.Add(T("Status"));
            Ui.AutoSizeColumns(_lv, 300, 290, 110, 70, 320);
            _lv.ItemCheck += (s, e) => { if ((_running || _busy) && !_programmaticCheck) e.NewValue = e.CurrentValue; };
            var cm = new ContextMenuStrip();
            cm.Items.Add(T("Tick all"), null, (s, e) => SetAll(_ => true));
            cm.Items.Add(T("Untick all"), null, (s, e) => SetAll(_ => false));
            cm.Items.Add(T("Tick only failed"), null, (s, e) => SetAll(i => IsFailure(i.Outcome)));
            _lv.ContextMenuStrip = cm;
            _split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            _split.Panel2.Controls.Add(_log);
            Controls.Add(_split);

            // ---- bottom ----
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(Ui.S(6)) };
            var opts = Ui.Flow(DockStyle.Fill);
            _pin = Ui.Check(T("Install the exact same versions"), m.PinVersions);
            _skipInstalled = Ui.Check(T("Skip apps that are already installed"), true);
            _skipInstalled.CheckedChanged += (s, e) => { foreach (var i in _items.Where(x => x.Installed && x.Outcome == null)) SetChecked(i, !_skipInstalled.Checked); };
            _restart = Ui.Check(T("Restart the PC when finished"), m.RestartWhenDone);
            opts.Controls.Add(_pin);
            opts.Controls.Add(_skipInstalled);
            opts.Controls.Add(_restart);
            opts.Controls.Add(Ui.Label(T("Timeout per app (min):")));
            _timeout.Minimum = 5; _timeout.Maximum = 240; _timeout.Width = Ui.S(60); _timeout.Anchor = AnchorStyles.Left;
            _timeout.Value = Math.Max(5, Math.Min(240, m.TimeoutMinutes));
            opts.Controls.Add(_timeout);
            bottom.Controls.Add(opts);

            _bar.Dock = DockStyle.Fill; _bar.Height = Ui.S(18); _bar.Margin = new Padding(Ui.S(6), Ui.S(2), Ui.S(6), Ui.S(2));
            bottom.Controls.Add(_bar);

            var buttons = Ui.Flow(DockStyle.Fill);
            _startBtn = Ui.Button(T("Start installation"), async (s, e) => await InstallAsync(false), true);
            _startBtn.Padding = new Padding(Ui.S(14), Ui.S(6), Ui.S(14), Ui.S(6));
            _startBtn.Enabled = false;
            _cancelBtn = Ui.Button(T("Stop"), (s, e) => { if (_running || _phaseActive) _stopRequested = true; _cts?.Cancel(); Log(_running ? T("Stopping… (the current installer is being terminated)") : _phaseActive ? T("Stopping…") : T("Skipping the check…")); });
            _cancelBtn.Enabled = false;
            _retryBtn = Ui.Button(T("Retry failed"), async (s, e) => await InstallAsync(true));
            _retryBtn.Enabled = false;
            buttons.Controls.Add(_startBtn);
            buttons.Controls.Add(_cancelBtn);
            buttons.Controls.Add(_retryBtn);
            buttons.Controls.Add(Ui.Button(F("Manual apps && downloads ({0})", m.ManualApps.Count), (s, e) => OpenManualApps()));
            buttons.Controls.Add(Ui.Button(T("Open log"), (s, e) => { if (_logFile != null && File.Exists(_logFile)) Ui.Open(_logFile); else Ui.Info(this, T("No log has been written yet.")); }));
            _settingsDir = Path.Combine(Path.GetDirectoryName(manifestPath), WinSettings.FolderName);
            try { _settingsPack = WinSettings.Load(_settingsDir); } catch { _settingsPack = null; }
            if (_settingsPack != null && _settingsPack.Groups.Count > 0)
                buttons.Controls.Add(Ui.Button(F("Windows settings ({0})", _settingsPack.Groups.Count), (s, e) => OpenSettings()));
            _packageDir = Path.GetDirectoryName(manifestPath);
            _secrets = new PackageSecrets(_packageDir);
            _appDir = Path.Combine(_packageDir, AppConfigs.FolderName);
            _appPack = AppConfigs.Load(_appDir);
            if (_appPack != null && _appPack.Apps.Count > 0)
                buttons.Controls.Add(Ui.Button(F("App settings & bookmarks ({0})", _appPack.Apps.Count).Replace("&", "&&"), (s, e) => OpenAppConfigs()));
            _filesDir = Path.Combine(_packageDir, PersonalFiles.FolderName);
            _filesPack = PersonalFiles.Load(_filesDir);
            if (_filesPack != null && _filesPack.Folders.Count > 0)
                buttons.Controls.Add(Ui.Button(F("Personal files ({0})", PersonalFiles.Size(_filesPack.Folders.Sum(f => f.Bytes))), (s, e) => OpenFiles()));
            buttons.Controls.Add(Ui.Button(T("Migration report"), (s, e) => OpenReport()));
            _report = new ReportData { SourceComputer = m.SourceComputer, PackageCreated = PackageWriter.FormatDate(m.CreatedUtc), ManualApps = m.ManualApps };
            buttons.Controls.Add(Ui.LogPaneButton(_split));
            buttons.Controls.Add(_status);
            bottom.Controls.Add(buttons);
            Controls.Add(bottom);

            // ---- header ----
            var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), Ui.S(2)) };
            var headerBar = Ui.HeaderBar(T("Install apps on this PC"));
            headerBar.Dock = DockStyle.Fill;
            head.Controls.Add(headerBar);
            var info = new Label { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(Ui.S(3), Ui.S(4), Ui.S(3), Ui.S(2)) };
            info.Text = F("{0} apps from \"{1}\"  ·  package created {2}", m.Packages.Count, m.SourceComputer, PackageWriter.FormatDate(m.CreatedUtc));
            head.Controls.Add(info);
            var wingetRow = Ui.Flow(DockStyle.Fill);
            wingetRow.Padding = new Padding(0);
            wingetRow.Controls.Add(_wingetLabel);
            _fixWingetBtn = Ui.Button(T("Install / repair winget"), async (s, e) => await RepairWingetAsync());
            _fixWingetBtn.Visible = false;
            wingetRow.Controls.Add(_fixWingetBtn);
            _adminBtn = Ui.Button(T("Restart as administrator"), async (s, e) => await ElevateAsync(true));
            _adminBtn.Visible = !Ui.IsAdmin();
            wingetRow.Controls.Add(_adminBtn);
            head.Controls.Add(wingetRow);
            Controls.Add(head);

            ResumeLayout(true);

            foreach (var p in m.Packages)
            {
                var it = new ListViewItem(p.Name) { Checked = true };
                it.SubItems.Add(p.Id);
                it.SubItems.Add(p.Version);
                it.SubItems.Add(p.Source);
                it.SubItems.Add(T("Waiting"));
                var item = new Item { Pkg = p, Lvi = it };
                it.Tag = item;
                _items.Add(item);
                _lv.Items.Add(it);
            }

            Load += (s, e) =>
            {
                try { _split.SplitterDistance = Math.Max(_split.Panel1MinSize, Math.Min(_split.Height - _split.Panel2MinSize - 1, _split.Height * 62 / 100)); }
                catch { }
            };
            Theme.Attach(this, RefreshThemeColors);
            Shown += async (s, e) => await StartupAsync();
            FormClosing += OnClosing;
        }

        // ------------------------------------------------------------------ startup

        async Task StartupAsync()
        {
            _starting = true;
            _adminBtn.Enabled = false;
            try { await StartupCoreAsync(); }
            finally
            {
                _starting = false;
                if (!IsDisposed) _adminBtn.Enabled = !_running;
            }
        }

        async Task StartupCoreAsync()
        {
            InitLogFile();
            Log(F("{0} {1} – restore on {2} as {3}", Program.AppName, Program.VersionText, Environment.MachineName, Environment.UserName) + (Ui.IsAdmin() ? " " + T("(administrator)") : ""));
            Log(F("Package: {0}", _manifestPath));

            if (_elevatedLaunch && _launcherSid != null && !string.Equals(_launcherSid, Ui.CurrentSid(), StringComparison.OrdinalIgnoreCase))
            {
                Log(F("Warning: running as a different account ({0}) than the one that started OrclWAMP.", Environment.UserName));
                if (_unattended || !Ui.Ask(this,
                        F("Administrator rights were granted with a different account ({0}).", Environment.UserName) + "\r\n\r\n" +
                        T("Apps that install per user (and Store apps) would end up in THAT account, not yours.") + "\r\n\r\n" +
                        F("Continue as {0} anyway?", Environment.UserName) + "\r\n\r\n" + T("No = continue without administrator rights (recommended)")))
                {
                    ExitCode = ExitContinueUnelevated;
                    FormClosing -= OnClosing;
                    Close();
                    return;
                }
            }

            if (_m.RequestAdmin && !Ui.IsAdmin() && !_elevatedLaunch)
            {
                if (await ElevateAsync(false)) return;
                Log(T("Continuing without administrator rights – some installers will show their own UAC prompt."));
            }

            await CheckWingetAsync();
            if (_elevating || IsDisposed) return;
            if (_wingetOk) await MarkInstalledAsync();
            else if (_unattended) await RepairWingetAsync(); // one attempt only; marks installed apps itself on success
            if (_closeWhenStopped) { BeginInvokeSafe(Close); return; }
            _startBtn.Enabled = _wingetOk;

            // Unattended: each step runs only if nobody pressed Stop / closed the window in an earlier step.
            if (_unattended && Continue()) await ApplySettingsUnattendedAsync();
            if (_unattended && _wingetOk && Continue()) await InstallAsync(false);
            if (_unattended && Continue()) await FinishUnattendedAsync();
            Continue(); // closes the window now if that was requested during the last step
        }

        /// <summary>False (and closes the window if asked to) when the run should not go on.</summary>
        bool Continue()
        {
            if (IsDisposed || _elevating) return false;
            if (_closeWhenStopped) { BeginInvokeSafe(Close); return false; }
            return !_stopRequested;
        }

        /// <summary>
        /// Starts an elevated copy and hides this window until it exits. Returns true when the elevated copy
        /// took over (this window then closes); false to carry on here without elevation.
        /// </summary>
        async Task<bool> ElevateAsync(bool userClicked)
        {
            if (_running || _busy || _elevating) return false;
            if (Ui.IsNetworkPath(_manifestPath) || Ui.IsNetworkPath(Application.ExecutablePath))
            {
                Log(T("OrclWAMP or the package is on a network drive, which administrator processes can't see – not elevating."));
                if (userClicked) Ui.Warn(this, T("The package is on a network drive, which isn't visible to administrator processes.\r\nCopy the folder to this PC or a USB stick first."));
                return false;
            }
            var args = "/restore " + Winget.Quote(_manifestPath) + " /elevated /launcher " + Ui.CurrentSid() + (_unattended ? " /unattended" : "");
            var child = Ui.RelaunchElevated(args);
            if (child == null)
            {
                if (userClicked) Ui.Warn(this, T("Administrator rights were not granted."));
                return false;
            }
            Log(T("Started an administrator copy of OrclWAMP – waiting for it to finish…"));
            _elevating = true;
            Hide();
            int code;
            try
            {
                using (child)
                {
                    await Task.Run(() => child.WaitForExit());
                    code = child.ExitCode;
                }
            }
            catch { code = 0; }
            if (code != 0)
            {
                _elevating = false;
                Show();
                Activate();
                Log(code == ExitContinueUnelevated ? T("Continuing without administrator rights.")
                    : F("The administrator copy ended unexpectedly (exit code {0}) – continuing here without administrator rights.", code));
                _adminBtn.Visible = false;
                return false;
            }
            FormClosing -= OnClosing;
            Close();
            return true;
        }

        async Task CheckWingetAsync()
        {
            Winget.Reset();
            _wingetOk = await Task.Run(() => Winget.IsAvailable);
            if (_wingetOk)
            {
                _fixWingetBtn.Visible = Winget.IsOutdated;
                _fixWingetBtn.Text = T("Update winget");
                _wingetLabel.Text = Winget.IsOutdated
                    ? F("winget {0} is outdated – updating is recommended.", Winget.Version)
                    : F("winget {0} is ready.", Winget.Version);
                _wingetColor = Winget.IsOutdated ? Orange : Green;
                _wingetLabel.ForeColor = _wingetColor();
                Log("winget " + Winget.Version + ": " + Winget.Exe);
            }
            else
            {
                _fixWingetBtn.Text = T("Install / repair winget");
                _wingetLabel.Text = T("winget was not found on this PC – click \"Install / repair winget\".");
                _wingetColor = Red;
                _wingetLabel.ForeColor = _wingetColor();
                _fixWingetBtn.Visible = true;
                Log(T("winget was not found."));
            }
        }

        async Task MarkInstalledAsync()
        {
            using var busyMark = Ui.Busy();
            SetStatus(T("Checking which apps are already installed… (click Stop to skip)"));
            _busy = true;
            _cts = new CancellationTokenSource();
            _cancelBtn.Enabled = true;
            _bar.Style = ProgressBarStyle.Marquee;
            try
            {
                var ids = await Scanner.InstalledIdsAsync(_cts.Token);
                if (ids.Count == 0) Log(T("Warning: winget returned no installed packages – the already-installed check was skipped."));
                int n = 0;
                foreach (var i in _items)
                {
                    i.Installed = ids.Contains(i.Pkg.Id);
                    if (!i.Installed) continue;
                    n++;
                    SetItem(i, T("Already installed"), Gray);
                    if (_skipInstalled.Checked) SetChecked(i, false);
                }
                Log(F("{0} of {1} apps are already installed on this PC.", n, _items.Count));
                SetStatus(F("{0} apps to install. Review the list, then click \"Start installation\".", _items.Count - n));
            }
            catch (OperationCanceledException) { Log(T("Already-installed check skipped.")); SetStatus(T("Review the list, then click \"Start installation\".")); }
            catch (Exception ex) { Log(F("Could not check installed apps: {0}", ex.Message)); SetStatus(""); }
            finally
            {
                _busy = false;
                _cancelBtn.Enabled = false;
                _bar.Style = ProgressBarStyle.Continuous;
                if (_closeWhenStopped && !_starting) BeginInvokeSafe(Close);
            }
        }

        async Task RepairWingetAsync()
        {
            if (_busy || _running) return;
            using var busyMark = Ui.Busy();
            _busy = true;
            _fixWingetBtn.Enabled = _startBtn.Enabled = false;
            UseWaitCursor = true;
            try
            {
                if (!Winget.IsAvailable)
                {
                    // Right after Windows setup App Installer is often present but not registered yet.
                    for (int attempt = 1; attempt <= 3 && !Winget.IsAvailable; attempt++)
                    {
                        SetStatus(F("Registering App Installer (attempt {0} of 3)…", attempt));
                        Log(T("Registering the built-in App Installer package…"));
                        await Winget.RunProcessAsync("powershell.exe",
                            "-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\"",
                            l => Log("  " + l), CancellationToken.None, TimeSpan.FromMinutes(3));
                        Winget.Reset();
                        if (!await Task.Run(() => Winget.IsAvailable) && attempt < 3) await Task.Delay(15000);
                    }
                }
                if (!await Task.Run(() => Winget.IsAvailable) || Winget.IsOutdated)
                {
                    SetStatus(T("Downloading winget from Microsoft (GitHub) – this can take a few minutes…"));
                    Log(T("Downloading App Installer and its dependencies from github.com/microsoft/winget-cli …"));
                    var script = Path.Combine(Path.GetTempPath(), "orclwamp-install-winget.ps1");
                    File.WriteAllText(script, InstallWingetScript, new UTF8Encoding(true));
                    var r = await Winget.RunProcessAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + Winget.Quote(script),
                        l => Log("  " + l), CancellationToken.None, TimeSpan.FromMinutes(20));
                    Log(r.ExitCode == 0 ? T("App Installer setup finished.") : F("App Installer setup failed (exit code {0}).", r.ExitCode));
                    try { File.Delete(script); } catch { }
                }
            }
            catch (Exception ex) { Log(F("winget setup failed: {0}", ex.Message)); }
            finally { UseWaitCursor = false; _fixWingetBtn.Enabled = true; }

            try { await CheckWingetAsync(); }
            finally { _busy = false; }
            if (_closeWhenStopped) { if (!_starting) BeginInvokeSafe(Close); return; }
            if (_wingetOk)
            {
                await MarkInstalledAsync();
                _startBtn.Enabled = true;
            }
            else if (!_unattended)
            {
                if (Ui.Ask(this, T("winget could not be installed automatically.") + "\r\n\r\n" +
                                 T("Install \"App Installer\" from the Microsoft Store (or run Windows Update), then click \"Install / repair winget\" again.\r\n\r\nOpen the Microsoft Store page now?")))
                    Ui.Open("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");
            }
        }

        const string InstallWingetScript = @"
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$t = Join-Path $env:TEMP 'orclwamp-winget'
New-Item -ItemType Directory -Force $t | Out-Null
$base = 'https://github.com/microsoft/winget-cli/releases/latest/download/'
$arch = switch ($env:PROCESSOR_ARCHITECTURE) { 'ARM64' { 'arm64' } 'x86' { 'x86' } default { 'x64' } }
try {
    Write-Output 'Downloading dependencies (VCLibs, UI.Xaml, ...)'
    Invoke-WebRequest ($base + 'DesktopAppInstaller_Dependencies.zip') -OutFile ""$t\deps.zip"" -UseBasicParsing
    Expand-Archive ""$t\deps.zip"" ""$t\deps"" -Force
    Get-ChildItem ""$t\deps"" -Recurse -Include *.appx, *.msix | Where-Object { $_.FullName -match ""\\$arch\\"" } | ForEach-Object {
        Write-Output ""Installing $($_.Name)""
        try { Add-AppxPackage -Path $_.FullName } catch { Write-Output ""  skipped: $($_.Exception.Message)"" }
    }
} catch { Write-Output ""Dependencies: $($_.Exception.Message)"" }
Write-Output 'Downloading App Installer'
Invoke-WebRequest ($base + 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe.msixbundle') -OutFile ""$t\AppInstaller.msixbundle"" -UseBasicParsing
Write-Output 'Installing App Installer'
Add-AppxPackage -Path ""$t\AppInstaller.msixbundle"" -ForceApplicationShutdown
Remove-Item $t -Recurse -Force -ErrorAction SilentlyContinue
Write-Output 'Done'
";

        // ------------------------------------------------------------------ install

        static bool IsFailure(InstallOutcome? o) =>
            o == InstallOutcome.Failed || o == InstallOutcome.NotFound || o == InstallOutcome.TimedOut || o == InstallOutcome.Cancelled;

        async Task InstallAsync(bool retryFailed)
        {
            if (_running || _busy || _elevating || !_wingetOk) return;
            if (retryFailed) foreach (var i in _items) SetChecked(i, IsFailure(i.Outcome));
            var queue = _items.Where(i => i.Lvi.Checked).ToList();
            if (queue.Count == 0) { if (!_unattended) Ui.Info(this, T("No apps are ticked.")); return; }

            // Lock the UI *before* the first await so a second click can't start a parallel run.
            using var busy = Ui.Busy();
            _running = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _startBtn.Enabled = _retryBtn.Enabled = _adminBtn.Enabled = _fixWingetBtn.Enabled = false;
            _cancelBtn.Enabled = true;
            _pin.Enabled = _skipInstalled.Enabled = _timeout.Enabled = false;
            bool doRestart = false;
            string summary = "";
            Stopwatch sw = null;
            try
            {
                if (!await WaitForInternetAsync(ct)) return;
                Ui.KeepAwake(true);
                sw = Stopwatch.StartNew();
                _bar.Style = ProgressBarStyle.Continuous;
                _bar.Maximum = queue.Count;
                _bar.Value = 0;
                var timeout = TimeSpan.FromMinutes((double)_timeout.Value);
                Log("");
                Log("=== " + F("Installing {0} app(s) – {1}", queue.Count, DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + " ===");
                foreach (var i in queue) SetItem(i, T("Queued"), Normal);

                int n = 0;
                foreach (var i in queue)
                {
                    if (ct.IsCancellationRequested) { SetItem(i, T("Skipped (stopped)"), Gray); i.Outcome = InstallOutcome.Cancelled; continue; }
                    n++;
                    SetStatus(F("Installing {0} of {1}: {2}", n, queue.Count, i.Pkg.Name));
                    SetItem(i, T("Installing…"), Blue);
                    i.Lvi.EnsureVisible();
                    Log("");
                    Log($"[{n}/{queue.Count}] {i.Pkg.Name}  ({i.Pkg.Id}, {i.Pkg.Source})");

                    var r = await InstallOneAsync(i, timeout, ct);
                    var outcome = Winget.Classify(r);
                    i.Outcome = outcome;
                    i.NeedsNonAdmin = Winget.NeedsNonAdmin(r);
                    var text = Winget.Describe(r);
                    Log("   => " + text);
                    switch (outcome)
                    {
                        case InstallOutcome.Installed:
                        case InstallOutcome.AlreadyInstalled:
                        case InstallOutcome.RebootRequired:
                            SetItem(i, text, Green); SetChecked(i, false); break;
                        case InstallOutcome.Cancelled:
                            SetItem(i, text, Gray); break;
                        default:
                            SetItem(i, text, Red); break;
                    }
                    _bar.Value = Math.Min(_bar.Maximum, _bar.Value + 1);
                }

                int ok = queue.Count(i => i.Outcome == InstallOutcome.Installed || i.Outcome == InstallOutcome.RebootRequired);
                int already = queue.Count(i => i.Outcome == InstallOutcome.AlreadyInstalled);
                var failed = queue.Where(i => IsFailure(i.Outcome)).ToList();
                bool reboot = queue.Any(i => i.Outcome == InstallOutcome.RebootRequired);
                summary = F("Finished in {0}: {1} installed, {2} already present, {3} failed/skipped.", sw.Elapsed.ToString("h\\:mm\\:ss"), ok, already, failed.Count);
                Log("");
                Log("=== " + summary + " ===");
                foreach (var f in failed) Log("   " + F("not installed: {0} ({1})", f.Pkg.Name, f.Pkg.Id));
                SetStatus(summary);

                // Unattended: the restart comes only after app settings, personal files and the report (FinishUnattendedAsync).
                doRestart = _restart.Checked && !ct.IsCancellationRequested && !_unattended;
                if (doRestart) ScheduleRestart();

                if (!_unattended && !_closeWhenStopped)
                {
                    var msg = summary;
                    if (failed.Count > 0) msg += "\r\n\r\n" + T("Click \"Retry failed\" to try the failed apps again.");
                    if (failed.Any(f => f.NeedsNonAdmin))
                        msg += "\r\n\r\n" + T("Some apps refuse to install as administrator. Close OrclWAMP, start it again, answer \"No\" to the administrator prompt and click \"Retry failed\".");
                    if (reboot && !doRestart) msg += "\r\n\r\n" + T("Some apps need a restart to finish installing.");
                    if (_m.ManualApps.Count > 0) msg += "\r\n\r\n" + F("Don't forget the {0} apps on the manual-install list (button \"Manual apps & downloads\").", _m.ManualApps.Count);
                    if (doRestart) msg += "\r\n\r\n" + T("The PC restarts in 60 seconds.");
                    MessageBox.Show(this, msg, Program.AppName, MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Log(F("Unexpected error: {0}", ex.Message));
                SetStatus(T("Stopped because of an error – see the log."));
            }
            finally
            {
                Ui.KeepAwake(false);
                _running = false;
                _cancelBtn.Enabled = false;
                _startBtn.Enabled = _fixWingetBtn.Enabled = true;
                _adminBtn.Enabled = !_starting;
                _pin.Enabled = _skipInstalled.Enabled = _timeout.Enabled = true;
                _retryBtn.Enabled = _items.Any(i => IsFailure(i.Outcome));
                if (_closeWhenStopped && !_starting) BeginInvokeSafe(Close); // unattended: StartupCoreAsync closes it
            }
        }

        /// <summary>Installs one package: pinned version first (if wanted), retries transient errors.</summary>
        async Task<ProcResult> InstallOneAsync(Item i, TimeSpan timeout, CancellationToken ct)
        {
            string version = _pin.Checked && Winget.IsPinnableVersion(i.Pkg.Version) ? i.Pkg.Version : null;
            ProcResult r = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                r = await RunInstall(i, version, timeout, ct);
                var outcome = Winget.Classify(r);
                if (version != null && (outcome == InstallOutcome.Failed || outcome == InstallOutcome.NotFound) && !Winget.IsTransient(r))
                {
                    Log("   " + F("Version {0} could not be installed ({1}) – trying the latest version instead.", version, Winget.Describe(r)));
                    version = null;
                    r = await RunInstall(i, null, timeout, ct);
                }
                if (!Winget.IsTransient(r) || attempt == 3) break;
                Log("   " + F("{0} – waiting 60 seconds and trying again ({1} of 3)…", Winget.Describe(r), attempt + 1));
                SetItem(i, T("Waiting to retry…"), Orange);
                try { await Task.Delay(TimeSpan.FromSeconds(60), ct); } catch (OperationCanceledException) { r.Cancelled = true; break; }
            }
            return r;
        }

        Task<ProcResult> RunInstall(Item i, string version, TimeSpan timeout, CancellationToken ct)
        {
            string lastProgress = null;
            return Winget.RunAsync(Winget.InstallArgs(i.Pkg.Id, i.Pkg.Source, version), line =>
            {
                if (Winget.IsNoise(line, out var progress))
                {
                    if (!string.IsNullOrEmpty(progress) && progress != lastProgress)
                    {
                        lastProgress = progress;
                        BeginInvokeSafe(() => SetItem(i, F("Downloading {0}", progress), Blue));
                    }
                    return;
                }
                Log("   " + line.Trim());
                if (line.IndexOf("Starting package install", StringComparison.OrdinalIgnoreCase) >= 0)
                    BeginInvokeSafe(() => SetItem(i, T("Installing…"), Blue));
            }, ct, timeout);
        }

        async Task<bool> WaitForInternetAsync(CancellationToken ct)
        {
            if (await IsOnlineAsync()) return true;
            Log(T("No internet connection detected."));
            if (!_unattended)
                return Ui.Ask(this, T("This PC doesn't seem to be connected to the internet. winget needs internet access to download the apps.\r\n\r\nTry anyway?"));
            // Unattended: wait up to 10 minutes for the network to come up (Wi-Fi after first logon is often slow).
            for (int k = 0; k < 40; k++)
            {
                SetStatus(T("Waiting for an internet connection…"));
                try { await Task.Delay(15000, ct); } catch (OperationCanceledException) { return false; }
                if (await IsOnlineAsync()) { Log(T("Internet connection is up.")); return true; }
            }
            Log(T("Still no internet connection after 10 minutes – installation not started."));
            SetStatus(T("No internet connection – installation not started."));
            return false;
        }

        static async Task<bool> IsOnlineAsync()
        {
            foreach (var url in new[] { "http://www.msftconnecttest.com/connecttest.txt", Winget.WingetSourceArgument + "/source.msix" })
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                var work = Task.Run(() => req.GetResponseAsync());
                // HttpWebRequest.Timeout doesn't apply to async calls – race it against a delay instead.
                if (await Task.WhenAny(work, Task.Delay(8000)) != work) { try { req.Abort(); } catch { } continue; }
                try { using (await work) return true; }
                catch (WebException ex) when (ex.Response != null) { ex.Response.Dispose(); return true; } // got an HTTP answer => online
                catch { }
            }
            return false;
        }

        // ------------------------------------------------------------------ helpers

        void SetAll(Func<Item, bool> f)
        {
            if (_running || _busy) return;
            foreach (var i in _items) SetChecked(i, f(i));
        }

        void SetChecked(Item i, bool value)
        {
            _programmaticCheck = true;
            try { i.Lvi.Checked = value; }
            finally { _programmaticCheck = false; }
        }

        static void SetItem(Item i, string status, Func<Color> color)
        {
            i.Lvi.SubItems[4].Text = status;
            i.Color = color;
            i.Lvi.ForeColor = color();
        }

        void RefreshThemeColors()
        {
            _lv.BeginUpdate();
            foreach (var i in _items) { i.Lvi.ForeColor = i.Color(); i.Lvi.BackColor = Theme.Surface; }
            _lv.EndUpdate();
            _wingetLabel.ForeColor = _wingetColor();
        }

        void SetStatus(string text) => BeginInvokeSafe(() => _status.Text = text);

        void BeginInvokeSafe(Action a)
        {
            if (IsDisposed || Disposing) return;
            if (InvokeRequired)
            {
                try { BeginInvoke(a); } catch (InvalidOperationException) { }
            }
            else a();
        }

        void InitLogFile()
        {
            var name = "OrclWAMP-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log";
            foreach (var dir in new[] { Path.GetDirectoryName(_manifestPath), Path.GetTempPath() })
            {
                try
                {
                    var path = Path.Combine(dir, name);
                    File.WriteAllText(path, "", Encoding.UTF8);
                    _logFile = path;
                    return;
                }
                catch { }
            }
        }

        void Log(string line)
        {
            var stamped = (line.Length == 0 ? "" : DateTime.Now.ToString("HH:mm:ss") + "  " + line) + "\r\n";
            lock (_logGate)
            {
                if (_logFile != null) { try { File.AppendAllText(_logFile, stamped, Encoding.UTF8); } catch { } }
            }
            BeginInvokeSafe(() =>
            {
                if (_log.TextLength > 2000000) _log.Clear();
                _log.AppendText(stamped);
            });
        }

        void OpenManualApps()
        {
            if (_m.ManualApps.Count == 0) { Ui.Info(this, T("Every app in this package can be installed by winget – nothing to install manually.")); return; }
            using (var f = new ManualAppsForm(_m.ManualApps, () =>
            {
                // Keep links the user added in the package (if the USB stick is writable).
                try { _m.Save(_manifestPath); } catch (Exception ex) { Log(F("Could not save the link in the package: {0}", ex.Message)); }
            }, ShowManualList))
                f.ShowDialog(this);
        }

        readonly string _settingsDir;
        readonly SettingsPack _settingsPack;

        readonly string _packageDir, _appDir, _filesDir;
        readonly PackageSecrets _secrets;
        readonly AppConfigPack _appPack;
        readonly FilesPack _filesPack;
        readonly ReportData _report;

        void OpenSettings()
        {
            if (_running || _busy) { Ui.Info(this, T("Please wait until the installation has finished.")); return; }
            using (var f = new SettingsForm(_settingsPack, _settingsDir, _secrets))
            {
                f.ShowDialog(this);
                if (f.LastResult != null) { _report.Settings = f.LastResult; _report.SettingsGroups = f.AppliedIds; }
            }
        }

        void OpenAppConfigs()
        {
            if (_running || _busy) { Ui.Info(this, T("Please wait until the installation has finished.")); return; }
            using (var f = new AppConfigsForm(_appPack, _appDir, _secrets))
            {
                f.ShowDialog(this);
                if (f.Result != null) { _report.AppConfigs = f.Result; _report.AppConfigNames = f.AppliedIds.Select(id => T(AppConfigs.Find(id).Name)).ToList(); }
            }
        }

        void OpenFiles()
        {
            if (_running || _busy) { Ui.Info(this, T("Please wait until the installation has finished.")); return; }
            using (var f = new FilesForm(_filesPack, _filesDir))
            {
                f.ShowDialog(this);
                if (f.Result != null) _report.Files = f.Result;
            }
        }

        /// <summary>Fills in the app results and writes the HTML report next to the package.</summary>
        string WriteReport()
        {
            _report.Apps = _items.Select(i =>
            {
                int state = i.Outcome == null ? (i.Installed ? 1 : 0)
                          : i.Outcome == InstallOutcome.Installed || i.Outcome == InstallOutcome.AlreadyInstalled || i.Outcome == InstallOutcome.RebootRequired ? 1 : -1;
                var status = i.Outcome == null ? (i.Installed ? T("Already installed") : T("Not installed in this session")) : i.Lvi.SubItems[4].Text;
                return (i.Pkg.Name, i.Pkg.Id, status, state);
            }).ToList();
            return MigrationReport.Write(_report, _packageDir);
        }

        void OpenReport()
        {
            var path = WriteReport();
            if (path != null) Ui.Open(path); else Ui.Warn(this, T("The report could not be saved."));
        }

        /// <summary>Unattended mode, after the apps: app settings, personal files, then the report.</summary>
        void ScheduleRestart()
        {
            Log(T("Restarting the PC in 60 seconds (run \"shutdown /a\" to abort)."));
            var msg = T("OrclWAMP finished installing apps. Restarting in 60 seconds.").Replace("\"", "'");
            try { Process.Start(new ProcessStartInfo("shutdown", "/r /t 60 /c \"" + msg + "\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose(); }
            catch (Exception ex) { Log(F("Could not schedule restart: {0}", ex.Message)); }
        }

        /// <summary>Marks an unattended phase as running: blocks other actions, enables Stop, closes the window afterwards if asked to.</summary>
        IDisposable Phase()
        {
            _busy = _phaseActive = true;
            _cts = new CancellationTokenSource();
            _cancelBtn.Enabled = true;
            _startBtn.Enabled = false;
            var busy = Ui.Busy();
            return new Disposer(() =>
            {
                busy.Dispose();
                _busy = _phaseActive = false;
                _cancelBtn.Enabled = false;
                _startBtn.Enabled = _wingetOk;   // closing (if requested) is done by StartupCoreAsync.Continue()
            });
        }

        async Task FinishUnattendedAsync()
        {
            using (Phase())
            {
            var ct = _cts.Token;
            if (_appPack != null && _appPack.Apps.Count > 0)
            {
                Log(T("Applying app settings & bookmarks…"));
                try
                {
                    // Protected items (SSH keys) need the password – not possible without a person at the PC.
                    var ids = _appPack.Apps.Where(a => !AppConfigs.Find(a.Id).Sensitive && !AppConfigs.Find(a.Id).RunsCode).Select(a => a.Id).ToList();
                    foreach (var a in _appPack.Apps.Where(a => AppConfigs.Find(a.Id).Sensitive || AppConfigs.Find(a.Id).RunsCode))
                        Log("   " + F("{0}: skipped in unattended mode – apply it with the \"App settings & bookmarks\" button.", T(AppConfigs.Find(a.Id).Name)));
                    _report.AppConfigs = await Task.Run(() => AppConfigs.Apply(_appPack, _appDir, ids, null, l => Log("   " + l)));
                    _report.AppConfigNames = ids.Select(id => T(AppConfigs.Find(id).Name)).ToList();
                    Log(F("App settings: {0} applied, {1} skipped.", _report.AppConfigs.Applied, _report.AppConfigs.Skipped));
                }
                catch (Exception ex) { Log(ex.Message); }
            }
            if (_filesPack != null && _filesPack.Folders.Count > 0)
            {
                Log(T("Copying personal files…"));
                try
                {
                    _report.Files = await Task.Run(() => FilesForm.CopyAllUnattended(_filesPack, _filesDir, l => Log("   " + l), ct));
                    Log(F("Personal files: {0} copied, {1} skipped, {2} failed.", _report.Files.Copied, _report.Files.Skipped, _report.Files.Failed));
                }
                catch (Exception ex) { Log(ex.Message); }
            }
            var report = WriteReport();
            if (report != null) Log(T("Migration report:") + " " + report);
            if (_restart.Checked && !ct.IsCancellationRequested && !_closeWhenStopped && !_stopRequested) ScheduleRestart();
            }
        }

        /// <summary>Unattended mode: apply every settings group in the package (a backup is made first).</summary>
        async Task ApplySettingsUnattendedAsync()
        {
            if (_settingsPack == null || _settingsPack.Groups.Count == 0) return;
            using var phase = Phase();
            Log(F("Applying Windows settings from {0}…", _settingsPack.SourceComputer));
            try
            {
                var ids = _settingsPack.Groups.Select(g => g.Id).ToList();
                if (WinSettings.WifiNeedsPassword(_settingsPack, _settingsDir))
                {
                    ids.Remove("wifi");
                    Log("   " + T("Wi-Fi networks skipped – they are password-protected (use the \"Windows settings\" button)."));
                }
                var r = await Task.Run(() => WinSettings.Apply(_settingsPack, _settingsDir, ids, l => Log("   " + l)));
                _report.Settings = r;
                _report.SettingsGroups = ids;
                Log(F("Windows settings: {0} applied, {1} skipped. Backup: {2}", r.Applied, r.Skipped, r.BackupFile));
                if (r.RestartExplorer && !_restart.Checked) { await Task.Run(() => WinSettings.RestartExplorer()); Log(T("File Explorer restarted.")); }
            }
            catch (Exception ex) { Log(F("Windows settings could not be applied: {0}", ex.Message)); }
        }

        void ShowManualList()
        {
            // Always regenerate: links may have been added in "Manual apps & downloads".
            var report = Path.Combine(Path.GetTempPath(), "OrclWAMP-" + PackageWriter.ReportName);
            try { File.WriteAllText(report, PackageWriter.Report(_m), new UTF8Encoding(false)); }
            catch (Exception ex) { Ui.Error(this, ex.Message); return; }
            Ui.Open(report);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_busy && !_running)
            {
                // An unattended phase (settings / app settings / files) is running: stop it, then close.
                if (e.CloseReason == CloseReason.UserClosing && !Ui.Ask(this, T("OrclWAMP is still working. Stop and close?"))) { e.Cancel = true; return; }
                _closeWhenStopped = _stopRequested = true;
                if (_busy || _running)   // it may have finished while the question was open – then just close
                {
                    _cts?.Cancel();
                    e.Cancel = true;
                    return;
                }
            }
            if (_running)
            {
                if (e.CloseReason == CloseReason.UserClosing && !Ui.Ask(this, T("Apps are still being installed. Stop and close?"))) { e.Cancel = true; return; }
                // Cancel and wait for the running installer to be terminated; the install loop closes the window afterwards.
                _closeWhenStopped = _stopRequested = true;
                if (_running || _busy)
                {
                    _cts?.Cancel();
                    e.Cancel = true;
                    SetStatus(T("Stopping the current installer…"));
                    return;
                }
            }
            _cts?.Cancel();
            Ui.KeepAwake(false);
            _secrets?.Dispose(); // removes the decrypted temp files
            WinSettings.SecretsRoot = null;
        }
    }
}
