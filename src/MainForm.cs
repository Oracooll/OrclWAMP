using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OrclWAMP
{
    /// <summary>Old PC: scan installed apps, choose what to migrate, write the package.</summary>
    internal sealed class MainForm : Form
    {
        readonly List<AppEntry> _all = new List<AppEntry>();
        readonly ListView _lv = Ui.ListView();
        readonly TextBox _filter = new TextBox();
        readonly ComboBox _view = new ComboBox();
        readonly CheckBox _showSystem = Ui.Check("Show system items", false);
        readonly CheckBox _pin = Ui.Check("Install the exact same versions", false);
        readonly CheckBox _admin = Ui.Check("Run as administrator on the new PC (fewer UAC prompts)", false);
        readonly CheckBox _restart = Ui.Check("Restart the new PC when finished", false);
        readonly NumericUpDown _timeout = new NumericUpDown();
        readonly ToolStripStatusLabel _status = new ToolStripStatusLabel { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
        readonly ToolStripStatusLabel _counts = new ToolStripStatusLabel();
        readonly ToolStripProgressBar _progress = new ToolStripProgressBar { Style = ProgressBarStyle.Marquee, Visible = false };
        readonly System.Windows.Forms.Timer _filterTimer = new System.Windows.Forms.Timer { Interval = 250 };
        Button _scanBtn, _addBtn, _createBtn, _cancelBtn;
        CancellationTokenSource _cts;
        int _sortCol;
        bool _sortAsc = true, _populating, _busy, _writing;

        static readonly string[] Views = { "Apps winget can install", "Apps to install manually", "All apps" };
        static readonly Color ManualColor = Color.FromArgb(156, 92, 0);

        public MainForm()
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – Oracooll Winget App Migration Tool", 1220, 780);
            SuspendLayout();

            // ---- list ----
            _lv.Columns.Add("Name");
            _lv.Columns.Add("Package ID");
            _lv.Columns.Add("Version");
            _lv.Columns.Add("Source");
            _lv.Columns.Add("Note");
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
            var caption = new Label { Text = "Migration package:", AutoSize = true, Font = Ui.BoldFont, Anchor = AnchorStyles.Left, Margin = new Padding(Ui.S(3), Ui.S(3), Ui.S(6), Ui.S(3)) };
            opts.Controls.Add(caption);
            opts.Controls.Add(_pin);
            opts.Controls.Add(_admin);
            opts.Controls.Add(_restart);
            opts.Controls.Add(Ui.Label("Timeout per app (min):"));
            _timeout.Minimum = 5; _timeout.Maximum = 240; _timeout.Value = Manifest.DefaultTimeout; _timeout.Width = Ui.S(60); _timeout.Anchor = AnchorStyles.Left;
            opts.Controls.Add(_timeout);
            var tips = new ToolTip();
            tips.SetToolTip(_pin, "Off (recommended): install the newest version.\r\nOn: install the same version as on this PC (falls back to newest if that version is gone).");
            tips.SetToolTip(_admin, "OrclWAMP asks for administrator rights once at the start on the new PC,\r\nso the individual installers don't each show a UAC prompt.\r\n\r\nOff (recommended for most people): apps install for the signed-in user;\r\nmachine-wide installers show their own UAC prompt. A few per-user apps refuse to install as administrator.");
            tips.SetToolTip(_showSystem, "Also list Windows components, drivers and runtimes that have no winget package.");
            _createBtn = Ui.Button("Create migration package…", async (s, e) => await CreatePackageAsync(), true);
            _createBtn.Padding = new Padding(Ui.S(14), Ui.S(8), Ui.S(14), Ui.S(8));
            _createBtn.Anchor = AnchorStyles.Left;
            _createBtn.Margin = new Padding(Ui.S(18), Ui.S(3), Ui.S(3), Ui.S(3));
            opts.Controls.Add(_createBtn);
            Controls.Add(opts);

            // ---- top toolbar ----
            var top = Ui.Flow(DockStyle.Top);
            _scanBtn = Ui.Button("Scan this PC", async (s, e) => await ScanAsync(), true);
            _cancelBtn = Ui.Button("Cancel", (s, e) => _cts?.Cancel());
            _cancelBtn.Visible = false;
            _addBtn = Ui.Button("Add apps from winget…", (s, e) => AddFromWinget());
            top.Controls.Add(_scanBtn);
            top.Controls.Add(_cancelBtn);
            top.Controls.Add(_addBtn);
            top.Controls.Add(Ui.Label("Search:"));
            _filter.Width = Ui.S(160); _filter.Anchor = AnchorStyles.Left;
            _filter.TextChanged += (s, e) => { _filterTimer.Stop(); _filterTimer.Start(); };
            _filterTimer.Tick += (s, e) => { _filterTimer.Stop(); Populate(); };
            top.Controls.Add(_filter);
            top.Controls.Add(Ui.Label("Show:"));
            _view.DropDownStyle = ComboBoxStyle.DropDownList; _view.Items.AddRange(Views); _view.SelectedIndex = 0;
            _view.Width = Ui.S(175); _view.Anchor = AnchorStyles.Left;
            _view.SelectedIndexChanged += (s, e) => Populate();
            top.Controls.Add(_view);
            _showSystem.CheckedChanged += (s, e) => Populate();
            top.Controls.Add(_showSystem);
            top.Controls.Add(Ui.Button("All", (s, e) => SetVisibleChecked(_ => true)));
            top.Controls.Add(Ui.Button("None", (s, e) => SetVisibleChecked(_ => false)));
            top.Controls.Add(Ui.Button("Invert", (s, e) => SetVisibleChecked(a => !a.Selected)));
            Controls.Add(top);

            var header = new Panel { Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(Ui.S(8), Ui.S(6), Ui.S(8), 0) };
            header.Controls.Add(Ui.AppHeader("Winget App Migration Tool"));
            Controls.Add(header);

            Controls.Add(BuildMenu());

            var strip = new StatusStrip();
            strip.Items.Add(_status);
            strip.Items.Add(_progress);
            strip.Items.Add(_counts);
            Controls.Add(strip);

            ResumeLayout(true);
            SetStatus("Ready.");
            UpdateCounts();
            Shown += async (s, e) => await StartupAsync();
            FormClosing += (s, e) =>
            {
                if (_writing) { e.Cancel = true; Ui.Warn(this, "Please wait – the migration package is still being written."); return; }
                _cts?.Cancel();
            };
        }

        MenuStrip BuildMenu()
        {
            var ms = new MenuStrip();
            var file = new ToolStripMenuItem("&File");
            file.DropDownItems.Add(new ToolStripMenuItem("&Scan this PC", null, async (s, e) => await ScanAsync(), Keys.F5));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("&Open app list / profile…", null, (s, e) => LoadProfile(), Keys.Control | Keys.O));
            file.DropDownItems.Add(new ToolStripMenuItem("Save app list as &profile…", null, (s, e) => SaveProfile(), Keys.Control | Keys.S));
            file.DropDownItems.Add(new ToolStripMenuItem("&Export list to CSV…", null, (s, e) => ExportCsv()));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("&Create migration package…", null, async (s, e) => await CreatePackageAsync(), Keys.Control | Keys.P));
            file.DropDownItems.Add(new ToolStripSeparator());
            file.DropDownItems.Add(new ToolStripMenuItem("E&xit", null, (s, e) => Close()));

            var tools = new ToolStripMenuItem("&Tools");
            tools.DropDownItems.Add(new ToolStripMenuItem("&Add apps from winget…", null, (s, e) => AddFromWinget(), Keys.Control | Keys.N));
            tools.DropDownItems.Add(new ToolStripMenuItem("&Install from a migration package (restore mode)…", null, (s, e) => OpenRestore()));
            tools.DropDownItems.Add(new ToolStripSeparator());
            tools.DropDownItems.Add(new ToolStripMenuItem("Check &winget", null, async (s, e) => { if (!_busy) await CheckWingetAsync(true); }));

            var help = new ToolStripMenuItem("&Help");
            help.DropDownItems.Add(new ToolStripMenuItem("&How it works", null, (s, e) => Ui.Info(this, HowTo), Keys.F1));
            help.DropDownItems.Add(new ToolStripMenuItem("Command-line options", null, (s, e) => Ui.Info(this, Options.HelpText)));
            help.DropDownItems.Add(new ToolStripMenuItem("Project page on &GitHub", null, (s, e) => Ui.Open(Program.RepoUrl)));
            help.DropDownItems.Add(new ToolStripMenuItem("&About", null, (s, e) => Ui.Info(this,
                Program.AppName + " " + Program.VersionText + "\r\nOracooll Winget App Migration Tool\r\n\r\n" + Program.RepoUrl + "\r\nMIT License")));

            ms.Items.Add(file);
            ms.Items.Add(tools);
            ms.Items.Add(help);
            MainMenuStrip = ms;
            return ms;
        }

        ContextMenuStrip BuildContextMenu()
        {
            var cm = new ContextMenuStrip();
            cm.Items.Add("Tick selected rows", null, (s, e) => SetRowsChecked(true));
            cm.Items.Add("Untick selected rows", null, (s, e) => SetRowsChecked(false));
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Copy package ID", null, (s, e) =>
            {
                var ids = SelectedEntries().Select(a => a.Id).ToList();
                if (ids.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, ids));
            });
            cm.Items.Add("Show package on winstall.app", null, (s, e) =>
            {
                var a = SelectedEntries().FirstOrDefault(x => x.IsInstallable);
                if (a != null) Ui.Open("https://winstall.app/apps/" + Uri.EscapeDataString(a.Id));
            });
            cm.Items.Add("Search the web for this app", null, (s, e) =>
            {
                var a = SelectedEntries().FirstOrDefault();
                if (a != null) Ui.Open("https://www.bing.com/search?q=" + Uri.EscapeDataString(a.Name + " download"));
            });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Remove from list", null, (s, e) => RemoveSelected());
            return cm;
        }

        const string HowTo =
            "1. On the OLD PC: click \"Scan this PC\". OrclWAMP lists every installed app and checks which ones winget can install.\r\n\r\n" +
            "2. Untick anything you don't want. Apps marked \"to install manually\" have no winget package; they go into a report so you don't forget them.\r\n\r\n" +
            "3. Click \"Create migration package…\" and pick your USB stick. An \"OrclWAMP-Migration\" folder is written there.\r\n\r\n" +
            "4. On the NEW PC: connect to the internet, open the folder and double-click Install.cmd (or OrclWAMP.exe). Check the list and click \"Start installation\".\r\n\r\n" +
            "Tip: use \"Add apps from winget…\" to add apps this PC doesn't have, and File > Save profile to reuse a list.";

        // ------------------------------------------------------------------ startup / scan

        async Task StartupAsync()
        {
            if (await CheckWingetAsync(false)) await ScanAsync();
        }

        async Task<bool> CheckWingetAsync(bool report)
        {
            SetStatus("Looking for winget…");
            Winget.Reset();
            bool ok = await Task.Run(() => Winget.IsAvailable);
            if (!ok)
            {
                SetStatus("winget was not found.");
                if (Ui.Ask(this, "winget (Windows Package Manager) was not found on this PC.\r\n\r\n" +
                                 "It comes with \"App Installer\" from the Microsoft Store. Open the download page now?"))
                    Ui.Open("https://aka.ms/getwinget");
                return false;
            }
            SetStatus("winget " + Winget.Version + " found.");
            if (report) Ui.Info(this, "winget " + Winget.Version + " is installed:\r\n" + Winget.Exe);
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
                SetStatus($"Scan complete: {w} apps can be installed with winget, {m} need a manual install.");
            }
            catch (OperationCanceledException) { SetStatus("Scan cancelled."); }
            catch (Exception ex) { SetStatus("Scan failed."); Ui.Error(this, "The scan failed:\r\n\r\n" + ex.Message); }
            finally { SetBusy(false); }
        }

        void SetBusy(bool busy)
        {
            _busy = busy;
            _progress.Visible = busy;
            _scanBtn.Enabled = _addBtn.Enabled = _createBtn.Enabled = !busy;
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
            it.SubItems.Add(a.Note);
            if (a.Category == AppCategory.NotAvailable) it.ForeColor = ManualColor;
            else if (a.Category == AppCategory.System) it.ForeColor = SystemColors.GrayText;
            it.ToolTipText = a.IsInstallable
                ? "Ticked = install on the new PC"
                : "No winget package. Ticked = add to the manual-install report";
            return it;
        }

        void UpdateCounts()
        {
            int inst = _all.Count(a => a.IsInstallable), instSel = _all.Count(a => a.IsInstallable && a.Selected);
            int man = _all.Count(a => !a.IsInstallable && a.Selected);
            _counts.Text = $"{instSel} of {inst} winget apps selected  ·  {man} on manual list  ·  {_lv.Items.Count} shown";
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
            if (!Winget.IsAvailable) { Ui.Warn(this, "winget is not available on this PC."); return; }
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
                SetStatus($"Added {added} app(s).");
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
                    .Select(a => new ManualApp { Name = a.Name, Version = a.Version, Id = a.Id })
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
                Ui.Warn(this, "Nothing is selected. Scan the PC (or open a profile) and tick the apps to migrate first.");
                return;
            }
            string root;
            using (var dlg = new FolderBrowserDialog
            {
                Description = "Choose your USB flash drive (or any folder).\r\nAn \"" + PackageWriter.FolderName + "\" folder will be created there.",
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
                !Ui.Ask(this, "A migration package already exists in\r\n" + dir + "\r\n\r\nReplace it?"))
                return;

            SetBusy(true);
            _writing = true;
            SetStatus("Writing migration package…");
            try
            {
                try { await Task.Run(() => PackageWriter.Write(dir, m, Application.ExecutablePath)); }
                finally { _writing = false; }
                SetStatus("Migration package written to " + dir);
                if (Ui.Ask(this, $"Migration package created:\r\n{dir}\r\n\r\n" +
                                 $"• {m.Packages.Count} apps will be installed automatically\r\n" +
                                 $"• {m.ManualApps.Count} apps are on the manual-install list\r\n\r\n" +
                                 "On the new PC, open this folder and double-click Install.cmd.\r\n\r\nOpen the folder now?"))
                    Ui.Open(dir);
            }
            catch (Exception ex)
            {
                SetStatus("Could not write the package.");
                Ui.Error(this, "Could not write the migration package:\r\n\r\n" + ex.Message);
            }
            finally { SetBusy(false); }
        }

        void SaveProfile()
        {
            var m = CurrentManifest();
            if (m.Packages.Count == 0 && m.ManualApps.Count == 0) { Ui.Warn(this, "Nothing is selected."); return; }
            using (var dlg = new SaveFileDialog { Filter = "OrclWAMP app list (*.json)|*.json", FileName = Environment.MachineName + "-apps.json" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { m.Save(dlg.FileName); SetStatus("Saved " + dlg.FileName); }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        void LoadProfile()
        {
            if (_busy) return;
            using (var dlg = new OpenFileDialog { Filter = "App lists (*.json)|*.json|All files|*.*", Title = "Open an OrclWAMP app list or a winget export file" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Manifest m;
                try { m = Manifest.Load(dlg.FileName); }
                catch (Exception ex) { Ui.Error(this, "Could not read the file:\r\n\r\n" + ex.Message); return; }

                if (_all.Count > 0)
                {
                    var r = MessageBox.Show(this, "Replace the current list?\r\n\r\nYes = replace,  No = add to the current list", Program.AppName,
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
                    _all.Add(new AppEntry { Name = a.Name, Id = a.Id ?? "", Version = a.Version ?? "", Category = AppCategory.NotAvailable, Selected = true, ManuallyAdded = true, Note = "From " + Path.GetFileName(dlg.FileName) });
                }
                if (m.Tool == "OrclWAMP")
                {
                    _pin.Checked = m.PinVersions; _admin.Checked = m.RequestAdmin; _restart.Checked = m.RestartWhenDone;
                    _timeout.Value = Math.Max(_timeout.Minimum, Math.Min(_timeout.Maximum, m.TimeoutMinutes));
                }
                Populate();
                SetStatus($"Loaded {m.Packages.Count} packages from {Path.GetFileName(dlg.FileName)}.");
            }
        }

        void ExportCsv()
        {
            if (_all.Count == 0) { Ui.Warn(this, "The list is empty – scan first."); return; }
            using (var dlg = new SaveFileDialog { Filter = "CSV file (*.csv)|*.csv", FileName = Environment.MachineName + "-apps.csv" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try { PackageWriter.WriteCsv(dlg.FileName, _all.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)); SetStatus("Exported " + dlg.FileName); }
                catch (Exception ex) { Ui.Error(this, ex.Message); }
            }
        }

        void OpenRestore()
        {
            using (var dlg = new OpenFileDialog { Filter = "OrclWAMP package (OrclWAMP-packages.json)|*.json|All files|*.*", Title = "Open a migration package" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                Ui.Launch(Application.ExecutablePath, "/restore " + Winget.Quote(dlg.FileName));
            }
        }
    }
}
