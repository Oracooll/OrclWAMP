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
        readonly Label _wingetLabel = Ui.Label("Checking winget…");
        readonly CheckBox _pin, _skipInstalled, _restart;
        readonly NumericUpDown _timeout = new NumericUpDown();
        readonly ProgressBar _bar = new ProgressBar();
        readonly Label _status = Ui.Label("");
        readonly Button _startBtn, _cancelBtn, _retryBtn, _fixWingetBtn, _adminBtn;
        readonly object _logGate = new object();
        CancellationTokenSource _cts;
        bool _running, _busy, _wingetOk, _programmaticCheck, _closeWhenStopped, _elevating, _starting;
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
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – Install apps on this PC", 1100, 760);
            SuspendLayout();

            // ---- list + log ----
            _lv.Columns.Add("Name");
            _lv.Columns.Add("Package ID");
            _lv.Columns.Add("Version");
            _lv.Columns.Add("Source");
            _lv.Columns.Add("Status");
            Ui.AutoSizeColumns(_lv, 300, 290, 110, 70, 320);
            _lv.ItemCheck += (s, e) => { if ((_running || _busy) && !_programmaticCheck) e.NewValue = e.CurrentValue; };
            var cm = new ContextMenuStrip();
            cm.Items.Add("Tick all", null, (s, e) => SetAll(_ => true));
            cm.Items.Add("Untick all", null, (s, e) => SetAll(_ => false));
            cm.Items.Add("Tick only failed", null, (s, e) => SetAll(i => IsFailure(i.Outcome)));
            _lv.ContextMenuStrip = cm;
            _split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            _split.Panel2.Controls.Add(_log);
            Controls.Add(_split);

            // ---- bottom ----
            var bottom = new TableLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, ColumnCount = 1, Padding = new Padding(Ui.S(6)) };
            var opts = Ui.Flow(DockStyle.Fill);
            _pin = Ui.Check("Install the exact same versions", m.PinVersions);
            _skipInstalled = Ui.Check("Skip apps that are already installed", true);
            _skipInstalled.CheckedChanged += (s, e) => { foreach (var i in _items.Where(x => x.Installed && x.Outcome == null)) SetChecked(i, !_skipInstalled.Checked); };
            _restart = Ui.Check("Restart the PC when finished", m.RestartWhenDone);
            opts.Controls.Add(_pin);
            opts.Controls.Add(_skipInstalled);
            opts.Controls.Add(_restart);
            opts.Controls.Add(Ui.Label("Timeout per app (min):"));
            _timeout.Minimum = 5; _timeout.Maximum = 240; _timeout.Width = Ui.S(60); _timeout.Anchor = AnchorStyles.Left;
            _timeout.Value = Math.Max(5, Math.Min(240, m.TimeoutMinutes));
            opts.Controls.Add(_timeout);
            bottom.Controls.Add(opts);

            _bar.Dock = DockStyle.Fill; _bar.Height = Ui.S(18); _bar.Margin = new Padding(Ui.S(6), Ui.S(2), Ui.S(6), Ui.S(2));
            bottom.Controls.Add(_bar);

            var buttons = Ui.Flow(DockStyle.Fill);
            _startBtn = Ui.Button("Start installation", async (s, e) => await InstallAsync(false), true);
            _startBtn.Padding = new Padding(Ui.S(14), Ui.S(6), Ui.S(14), Ui.S(6));
            _startBtn.Enabled = false;
            _cancelBtn = Ui.Button("Stop", (s, e) => { _cts?.Cancel(); Log(_running ? "Stopping… (the current installer is being terminated)" : "Skipping the check…"); });
            _cancelBtn.Enabled = false;
            _retryBtn = Ui.Button("Retry failed", async (s, e) => await InstallAsync(true));
            _retryBtn.Enabled = false;
            buttons.Controls.Add(_startBtn);
            buttons.Controls.Add(_cancelBtn);
            buttons.Controls.Add(_retryBtn);
            buttons.Controls.Add(Ui.Button("Manual-install list", (s, e) => ShowManualList()));
            buttons.Controls.Add(Ui.Button("Open log", (s, e) => { if (_logFile != null && File.Exists(_logFile)) Ui.Open(_logFile); else Ui.Info(this, "No log has been written yet."); }));
            buttons.Controls.Add(_status);
            bottom.Controls.Add(buttons);
            Controls.Add(bottom);

            // ---- header ----
            var head = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), Ui.S(2)) };
            var headerBar = Ui.HeaderBar("Install apps on this PC");
            headerBar.Dock = DockStyle.Fill;
            head.Controls.Add(headerBar);
            var info = new Label { AutoSize = true, Font = Ui.BoldFont, Margin = new Padding(Ui.S(3), Ui.S(4), Ui.S(3), Ui.S(2)) };
            info.Text = $"{m.Packages.Count} apps from \"{m.SourceComputer}\"  ·  package created {PackageWriter.FormatDate(m.CreatedUtc)}";
            head.Controls.Add(info);
            var wingetRow = Ui.Flow(DockStyle.Fill);
            wingetRow.Padding = new Padding(0);
            wingetRow.Controls.Add(_wingetLabel);
            _fixWingetBtn = Ui.Button("Install / repair winget", async (s, e) => await RepairWingetAsync());
            _fixWingetBtn.Visible = false;
            wingetRow.Controls.Add(_fixWingetBtn);
            _adminBtn = Ui.Button("Restart as administrator", async (s, e) => await ElevateAsync(true));
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
                it.SubItems.Add("Waiting");
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
            Log($"{Program.AppName} {Program.VersionText} – restore on {Environment.MachineName} as {Environment.UserName}" + (Ui.IsAdmin() ? " (administrator)" : ""));
            Log("Package: " + _manifestPath);

            if (_elevatedLaunch && _launcherSid != null && !string.Equals(_launcherSid, Ui.CurrentSid(), StringComparison.OrdinalIgnoreCase))
            {
                Log("Warning: running as a different account (" + Environment.UserName + ") than the one that started OrclWAMP.");
                if (_unattended || !Ui.Ask(this,
                        "Administrator rights were granted with a different account (" + Environment.UserName + ").\r\n\r\n" +
                        "Apps that install per user (and Store apps) would end up in THAT account, not yours.\r\n\r\n" +
                        "Continue as " + Environment.UserName + " anyway?\r\n\r\nNo = continue without administrator rights (recommended)"))
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
                Log("Continuing without administrator rights – some installers will show their own UAC prompt.");
            }

            await CheckWingetAsync();
            if (_elevating || IsDisposed) return;
            if (_wingetOk) await MarkInstalledAsync();
            else if (_unattended) await RepairWingetAsync(); // one attempt only; marks installed apps itself on success
            _startBtn.Enabled = _wingetOk;

            if (_unattended && _wingetOk && !_elevating && !IsDisposed) await InstallAsync(false);
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
                Log("OrclWAMP or the package is on a network drive, which administrator processes can't see – not elevating.");
                if (userClicked) Ui.Warn(this, "The package is on a network drive, which isn't visible to administrator processes.\r\nCopy the folder to this PC or a USB stick first.");
                return false;
            }
            var args = "/restore " + Winget.Quote(_manifestPath) + " /elevated /launcher " + Ui.CurrentSid() + (_unattended ? " /unattended" : "");
            var child = Ui.RelaunchElevated(args);
            if (child == null)
            {
                if (userClicked) Ui.Warn(this, "Administrator rights were not granted.");
                return false;
            }
            Log("Started an administrator copy of OrclWAMP – waiting for it to finish…");
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
                Log(code == ExitContinueUnelevated ? "Continuing without administrator rights."
                    : "The administrator copy ended unexpectedly (exit code " + code + ") – continuing here without administrator rights.");
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
                _fixWingetBtn.Text = "Update winget";
                _wingetLabel.Text = Winget.IsOutdated
                    ? "winget " + Winget.Version + " is outdated – updating is recommended."
                    : "winget " + Winget.Version + " is ready.";
                _wingetColor = Winget.IsOutdated ? Orange : Green;
                _wingetLabel.ForeColor = _wingetColor();
                Log("winget " + Winget.Version + ": " + Winget.Exe);
            }
            else
            {
                _fixWingetBtn.Text = "Install / repair winget";
                _wingetLabel.Text = "winget was not found on this PC – click \"Install / repair winget\".";
                _wingetColor = Red;
                _wingetLabel.ForeColor = _wingetColor();
                _fixWingetBtn.Visible = true;
                Log("winget was not found.");
            }
        }

        async Task MarkInstalledAsync()
        {
            SetStatus("Checking which apps are already installed… (click Stop to skip)");
            _busy = true;
            _cts = new CancellationTokenSource();
            _cancelBtn.Enabled = true;
            _bar.Style = ProgressBarStyle.Marquee;
            try
            {
                var ids = await Scanner.InstalledIdsAsync(_cts.Token);
                if (ids.Count == 0) Log("Warning: winget returned no installed packages – the already-installed check was skipped.");
                int n = 0;
                foreach (var i in _items)
                {
                    i.Installed = ids.Contains(i.Pkg.Id);
                    if (!i.Installed) continue;
                    n++;
                    SetItem(i, "Already installed", Gray);
                    if (_skipInstalled.Checked) SetChecked(i, false);
                }
                Log($"{n} of {_items.Count} apps are already installed on this PC.");
                SetStatus($"{_items.Count - n} apps to install. Review the list, then click \"Start installation\".");
            }
            catch (OperationCanceledException) { Log("Already-installed check skipped."); SetStatus("Review the list, then click \"Start installation\"."); }
            catch (Exception ex) { Log("Could not check installed apps: " + ex.Message); SetStatus(""); }
            finally
            {
                _busy = false;
                _cancelBtn.Enabled = false;
                _bar.Style = ProgressBarStyle.Continuous;
            }
        }

        async Task RepairWingetAsync()
        {
            if (_busy || _running) return;
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
                        SetStatus($"Registering App Installer (attempt {attempt} of 3)…");
                        Log("Registering the built-in App Installer package…");
                        await Winget.RunProcessAsync("powershell.exe",
                            "-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -RegisterByFamilyName -MainPackage Microsoft.DesktopAppInstaller_8wekyb3d8bbwe\"",
                            l => Log("  " + l), CancellationToken.None, TimeSpan.FromMinutes(3));
                        Winget.Reset();
                        if (!await Task.Run(() => Winget.IsAvailable) && attempt < 3) await Task.Delay(15000);
                    }
                }
                if (!await Task.Run(() => Winget.IsAvailable) || Winget.IsOutdated)
                {
                    SetStatus("Downloading winget from Microsoft (GitHub) – this can take a few minutes…");
                    Log("Downloading App Installer and its dependencies from github.com/microsoft/winget-cli …");
                    var script = Path.Combine(Path.GetTempPath(), "orclwamp-install-winget.ps1");
                    File.WriteAllText(script, InstallWingetScript, new UTF8Encoding(true));
                    var r = await Winget.RunProcessAsync("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -File " + Winget.Quote(script),
                        l => Log("  " + l), CancellationToken.None, TimeSpan.FromMinutes(20));
                    Log(r.ExitCode == 0 ? "App Installer setup finished." : "App Installer setup failed (exit code " + r.ExitCode + ").");
                    try { File.Delete(script); } catch { }
                }
            }
            catch (Exception ex) { Log("winget setup failed: " + ex.Message); }
            finally { UseWaitCursor = false; _fixWingetBtn.Enabled = true; }

            try { await CheckWingetAsync(); }
            finally { _busy = false; }
            if (_wingetOk)
            {
                await MarkInstalledAsync();
                _startBtn.Enabled = true;
            }
            else if (!_unattended)
            {
                if (Ui.Ask(this, "winget could not be installed automatically.\r\n\r\n" +
                                 "Install \"App Installer\" from the Microsoft Store (or run Windows Update), then click \"Install / repair winget\" again.\r\n\r\nOpen the Microsoft Store page now?"))
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
            if (queue.Count == 0) { if (!_unattended) Ui.Info(this, "No apps are ticked."); return; }

            // Lock the UI *before* the first await so a second click can't start a parallel run.
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
                Log($"=== Installing {queue.Count} app(s) – {DateTime.Now:yyyy-MM-dd HH:mm} ===");
                foreach (var i in queue) SetItem(i, "Queued", Normal);

                int n = 0;
                foreach (var i in queue)
                {
                    if (ct.IsCancellationRequested) { SetItem(i, "Skipped (stopped)", Gray); i.Outcome = InstallOutcome.Cancelled; continue; }
                    n++;
                    SetStatus($"Installing {n} of {queue.Count}: {i.Pkg.Name}");
                    SetItem(i, "Installing…", Blue);
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
                summary = $"Finished in {sw.Elapsed:h\\:mm\\:ss}: {ok} installed, {already} already present, {failed.Count} failed/skipped.";
                Log("");
                Log("=== " + summary + " ===");
                foreach (var f in failed) Log("   not installed: " + f.Pkg.Name + " (" + f.Pkg.Id + ")");
                SetStatus(summary);

                doRestart = _restart.Checked && !ct.IsCancellationRequested;
                if (doRestart)
                {
                    Log("Restarting the PC in 60 seconds (run \"shutdown /a\" to abort).");
                    try { Process.Start(new ProcessStartInfo("shutdown", "/r /t 60 /c \"OrclWAMP finished installing apps. Restarting in 60 seconds.\"") { CreateNoWindow = true, UseShellExecute = false })?.Dispose(); }
                    catch (Exception ex) { Log("Could not schedule restart: " + ex.Message); }
                }

                if (!_unattended && !_closeWhenStopped)
                {
                    var msg = summary;
                    if (failed.Count > 0) msg += "\r\n\r\nClick \"Retry failed\" to try the failed apps again.";
                    if (failed.Any(f => f.NeedsNonAdmin))
                        msg += "\r\n\r\nSome apps refuse to install as administrator. Close OrclWAMP, start it again, answer \"No\" to the administrator prompt and click \"Retry failed\".";
                    if (reboot && !doRestart) msg += "\r\n\r\nSome apps need a restart to finish installing.";
                    if (_m.ManualApps.Count > 0) msg += $"\r\n\r\nDon't forget the {_m.ManualApps.Count} apps on the manual-install list (button \"Manual-install list\").";
                    if (doRestart) msg += "\r\n\r\nThe PC restarts in 60 seconds.";
                    MessageBox.Show(this, msg, Program.AppName, MessageBoxButtons.OK, failed.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                Log("Unexpected error: " + ex.Message);
                SetStatus("Stopped because of an error – see the log.");
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
                if (_closeWhenStopped) BeginInvoke(new Action(Close));
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
                    Log($"   Version {version} could not be installed ({Winget.Describe(r)}) – trying the latest version instead.");
                    version = null;
                    r = await RunInstall(i, null, timeout, ct);
                }
                if (!Winget.IsTransient(r) || attempt == 3) break;
                Log($"   {Winget.Describe(r)} – waiting 60 seconds and trying again ({attempt + 1} of 3)…");
                SetItem(i, "Waiting to retry…", Orange);
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
                        BeginInvokeSafe(() => SetItem(i, "Downloading " + progress, Blue));
                    }
                    return;
                }
                Log("   " + line.Trim());
                if (line.IndexOf("Starting package install", StringComparison.OrdinalIgnoreCase) >= 0)
                    BeginInvokeSafe(() => SetItem(i, "Installing…", Blue));
            }, ct, timeout);
        }

        async Task<bool> WaitForInternetAsync(CancellationToken ct)
        {
            if (await IsOnlineAsync()) return true;
            Log("No internet connection detected.");
            if (!_unattended)
                return Ui.Ask(this, "This PC doesn't seem to be connected to the internet. winget needs internet access to download the apps.\r\n\r\nTry anyway?");
            // Unattended: wait up to 10 minutes for the network to come up (Wi-Fi after first logon is often slow).
            for (int k = 0; k < 40; k++)
            {
                SetStatus("Waiting for an internet connection…");
                try { await Task.Delay(15000, ct); } catch (OperationCanceledException) { return false; }
                if (await IsOnlineAsync()) { Log("Internet connection is up."); return true; }
            }
            Log("Still no internet connection after 10 minutes – installation not started.");
            SetStatus("No internet connection – installation not started.");
            return false;
        }

        static async Task<bool> IsOnlineAsync()
        {
            foreach (var url in new[] { "http://www.msftconnecttest.com/connecttest.txt", Winget.WingetSourceArgument + "/source.msix" })
            {
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Method = "HEAD";
                var work = req.GetResponseAsync();
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

        void ShowManualList()
        {
            var report = Path.Combine(Path.GetDirectoryName(_manifestPath), PackageWriter.ReportName);
            if (!File.Exists(report))
            {
                try
                {
                    report = Path.Combine(Path.GetTempPath(), "OrclWAMP-" + PackageWriter.ReportName);
                    File.WriteAllText(report, PackageWriter.Report(_m), new UTF8Encoding(false));
                }
                catch (Exception ex) { Ui.Error(this, ex.Message); return; }
            }
            Ui.Open(report);
        }

        void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (_running)
            {
                if (e.CloseReason == CloseReason.UserClosing && !Ui.Ask(this, "Apps are still being installed. Stop and close?")) { e.Cancel = true; return; }
                // Cancel and wait for the running installer to be terminated; the install loop closes the window afterwards.
                _closeWhenStopped = true;
                _cts?.Cancel();
                e.Cancel = true;
                SetStatus("Stopping the current installer…");
                return;
            }
            _cts?.Cancel();
            Ui.KeepAwake(false);
        }
    }
}
