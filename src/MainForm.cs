using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>Old PC: scan installed apps, choose what to migrate, write the package.</summary>
    internal sealed class MainForm : Form
    {
        readonly List<AppEntry> _all = new List<AppEntry>();
        readonly ListView _lv = Ui.ListView();
        readonly TextBox _filter = new TextBox();
        readonly ComboBox _view = new ComboBox();
        readonly CheckBox _showSystem = Ui.Check(T("Show system items"), false);
        readonly CheckBox _pin = Ui.Check(T("Install the exact same versions"), false);
        readonly CheckBox _admin = Ui.Check(T("Run as administrator on the new PC (fewer UAC prompts)"), false);
        readonly CheckBox _restart = Ui.Check(T("Restart the new PC when finished"), false);
        readonly NumericUpDown _timeout = new NumericUpDown();
        readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel _counts = new ToolStripStatusLabel();
        readonly ToolStripProgressBar _progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false };
        readonly System.Windows.Forms.Timer _filterTimer = new System.Windows.Forms.Timer { Interval = 250 };
        Button _scanBtn, _addBtn, _createBtn, _cancelBtn, _findBtn, _manualBtn, _updatesBtn;
        CancellationTokenSource _cts;
        int _sortCol;
        bool _sortAsc = true, _populating, _busy, _writing;
        readonly CheckBox _settingsChk = Ui.Check(T("Windows settings"), true);
        readonly CheckBox _appsChk = Ui.Check(T("App settings & bookmarks"), true);
        readonly CheckBox _filesChk = Ui.Check(T("Personal files"), false);
        readonly CheckBox _protectChk = Ui.Check(T("Protect with password"), false);
        List<string> _settingsIds = WinSettings.DefaultIds.ToList();
        List<string> _appIds;              // null = everything found except SSH keys
        List<FolderData> _folders;         // null = not chosen yet
        string _password;
        bool _loadingOptions;

        static readonly string[] Views = { T("Apps winget can install"), T("Apps to install manually"), T("All apps") };

        public MainForm()
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – Oracooll Winget App Migration Program", 1220, 780);
            SuspendLayout();

            // ---- list ----
            _lv.Columns.Add(T("Name"));
            _lv.Columns.Add(T("Package ID"));
            _lv.Columns.Add(T("Version"));
            _lv.Columns.Add(T("Source"));
            _lv.Columns.Add(T("Note"));
            Ui.AutoSizeColumns(_lv, 300, 290, 120, 70, 280);
            _lv.ItemChecked += (s, e) =>
            {
                if (_populating || !(e.Item.Tag is AppEntry a)) return;
                a.Selected = e.Item.Checked;
                UpdateCounts();
            };
            _lv.ColumnClick += (s, e) =>
            {
                if (_sortCol == e.Column) _sortAsc = !_sortAsc; else { _sortCol = e.Column; _sortAsc = true; }
                Populate();
            };
            _lv.ContextMenuStrip = BuildContextMenu();
            _lv.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelected(); };
            Controls.Add(_lv);

            // ---- bottom: package options ----
            var opts = Ui.Flow(DockStyle.Bottom);
            opts.Padding = new Padding(Ui.S(6), Ui.S(4), Ui.S(6), Ui.S(6));
            var caption = new Label { Text = T("Migration package:"), AutoSize = true, Font = Ui.BoldFont, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(6), Ui.S(3)) };
            opts.Controls.Add(caption);
            opts.Controls.Add(_pin);
            opts.Controls.Add(_admin);
            opts.Controls.Add(_restart);
            opts.Controls.Add(Ui.Label(T("Timeout per app (min):")));
            _timeout.Minimum = 5; _timeout.Maximum = 240; _timeout.Value = Manifest.DefaultTimeout; _timeout.Width = Ui.S(60); _timeout.Anchor = AnchorStyles.Left;
            opts.Controls.Add(_timeout);
            var tips = new ToolTip();
            tips.SetToolTip(_pin, T("Off (recommended): install the newest version.\r\nOn: install the same version as on this PC (falls back to newest if that version is gone)."));
            tips.SetToolTip(_admin, T("OrclWAMP asks for administrator rights once at the start on the new PC,\r\nso the individual installers don't each show a UAC prompt.\r\n\r\nOff (recommended for most people): apps install for the signed-in user;\r\nmachine-wide installers show their own UAC prompt. A few per-user apps refuse to install as administrator."));
            tips.SetToolTip(_showSystem, T("Also list Windows components, drivers and runtimes that have no winget package."));
            opts.SetFlowBreak(_timeout, true);
            opts.Controls.Add(new Label { Text = T("Also take along:"), AutoSize = true, Font = Ui.BoldFont, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(6), Ui.S(3)) });
            opts.Controls.Add(_settingsChk);
            opts.Controls.Add(Ui.Button(T("Choose…"), (s, e) => ChooseSettings()));
            tips.SetToolTip(_settingsChk, T("Personal Windows settings: touchpad gestures, mouse, keyboard & languages,\r\ntaskbar & Explorer options, colours, wallpaper, regional formats, power, fonts."));
            opts.Controls.Add(_appsChk);
            opts.Controls.Add(Ui.Button(T("Choose…"), (s, e) => ChooseApps()));
            tips.SetToolTip(_appsChk, T("Settings of apps like Windows Terminal, Notepad++, VS Code, Git, and your browser bookmarks."));
            opts.Controls.Add(_filesChk);
            opts.Controls.Add(Ui.Button(T("Choose…"), (s, e) => ChooseFiles()));
            _filesChk.CheckedChanged += (s, e) => { if (_filesChk.Checked && _folders == null && !_loadingOptions) ChooseFiles(); };
            tips.SetToolTip(_filesChk, T("Copies Desktop, Documents, Pictures… into the package (needs enough space on the USB drive)."));
            opts.Controls.Add(_protectChk);
            _protectChk.CheckedChanged += (s, e) =>
            {
                if (_loadingOptions) return;
                if (_protectChk.Checked && _password == null)
                {
                    _password = PasswordDialog.Ask(this, T("Choose a password for the sensitive parts of the package (Wi-Fi passwords, SSH keys). You need it again on the new PC – OrclWAMP can't recover it.") + "\r\n\r\n" + T("Tip: use a long passphrase of several words – anyone who finds the USB stick could try to guess it."), true);
                    if (_password == null) { _loadingOptions = true; _protectChk.Checked = false; _loadingOptions = false; }
                }
                if (!_protectChk.Checked) _password = null;
            };
            tips.SetToolTip(_protectChk, T("Encrypts Wi-Fi passwords and SSH keys in the package with a password (AES-256)."));
            _createBtn = Ui.Button(T("Create migration package…"), async (s, e) => await CreatePackageAsync(), true);
            _createBtn.Padding = new Padding(Ui.S(14), Ui.S(8), Ui.S(14), Ui.S(8));
            _createBtn.Anchor = AnchorStyles.Left;
            _createBtn.Margin = new Padding(Ui.S(18), Ui.S(3), Ui.S(3), Ui.S(3));
            opts.Controls.Add(_createBtn);
            Controls.Add(opts);

            // ---- filter row (second toolbar row) ----
            var top = Ui.Flow(DockStyle.Top);
            top.Padding = new Padding(Ui.S(4), 0, Ui.S(4), Ui.S(4));
            top.Controls.Add(Ui.Label(T("Search:")));
            _filter.Width = Ui.S(160); _filter.Anchor = AnchorStyles.Left;
            _filter.TextChanged += (s, e) => { _filterTimer.Stop(); _filterTimer.Start(); };
            _filterTimer.Tick += (s, e) => { _filterTimer.Stop(); Populate(); };
            top.Controls.Add(_filter);
            top.Controls.Add(Ui.Label(T("Show:")));
            _view.DropDownStyle = ComboBoxStyle.DropDownList; _view.Items.AddRange(Views); _view.SelectedIndex = 0;
            _view.Width = Ui.S(Lang.IsBg ? 290 : 190); _view.DropDownWidth = Ui.S(320); _view.Anchor = AnchorStyles.Left;
            _view.SelectedIndexChanged += (s, e) => Populate();
            top.Controls.Add(_view);
            _showSystem.CheckedChanged += (s, e) => Populate();
            top.Controls.Add(_showSystem);
            top.Controls.Add(Ui.Button(T("All"), (s, e) => SetVisibleChecked(_ => true)));
            top.Controls.Add(Ui.Button(T("None"), (s, e) => SetVisibleChecked(_ => false)));
            top.Controls.Add(Ui.Button(T("Invert"), (s, e) => SetVisibleChecked(a => !a.Selected)));
            Controls.Add(top);

            // ---- action row ----
            var actions = Ui.Flow(DockStyle.Top);
            _scanBtn = Ui.Button(T("Scan this PC"), async (s, e) => await ScanAsync(), true);
            _cancelBtn = Ui.Button(T("Cancel"), (s, e) => _cts?.Cancel());
            _cancelBtn.Visible = false;
            _addBtn = Ui.Button(T("Add apps from winget…"), (s, e) => AddFromWinget());
            _findBtn = Ui.Button(T("Find winget packages for manual apps…"), async (s, e) => await FindMatchesAsync());
            _manualBtn = Ui.Button(T("Manual apps && downloads…"), (s, e) => OpenManualApps());
            tips.SetToolTip(_findBtn, T("Searches winget by name for the apps it couldn't link to a package\r\n(e.g. Store versions). You confirm every match."));
            tips.SetToolTip(_manualBtn, T("Download links for apps winget can't install – download them one by one or all at once."));
            actions.Controls.Add(_scanBtn);
            actions.Controls.Add(_cancelBtn);
            actions.Controls.Add(_addBtn);
            actions.Controls.Add(_findBtn);
            actions.Controls.Add(_manualBtn);
            _updatesBtn = Ui.Button(T("App updates…"), (s, e) => OpenUpgrades());
            tips.SetToolTip(_updatesBtn, T("Shows apps on this PC that have a newer version and updates the ticked ones."));
            actions.Controls.Add(_updatesBtn);
            Controls.Add(actions);

            var header = Ui.HeaderBar(T("Winget App Migration Program"));
            header.Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), 0);
            Controls.Add(header);

            Controls.Add(BuildMenu());

            var strip = new StatusStrip();
            strip.Items.Add(_status);
            strip.Items.Add(_progress);
            strip.Items.Add(_counts);
            Controls.Add(strip);

            ResumeLayout(true);
            Theme.Attach(this, Populate);
            SetStatus(T("Ready."));
            UpdateCounts();
            Shown += async (s, e) => await StartupAsync();
            FormClosing += (s, e) =>
            {
                if (_writing) { e.Cancel = true; Ui.Warn(this, T("Please wait – the migration package is still being written.")); return; }
                _cts?.Cancel();
            };
        }

        MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();
            var file = new ToolStripMenuItem(T("&File"));
            file.DropDownItems.Add(new ToolStripMenuItem(T("&Scan this PC"), null, async (s, e) => await ScanAsync(), Keys.F5));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem(T("&Open app list / profile…"), null, (s, e) => LoadProfile(), Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem(T("Save app list as &profile…"), null, (s, e) => SaveProfile(), Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripMenuItem(T("&Export list to CSV…"), null, (s, e) => ExportCsv()));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem(T("&Create migration package…"), null, async (s, e) => await CreatePackageAsync(), Keys.Control | Keys.P));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem(T("E&xit"), null, (s, e) => Close()));

            var tools = new ToolStripMenuItem(T("&Tools"));
            tools.DropDownItems.Add(new ToolStripMenuItem(T("&Add apps from winget…"), null, (s, e) => AddFromWinget(), Keys.Control | Keys.N));
            tools.DropDownItems.Add(new ToolStripMenuItem(T("&Find winget packages for manual apps…"), null, async (s, e) => await FindMatchesAsync()));
            tools.DropDownItems.Add(new ToolStripMenuItem(T("&Manual apps && downloads…"), null, (s, e) => OpenManualApps(), Keys.Control | Keys.D));
            tools.DropDownItems.Add(new ToolStripMenuItem(T("&Install from a migration package (restore mode)…"), null, (s, e) => OpenRestore()));
            tools.DropDownItems.Add(new ToolStripMenuItem(T("App &updates…"), null, (s, e) => OpenUpgrades(), Keys.Control | Keys.U));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem(T("Check &winget"), null, async (s, e) => { if (!_busy) await CheckWingetAsync(true); }));

            var help = new ToolStripMenuItem(T("&Help"));
            help.DropDownItems.Add(new ToolStripMenuItem(T("&How it works"), null, (s, e) => Ui.Info(this, HowTo), Keys.F1));
            help.DropDownItems.Add(new ToolStripMenuItem(T("Command-line options"), null, (s, e) => Ui.Info(this, Options.HelpText)));
            help.DropDownItems.Add(new ToolStripMenuItem(T("Project page on &GitHub"), null, (s, e) => Ui.Open(Program.RepoUrl)));
            help.DropDownItems.Add(new ToolStripMenuItem(T("&About"), null, (s, e) => Ui.Info(this,
                Program.AppName + " " + Program.VersionText + "\r\nOracooll Winget App Migration Program\r\n\r\n" + Program.RepoUrl + "\r\nMIT License")));

            ms.Items.Add(file);
            ms.Items.Add(tools);
            ms.Items.Add(help);
            MainMenuStrip = ms;
            return ms;
        }

        ContextMenuStrip BuildContextMenu()
        {
            var cm = new ContextMenuStrip();
            cm.Items.Add(T("Tick selected rows"), null, (s, e) => SetRowsChecked(true));
            cm.Items.Add(T("Untick selected rows"), null, (s, e) => SetRowsChecked(false));
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(T("Copy package ID"), null, (s, e) =>
            {
                var ids = SelectedEntries().Select(a => a.Id).ToList();
                if (ids.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, ids));
            });
            cm.Items.Add(T("Show package on winstall.app"), null, (s, e) =>
            {
                var a = SelectedEntries().FirstOrDefault(x => x.IsInstallable);
                if (a != null) Ui.Open("https://winstall.app/apps/" + Uri.EscapeDataString(a.Id));
            });
            cm.Items.Add(T("Search the web for this app"), null, (s, e) =>
            {
                var a = SelectedEntries().FirstOrDefault();
                if (a != null) Ui.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(a.Name + " download"));
            });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add(T("Remove from list"), null, (s, e) => RemoveSelected());
            return cm;
        }

        static string HowTo =>
            T("1. On the OLD PC: click \"Scan this PC\". OrclWAMP lists every installed app and checks which ones winget can install.") + "\r\n\r\n" +
            T("2. Untick anything you don't want. Apps marked \"to install manually\" have no winget package; they go into a report so you don't forget them.") + "\r\n\r\n" +
            T("3. Click \"Create migration package…\" and pick your USB stick. An \"OrclWAMP-Migration\" folder is written there.") + "\r\n\r\n" +
            T("4. On the NEW PC: connect to the internet, open the folder and double-click Install.cmd (or OrclWAMP.exe). Check the list and click \"Start installation\".") + "\r\n\r\n" +
            T("Tip: use \"Add apps from winget…\" to add apps this PC doesn't have, and File > Save profile to reuse a list.");

        // ------------------------------------------------------------------ startup / scan

        async Task StartupAsync()
        {
            if (await CheckWingetAsync(false)) await ScanAsync();
        }

        async Task<bool> CheckWingetAsync(bool report)
        {
            SetStatus(T("Looking for winget…"));
            Winget.Reset();
            bool ok = await Task.Run(() => Winget.IsAvailable);
            if (!ok)
            {
                SetStatus(T("winget was not found."));
                if (Ui.Ask(this, T("winget (Windows Package Manager) was not found on this PC.") + "\r\n\r\n" +
                                 T("It comes with \"App Installer\" from the Microsoft Store. Open the download page now?")))
                    Ui.Open("https://aka.ms/getwinget");
                return false;
            }
            SetStatus(F("winget {0} found.", Winget.Version));
            if (report) Ui.Info(this, F("winget {0} is installed:", Winget.Version) + "\r\n" + Winget.Exe);
            return true;
        }

        async Task ScanAsync()
        {
            if (_busy) return;
            if (!Winget.IsAvailable && !await CheckWingetAsync(false)) return;
            SetBusy(true);
            _cts = new CancellationTokenSource();
            try
            {
                var entries = await Scanner.ScanAsync(s => SetStatus(s), _cts.Token);
                var keep = _all.Where(a => a.ManuallyAdded && !entries.Any(n => n.Id.Equals(a.Id, StringComparison.OrdinalIgnoreCase))).ToList();
                _all.Clear();
                _all.AddRange(entries);
                _all.AddRange(keep);
                Populate();
                int w = _all.Count(a => a.IsInstallable), m = _all.Count(a => a.Category == AppCategory.NotAvailable);
                SetStatus(F("Scan complete: {0} apps can be installed with winget, {1} need a manual install.", w, m) +
                          (m > 0 ? "  " + T("Tip: \"Find winget packages for manual apps\" often finds more.") : ""));
            }
            catch (OperationCanceledException) { SetStatus(T("Scan cancelled.")); }
            catch (Exception ex) { SetStatus(T("Scan failed.")); Ui.Error(this, T("The scan failed:") + "\r\n\r\n" + ex.Message); }
            finally { SetBusy(false); }
        }

        IDisposable _busyToken;
        void SetBusy(bool busy)
        {
            if (busy && _busyToken == null) _busyToken = Ui.Busy();
            else if (!busy) { _busyToken?.Dispose(); _busyToken = null; }
            _busy = busy;
            _progress.Visible = busy;
            _scanBtn.Enabled = _addBtn.Enabled = _createBtn.Enabled = _findBtn.Enabled = _manualBtn.Enabled = _updatesBtn.Enabled = !busy;
            _cancelBtn.Visible = busy;
            UseWaitCursor = busy;
            if (busy) _lv.Cursor = Cursors.WaitCursor; else _lv.Cursor = Cursors.Default;
        }

        void SetStatus(string text)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text))); return; }
            _status.Text = text;
        }

        // ------------------------------------------------------------------ list

        IEnumerable<AppEntry> Filtered()
        {
            var q = _filter.Text.Trim();
            foreach (var a in _all)
            {
                if (a.Category == AppCategory.System && !_showSystem.Checked) continue;
                switch (_view.SelectedIndex)
                {
                    case 0: if (!a.IsInstallable) continue; break;
                    case 1: if (a.IsInstallable) continue; break;
                }
                if (q.Length > 0 &&
                    a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) < 0 &&
                    a.Id.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                yield return a;
            }
        }

        static string Key(AppEntry a, int col)
        {
            switch (col)
            {
                case 1: return a.Id;
                case 2: return a.Version;
                case 3: return a.Source;
                case 4: return a.Note;
                default: return a.Name;
            }
        }

        void Populate()
        {
            var items = Filtered().ToList();
            var cmp = StringComparer.CurrentCultureIgnoreCase;
            items.Sort((x, y) =>
            {
                int r = cmp.Compare(Key(x, _sortCol), Key(y, _sortCol));
                if (r == 0) r = cmp.Compare(x.Name, y.Name);
                return _sortAsc ? r : -r;
            });

            _populating = true;
            _lv.BeginUpdate();
            try
            {
                _lv.Items.Clear();
                _lv.Items.AddRange(items.Select(MakeItem).ToArray());
            }
            finally
            {
                _lv.EndUpdate();
                _populating = false;
            }
            UpdateCounts();
        }

        static ListViewItem MakeItem(AppEntry a)
        {
            var it = new ListViewItem(a.Name) { Tag = a, Checked = a.Selected };
            it.SubItems.Add(a.Id);
            it.SubItems.Add(a.Version);
            it.SubItems.Add(a.Source.Length > 0 ? a.Source : "–");
            it.SubItems.Add(NoteText(a.Note));
            if (a.Category == AppCategory.NotAvailable) it.ForeColor = Theme.Manual;
            else if (a.Category == AppCategory.System) it.ForeColor = Theme.Muted;
            it.ToolTipText = a.IsInstallable
                ? T("Ticked = install on the new PC")
                : T("No winget package. Ticked = add to the manual-install report");
            return it;
        }

        /// <summary>Notes are stored in English (they go into the package); translate them for display only.</summary>
        static string NoteText(string note) => Lang.Note(note);

        void UpdateCounts()
        {
            int inst = _all.Count(a => a.IsInstallable), instSel = _all.Count(a => a.IsInstallable && a.Selected);
            int man = _all.Count(a => !a.IsInstallable && a.Selected);
            _counts.Text = F("{0} of {1} winget apps selected  ·  {2} on manual list  ·  {3} shown", instSel, inst, man, _lv.Items.Count);
        }

        void SetVisibleChecked(Func<AppEntry, bool> f)
        {
            _populating = true;
            _lv.BeginUpdate();
            foreach (ListViewItem it in _lv.Items)
            {
                var a = (AppEntry)it.Tag;
                a.Selected = f(a);
                it.Checked = a.Selected;
            }
            _lv.EndUpdate();
            _populating = false;
            UpdateCounts();
        }

        void SetRowsChecked(bool value)
        {
            foreach (ListViewItem it in _lv.SelectedItems) it.Checked = value; // ItemChecked updates the entry
        }

        List<AppEntry> SelectedEntries() => _lv.SelectedItems.Cast<ListViewItem>().Select(i => (AppEntry)i.Tag).ToList();

        void RemoveSelected()
        {
            var sel = SelectedEntries();
            if (sel.Count == 0) return;
            foreach (var a in sel) _all.Remove(a);
            Populate();
        }

        void AddFromWinget()
        {
            if (!Winget.IsAvailable) { Ui.Warn(this, T("winget is not available on this PC.")); return; }
            using (var f = new SearchForm())
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                int added = 0;
                foreach (var e in f.Result)
                {
                    var existing = _all.FirstOrDefault(a => a.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { existing.Selected = true; continue; }
                    e.Selected = true;
                    e.ManuallyAdded = true;
                    e.Note = "Added from winget search";
                    _all.Add(e);
                    added++;
                }
                if (_view.SelectedIndex == 1) _view.SelectedIndex = 0; else Populate();
                SetStatus(F("Added {0} app(s).", added));
            }
        }

        // ------------------------------------------------------------------ files

        internal static Manifest BuildManifest(IEnumerable<AppEntry> entries, bool pin, bool admin, bool restart, int timeout)
        {
            var list = entries.ToList();
            return new Manifest
            {
                CreatedUtc = DateTime.UtcNow.ToString("o"),
                SourceComputer = Environment.MachineName,
                PinVersions = pin,
                RequestAdmin = admin,
                RestartWhenDone = restart,
                TimeoutMinutes = timeout,
                Packages = list.Where(a => a.IsInstallable && a.Selected)
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(a => new PackageRef { Id = a.Id, Name = a.Name, Version = a.Version, Source = a.Source.Length > 0 ? a.Source : "winget" })
                    .ToList(),
                ManualApps = list.Where(a => !a.IsInstallable && a.Selected)
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(a => a.ToManual())
                    .ToList()
            };
        }

        Manifest CurrentManifest() => BuildManifest(_all, _pin.Checked, _admin.Checked, _restart.Checked, (int)_timeout.Value);

        async Task CreatePackageAsync()
        {
            if (_busy) return;
            var m = CurrentManifest();
            if (m.Packages.Count == 0 && m.ManualApps.Count == 0)
            {
                Ui.Warn(this, T("Nothing is selected. Scan the PC (or open a profile) and tick the apps to migrate first."));
                return;
            }
            string root;
            using (var dlg = new FolderBrowserDialog
            {
                Description = F("Choose your USB flash drive (or any folder).\r\nAn \"{0}\" folder will be created there.", PackageWriter.FolderName),
                ShowNewFolderButton = true
            })
            {
                var usb = DriveInfo.GetDrives().FirstOrDefault(d => d.DriveType == DriveType.Removable && d.IsReady);
                if (usb != null) dlg.SelectedPath = usb.RootDirectory.FullName;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                root = dlg.SelectedPath;
            }

            var dir = string.Equals(Path.GetFileName(root.TrimEnd('\\')), PackageWriter.FolderName, StringComparison.OrdinalIgnoreCase)
                ? root : Path.Combine(root, PackageWriter.FolderName);
            if (File.Exists(Path.Combine(dir, Program.ManifestFileName)) &&
                !Ui.Ask(this, F("A migration package already exists in\r\n{0}\r\n\r\nReplace it?", dir)))
                return;

            var opt = new PackageWriter.Options
            {
                SettingsIds = _settingsChk.Checked && _settingsIds.Count > 0 ? _settingsIds : null,
                AppConfigIds = _appsChk.Checked ? (_appIds ?? AppConfigs.Detect().Where(d => !d.Sensitive).Select(d => d.Id).ToList()) : null,
                Folders = _filesChk.Checked && _folders != null && _folders.Count > 0 ? _folders : null,
                Password = _protectChk.Checked ? _password : null
            };
            if (opt.Folders != null)
            {
                // The package must not be written into a folder that is being copied (it would copy itself).
                var inside = opt.Folders.FirstOrDefault(f => (Path.GetFullPath(dir).TrimEnd('\\') + "\\").StartsWith(Path.GetFullPath(f.SourcePath).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
                if (inside != null)
                {
                    Ui.Warn(this, F("The package folder is inside \"{0}\", which is being copied. Save the package somewhere else (e.g. a USB drive).", inside.DisplayName));
                    return;
                }
                long need = opt.Folders.Sum(f => f.Bytes) + 50L * 1024 * 1024;
                if (PersonalFiles.FreeSpace(root) < need &&
                    !Ui.Ask(this, F("The personal files need about {0}, but the drive only has {1} free.\r\n\r\nWrite the package anyway?",
                                    PersonalFiles.Size(need), PersonalFiles.Size(PersonalFiles.FreeSpace(root)))))
                    return;
                if (PersonalFiles.SizeLimitFor(root) > 0 && opt.Folders.Any(f => f.Bytes > 0))
                    Ui.Info(this, T("This drive uses FAT32, which can't store files of 4 GB or larger – such files will be skipped and listed in the log."));
            }

            SetBusy(true);
            _writing = true;
            _cts = new CancellationTokenSource();
            opt.Cancel = _cts.Token;
            var writeLog = new List<string>();
            opt.Log = l => { lock (writeLog) writeLog.Add(l); SetStatus(l.Trim()); };
            opt.FileProgress = (done, total, file) => SetStatus(F("Copying personal files: {0} of {1} – {2}", PersonalFiles.Size(done), PersonalFiles.Size(total), file));
            SetStatus(T("Writing migration package…"));
            try
            {
                try { await Task.Run(() => PackageWriter.Write(dir, m, Application.ExecutablePath, opt)); }
                finally { _writing = false; }
                try { File.WriteAllLines(Path.Combine(dir, "OrclWAMP-package.log"), writeLog); } catch { }
                SetStatus(T("Migration package written to") + " " + dir);
                var summary = F("Migration package created:\r\n{0}", dir) + "\r\n\r\n" +
                              F("• {0} apps will be installed automatically", m.Packages.Count) + "\r\n" +
                              F("• {0} apps are on the manual-install list", m.ManualApps.Count) + "\r\n";
                if (opt.SettingsIds != null) summary += F("• {0} groups of Windows settings", opt.SettingsIds.Count) + "\r\n";
                if (opt.AppConfigIds != null) summary += F("• settings of {0} app(s) / browsers", opt.AppConfigIds.Count) + "\r\n";
                if (opt.Folders != null) summary += F("• {0} personal folder(s), {1}", opt.Folders.Count, PersonalFiles.Size(opt.Folders.Sum(f => f.Bytes))) + "\r\n";
                if (opt.Password != null) summary += T("• sensitive items protected with your password") + "\r\n";
                summary += "\r\n" + T("On the new PC, open this folder and double-click Install.cmd.") + "\r\n\r\n" + T("Open the folder now?");
                if (Ui.Ask(this, summary)) Ui.Open(dir);
            }
            catch (OperationCanceledException)
            {
                SetStatus(T("Cancelled – the package is incomplete."));
                Ui.Warn(this, T("Writing the package was cancelled. The folder is incomplete – create the package again before using it."));
            }
            catch (Exception ex)
            {
                SetStatus(T("Could not write the package."));
                Ui.Error(this, T("Could not write the migration package:") + "\r\n\r\n" + ex.Message);
            }
            finally { SetBusy(false); }
        }

        void SaveProfile()
        {
            var m = CurrentManifest();
            if (m.Packages.Count == 0 && m.ManualApps.Count == 0) { Ui.Warn(this, T("Nothing is selected.")); return; }
            using (var dlg = new SaveFileDialog { Filter = T("OrclWAMP app list (*.json)|*.json"), FileName = Environment.MachineName + "-apps.json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { m.Save(dlg.FileName); SetStatus(F("Saved {0}", dlg.FileName)); }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        void LoadProfile()
        {
            if (_busy) return;
            using (var dlg = new OpenFileDialog { Filter = T("App lists (*.json)|*.json|All files|*.*"), Title = T("Open an OrclWAMP app list or a winget export file") })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Manifest m;
                try { m = Manifest.Load(dlg.FileName); }
                catch (Exception ex) { Ui.Error(this, T("Could not read the file:") + "\r\n\r\n" + ex.Message); return; }

                if (_all.Count > 0)
                {
                    var r = MessageBox.Show(this, T("Replace the current list?\r\n\r\nYes = replace,  No = add to the current list"), Program.AppName,
                        MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
                    if (r == DialogResult.Cancel) return;
                    if (r == DialogResult.Yes) _all.Clear();
                }
                foreach (var p in m.Packages)
                {
                    var existing = _all.FirstOrDefault(a => a.Id.Equals(p.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { existing.Selected = true; continue; }
                    _all.Add(new AppEntry
                    {
                        Name = p.Name, Id = p.Id, Version = p.Version, Source = p.Source, Selected = true, ManuallyAdded = true,
                        Category = p.Source.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? AppCategory.Store : AppCategory.Winget,
                        Note = "From " + Path.GetFileName(dlg.FileName)
                    });
                }
                foreach (var a in m.ManualApps)
                {
                    if (_all.Any(x => !x.IsInstallable && x.Name.Equals(a.Name, StringComparison.CurrentCultureIgnoreCase))) continue;
                    _all.Add(new AppEntry
                    {
                        Name = a.Name, Id = a.Id, Version = a.Version, Category = AppCategory.NotAvailable, Selected = true, ManuallyAdded = true,
                        Publisher = a.Publisher, Homepage = a.Homepage, DownloadUrl = a.DownloadUrl,
                        Note = a.Note.Length > 0 ? a.Note : "From " + Path.GetFileName(dlg.FileName)
                    });
                }
                if (m.Tool == "OrclWAMP")
                {
                    _pin.Checked = m.PinVersions; _admin.Checked = m.RequestAdmin; _restart.Checked = m.RestartWhenDone;
                    _timeout.Value = Math.Max(_timeout.Minimum, Math.Min(_timeout.Maximum, m.TimeoutMinutes));
                }
                Populate();
                SetStatus(F("Loaded {0} packages from {1}.", m.Packages.Count, Path.GetFileName(dlg.FileName)));
            }
        }

        void ExportCsv()
        {
            if (_all.Count == 0) { Ui.Warn(this, T("The list is empty – scan first.")); return; }
            using (var dlg = new SaveFileDialog { Filter = T("CSV file (*.csv)|*.csv"), FileName = Environment.MachineName + "-apps.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { PackageWriter.WriteCsv(dlg.FileName, _all.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)); SetStatus(F("Exported {0}", dlg.FileName)); }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        // ------------------------------------------------------------------ manual apps

        async Task FindMatchesAsync()
        {
            if (_busy) return;
            var manual = _all.Where(a => a.Category == AppCategory.NotAvailable).ToList();
            if (manual.Count == 0) { Ui.Info(this, T("There are no manual-install apps in the list. Scan the PC first.")); return; }
            if (!Winget.IsAvailable) { Ui.Warn(this, T("winget is not available on this PC.")); return; }
            SetBusy(true);
            _cts = new CancellationTokenSource();
            List<Scanner.WingetMatch> matches;
            try
            {
                matches = await Scanner.FindWingetMatchesAsync(manual, (i, n, name) => SetStatus(F("Searching winget {0} of {1}: {2}", i, n, name)), _cts.Token);
            }
            catch (OperationCanceledException) { SetStatus(T("Search cancelled.")); return; }
            finally { SetBusy(false); }

            if (matches.Count == 0) { SetStatus(T("No winget packages found for the manual apps.")); Ui.Info(this, T("winget has no packages with the same names as the manual-install apps.")); return; }
            using (var f = new MatchesForm(matches))
            {
                if (f.ShowDialog(this) != DialogResult.OK || f.Result.Count == 0) { SetStatus(T("No matches used.")); return; }
                foreach (var m in f.Result)
                {
                    var e = m.Entry;
                    var src = m.Row.Source.Length > 0 ? m.Row.Source : "winget";
                    // Same package already in the list (e.g. a Store copy of an app also installed as desktop app): merge.
                    var existing = _all.FirstOrDefault(a => a != e && a.IsInstallable && a.Id.Equals(m.Row.Id, StringComparison.OrdinalIgnoreCase));
                    if (existing != null) { existing.Selected = true; _all.Remove(e); continue; }
                    e.Id = m.Row.Id;
                    e.Version = m.Row.Version; // the old desktop version usually doesn't exist in winget (matters with pinned versions)
                    e.Source = src;
                    e.Category = src.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? AppCategory.Store : AppCategory.Winget;
                    e.Selected = true;
                    e.Note = "Found by name in winget";
                    e.Homepage = e.DownloadUrl = "";
                }
                Populate();
                SetStatus(F("{0} app(s) will now be installed with winget.", f.Result.Count));
            }
        }

        void OpenManualApps()
        {
            if (_busy) return;
            var entries = _all.Where(a => a.Category == AppCategory.NotAvailable).ToList();
            if (entries.Count == 0) { Ui.Info(this, T("There are no manual-install apps in the list. Scan the PC first.")); return; }
            var map = entries.ToDictionary(e => e.ToManual());
            using (var f = new ManualAppsForm(map.Keys.ToList(), () =>
            {
                foreach (var kv in map) kv.Value.DownloadUrl = kv.Key.DownloadUrl; // keep the user's links for the package
            }, () => OpenChecklist(map.Keys.ToList())))
                f.ShowDialog(this);
        }

        static void OpenChecklist(List<ManualApp> apps)
        {
            try
            {
                var path = Path.Combine(Path.GetTempPath(), "OrclWAMP-" + PackageWriter.ReportName);
                var m = new Manifest { CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = Environment.MachineName, ManualApps = apps };
                File.WriteAllText(path, PackageWriter.Report(m), new System.Text.UTF8Encoding(false));
                Ui.Open(path);
            }
            catch (Exception ex) { Ui.Warn(null, ex.Message); }
        }

        void ChooseApps()
        {
            using (var f = new AppConfigsForm(_appIds, _protectChk.Checked))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _appIds = f.SelectedIds;
                _appsChk.Checked = _appIds.Count > 0;
                SetStatus(F("Settings of {0} app(s) will be included.", _appIds.Count));
            }
        }

        void ChooseFiles()
        {
            using (var f = new FilesForm(_folders))
            {
                if (f.ShowDialog(this) == DialogResult.OK) _folders = f.Selected;
                _loadingOptions = true;
                _filesChk.Checked = _folders != null && _folders.Count > 0;
                _loadingOptions = false;
                if (_folders != null) SetStatus(F("{0} personal folder(s), {1}, will be included.", _folders.Count, PersonalFiles.Size(_folders.Sum(x => x.Bytes))));
            }
        }

        void OpenUpgrades()
        {
            if (_busy) return;
            if (!Winget.IsAvailable) { Ui.Warn(this, T("winget is not available on this PC.")); return; }
            using (var f = new UpgradeForm()) f.ShowDialog(this);
        }

        void ChooseSettings()
        {
            using (var f = new SettingsForm(_settingsIds, _protectChk.Checked))
            {
                if (f.ShowDialog(this) != DialogResult.OK) return;
                _settingsIds = f.SelectedIds;
                _settingsChk.Checked = _settingsIds.Count > 0;
                SetStatus(F("{0} group(s) of Windows settings will be included.", _settingsIds.Count));
            }
        }

        void OpenRestore()
        {
            using (var dlg = new OpenFileDialog { Filter = T("OrclWAMP package (OrclWAMP-packages.json)|*.json|All files|*.*"), Title = T("Open a migration package") })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Ui.Launch(Application.ExecutablePath, "/restore " + Winget.Quote(dlg.FileName));
            }
        }
    }
}
