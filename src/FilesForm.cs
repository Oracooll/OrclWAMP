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
    /// <summary>Personal files. Old PC: choose folders for the package. New PC: copy them back.</summary>
    internal sealed class FilesForm : Form
    {
        sealed class Row
        {
            public FolderData Folder;
            public string Target;        // new PC
            public bool OneDrive;
            public ListViewItem Lvi;
        }

        readonly FilesPack _pack;
        readonly string _filesDir;
        readonly List<Row> _rows = new List<Row>();
        readonly ListView _lv = Ui.ListView();
        readonly TextBox _log = new TextBox();
        readonly Label _status = Ui.Label("");
        readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Bottom };
        readonly ComboBox _mode = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        Button _goBtn, _stopBtn;
        CancellationTokenSource _cts;
        bool _busy;

        /// <summary>Old PC: the chosen folders (with measured sizes).</summary>
        public List<FolderData> Selected { get; } = new List<FolderData>();
        /// <summary>New PC: what was copied (for the migration report).</summary>
        public CopyResult Result { get; private set; }

        /// <summary>Old PC.</summary>
        public FilesForm(IEnumerable<FolderData> preselected)
        {
            Build(T("Personal files to take along"));
            _lv.Columns.Add(T("Folder"));
            _lv.Columns.Add(T("Location"));
            _lv.Columns.Add(T("Size"));
            _lv.Columns.Add(T("Files"));
            _lv.Columns.Add(T("Note"));
            Ui.AutoSizeColumns(_lv, 150, 380, 90, 80, 300);
            var pre = preselected?.ToList();
            foreach (var k in PersonalFiles.Known)
            {
                var path = PersonalFiles.KnownPath(k.Key);
                if (path == null || !Directory.Exists(path)) continue;
                bool od = PersonalFiles.IsOneDrive(path);
                bool on = pre != null ? pre.Any(p => p.Key == k.Key) : !od && k.Key != "Downloads";
                AddRow(new FolderData { Key = k.Key, Name = k.Name, SourcePath = path }, on, od);
            }
            if (pre != null) foreach (var c in pre.Where(p => p.Key == "Custom")) AddRow(c, true, PersonalFiles.IsOneDrive(c.SourcePath));
            _lv.ItemChecked += (s, e) => UpdateTotal();
            Controls.Add(_lv);

            var bottom = Ui.Flow(DockStyle.Bottom);
            bottom.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button(T("Cancel"), (s, e) => { _cts?.Cancel(); DialogResult = DialogResult.Cancel; });
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(Ui.Button(T("Use these folders"), (s, e) => Accept(), true));
            bottom.Controls.Add(Ui.Button(T("Remove folder"), (s, e) => RemoveCustom()));
            bottom.Controls.Add(Ui.Button(T("Add folder…"), (s, e) => AddCustom()));
            bottom.Controls.Add(_status);
            CancelButton = cancel;
            Controls.Add(bottom);
            Controls.Add(Ui.InfoLabel(this, T("These folders are copied into the migration package (make sure the USB drive is big enough) and put back on the new PC. " +
                                              "Folders kept in OneDrive are not ticked – they come back by themselves when you sign in to OneDrive.")));
            Controls.Add(Ui.HeaderBar(T("Personal files")));
            Theme.Attach(this, Recolor);
            Shown += async (s, e) => await MeasureAsync();
            FormClosing += (s, e) => _cts?.Cancel();
        }

        /// <summary>New PC.</summary>
        public FilesForm(FilesPack pack, string filesDir)
        {
            _pack = pack;
            _filesDir = filesDir;
            Build(T("Copy personal files to this PC"));
            _lv.Columns.Add(T("Folder"));
            _lv.Columns.Add(T("Size"));
            _lv.Columns.Add(T("Files"));
            _lv.Columns.Add(T("Copy to"));
            _lv.Columns.Add(T("Status"));
            Ui.AutoSizeColumns(_lv, 160, 90, 80, 380, 260);
            foreach (var f in pack.Folders)
            {
                var r = new Row { Folder = f, Target = PersonalFiles.DefaultTarget(f, pack.SourceComputer) };
                r.Lvi = new ListViewItem(f.DisplayName) { Tag = r, Checked = true, ToolTipText = T("Was:") + " " + f.SourcePath };
                r.Lvi.SubItems.Add(PersonalFiles.Size(f.Bytes));
                r.Lvi.SubItems.Add(f.FileCount.ToString());
                r.Lvi.SubItems.Add(r.Target);
                r.Lvi.SubItems.Add("");
                _rows.Add(r);
                _lv.Items.Add(r.Lvi);
            }
            _lv.ItemActivate += (s, e) => ChangeTarget();
            _lv.ItemCheck += (s, e) => { if (_busy) e.NewValue = e.CurrentValue; };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            split.Panel2.Controls.Add(_log);
            Controls.Add(split);

            var bottom = Ui.Flow(DockStyle.Bottom);
            _goBtn = Ui.Button(T("Copy ticked folders"), async (s, e) => await CopyAsync(), true);
            _stopBtn = Ui.Button(T("Stop"), (s, e) => _cts?.Cancel());
            _stopBtn.Enabled = false;
            bottom.Controls.Add(_goBtn);
            bottom.Controls.Add(_stopBtn);
            bottom.Controls.Add(Ui.Button(T("Change destination…"), (s, e) => ChangeTarget()));
            bottom.Controls.Add(Ui.Label(T("If a file already exists:")));
            _mode.Items.AddRange(new object[] { T("Keep both (rename the copy)"), T("Skip it"), T("Replace it") });
            _mode.SelectedIndex = 0;
            _mode.Width = Ui.S(280);
            _mode.Anchor = AnchorStyles.Left;
            bottom.Controls.Add(_mode);
            bottom.Controls.Add(Ui.LogPaneButton(split));
            bottom.Controls.Add(_status);
            Controls.Add(bottom);
            _bar.Height = Ui.S(14);
            Controls.Add(_bar);
            Controls.Add(Ui.InfoLabel(this, F("Personal files from \"{0}\" ({1} in total). Identical files are skipped automatically. Double-click a folder to change where it goes.",
                                              pack.SourceComputer, PersonalFiles.Size(pack.Folders.Sum(f => f.Bytes)))));
            Controls.Add(Ui.HeaderBar(T("Personal files")));
            Theme.Attach(this);
            FormClosing += (s, e) =>
            {
                if (!_busy) return;
                if (!Ui.Ask(this, T("Files are still being copied. Stop and close?"))) { e.Cancel = true; return; }
                _cts?.Cancel();
            };
        }

        void Build(string title)
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – " + title, 1060, 620);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
        }

        // ---------------------------------------------------------------- old PC

        void AddRow(FolderData f, bool on, bool oneDrive)
        {
            var r = new Row { Folder = f, OneDrive = oneDrive };
            r.Lvi = new ListViewItem(f.DisplayName) { Tag = r, Checked = on };
            r.Lvi.SubItems.Add(f.SourcePath);
            r.Lvi.SubItems.Add(f.Bytes > 0 ? PersonalFiles.Size(f.Bytes) : T("measuring…"));
            r.Lvi.SubItems.Add(f.FileCount > 0 ? f.FileCount.ToString() : "");
            r.Lvi.SubItems.Add(oneDrive ? T("Kept in OneDrive – comes back by itself") : "");
            if (oneDrive) r.Lvi.ForeColor = Theme.Muted;
            _rows.Add(r);
            _lv.Items.Add(r.Lvi);
        }

        void Recolor() { foreach (var r in _rows) r.Lvi.ForeColor = r.OneDrive ? Theme.Muted : Theme.Text; }

        async Task MeasureAsync()
        {
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _status.Text = T("Measuring folders…");
            foreach (var r in _rows.ToList())
            {
                if (ct.IsCancellationRequested || IsDisposed) return;
                if (r.Folder.Bytes > 0) continue;
                try
                {
                    var (bytes, files) = await Task.Run(() => PersonalFiles.Measure(r.Folder.SourcePath, ct));
                    if (IsDisposed) return;
                    r.Folder.Bytes = bytes; r.Folder.FileCount = files;
                    r.Lvi.SubItems[2].Text = PersonalFiles.Size(bytes);
                    r.Lvi.SubItems[3].Text = files.ToString();
                }
                catch (OperationCanceledException) { return; }
                UpdateTotal();
            }
            if (!IsDisposed) UpdateTotal();
        }

        void UpdateTotal()
        {
            var sel = _rows.Where(r => r.Lvi.Checked).ToList();
            _status.Text = F("{0} folder(s), {1} selected", sel.Count, PersonalFiles.Size(sel.Sum(r => r.Folder.Bytes)));
        }

        void AddCustom()
        {
            using (var dlg = new FolderBrowserDialog { Description = T("Choose a folder to take along"), ShowNewFolderButton = false })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                var path = dlg.SelectedPath;
                if (_rows.Any(r => string.Equals(r.Folder.SourcePath, path, StringComparison.OrdinalIgnoreCase))) return;
                if (Path.GetPathRoot(path) == path) { Ui.Warn(this, T("Please choose a folder, not a whole drive.")); return; }
                AddRow(new FolderData { Key = "Custom", Name = Path.GetFileName(path.TrimEnd('\\')), SourcePath = path }, true, PersonalFiles.IsOneDrive(path));
                var r = _rows.Last();
                Task.Run(() => PersonalFiles.Measure(path, CancellationToken.None)).ContinueWith(t =>
                {
                    if (IsDisposed || t.IsFaulted) return;
                    r.Folder.Bytes = t.Result.Bytes; r.Folder.FileCount = t.Result.Files;
                    r.Lvi.SubItems[2].Text = PersonalFiles.Size(t.Result.Bytes);
                    r.Lvi.SubItems[3].Text = t.Result.Files.ToString();
                    UpdateTotal();
                }, TaskScheduler.FromCurrentSynchronizationContext());
            }
        }

        void RemoveCustom()
        {
            foreach (var r in _lv.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag).Where(r => r.Folder.Key == "Custom").ToList())
            {
                _rows.Remove(r);
                _lv.Items.Remove(r.Lvi);
            }
            UpdateTotal();
        }

        void Accept()
        {
            _cts?.Cancel();
            // Nested folders would be copied twice – keep only the outer one.
            var chosen = _rows.Where(r => r.Lvi.Checked).Select(r => r.Folder).ToList();
            foreach (var f in chosen)
                if (!chosen.Any(o => o != f && f.SourcePath.StartsWith(o.SourcePath.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)))
                    Selected.Add(f);
            DialogResult = DialogResult.OK;
        }

        // ---------------------------------------------------------------- new PC

        void ChangeTarget()
        {
            if (_busy) return;
            var r = _lv.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag).FirstOrDefault();
            if (r == null) return;
            using (var dlg = new FolderBrowserDialog { Description = F("Where should \"{0}\" go?", r.Folder.DisplayName), SelectedPath = Directory.Exists(r.Target) ? r.Target : "" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                r.Target = dlg.SelectedPath;
                r.Lvi.SubItems[3].Text = r.Target;
            }
        }

        async Task CopyAsync()
        {
            if (_busy) return;
            var rows = _rows.Where(r => r.Lvi.Checked).ToList();
            if (rows.Count == 0) { Ui.Info(this, T("No folders are ticked.")); return; }
            long need = rows.Sum(r => r.Folder.Bytes);
            foreach (var g in rows.GroupBy(r => Path.GetPathRoot(Path.GetFullPath(r.Target)), StringComparer.OrdinalIgnoreCase))
                if (PersonalFiles.FreeSpace(g.Key) < g.Sum(r => r.Folder.Bytes))
                    if (!Ui.Ask(this, F("Drive {0} may not have enough free space for these files. Copy anyway?", g.Key))) return;

            var mode = (ExistingFile)_mode.SelectedIndex;
            await RunCopyAsync(rows, mode, need);
        }

        async Task<CopyResult> RunCopyAsync(List<Row> rows, ExistingFile mode, long need)
        {
            using var busy = Ui.Busy();
            var total = new CopyResult();
            Result = total; // filled in as folders finish – also valid if the window is closed mid-copy
            _busy = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _goBtn.Enabled = false;
            _stopBtn.Enabled = true;
            _mode.Enabled = false;
            Ui.KeepAwake(true);
            long doneBefore = 0;
            try
            {
                foreach (var r in rows)
                {
                    if (ct.IsCancellationRequested) break;
                    SetStatus(r, T("Copying…"));
                    Log(F("{0} -> {1}", r.Folder.DisplayName, r.Target));
                    long start = doneBefore;
                    var src = Path.Combine(_filesDir, r.Folder.SubDir);
                    var res = await Task.Run(() => PersonalFiles.Copy(src, r.Target, mode, F("from {0}", PersonalFiles.SafeName(_pack.SourceComputer)), 0,
                        (b, file) => Progress(start + b, need, file), l => Log(l), ct, IsDocuments(r.Folder.Key, r.Target) ? PersonalFiles.ScriptFolders : null));
                    doneBefore += res.Bytes;
                    total.Copied += res.Copied; total.Skipped += res.Skipped; total.Renamed += res.Renamed; total.Failed += res.Failed; total.Bytes += res.Bytes;
                    SetStatus(r, F("{0} copied, {1} skipped, {2} failed", res.Copied, res.Skipped, res.Failed));
                    Log("   " + r.Lvi.SubItems[4].Text);
                }
            }
            catch (OperationCanceledException) { Log(T("Stopped.")); }
            catch (Exception ex) { Log(ex.Message); }
            finally
            {
                Ui.KeepAwake(false);
                _busy = false;
                if (!IsDisposed)
                {
                    _goBtn.Enabled = true;
                    _stopBtn.Enabled = false;
                    _mode.Enabled = true;
                    _status.Text = F("{0} files copied ({1}), {2} skipped, {3} renamed, {4} failed.", total.Copied, PersonalFiles.Size(total.Bytes), total.Skipped, total.Renamed, total.Failed);
                    Log("=== " + _status.Text + " ===");
                }
            }
            return total;
        }

        long _lastProgress;
        void Progress(long done, long total, string file)
        {
            if (IsDisposed) return;
            var now = Environment.TickCount;
            if (now - _lastProgress < 100 && done < total) return;   // at most ~10 updates per second
            _lastProgress = now;
            try { BeginInvoke(new Action(() =>
            {
                if (IsDisposed) return;
                _bar.Maximum = 1000;
                _bar.Value = total > 0 ? (int)Math.Min(1000, done * 1000 / total) : 0;
                _status.Text = F("{0} of {1} – {2}", PersonalFiles.Size(done), PersonalFiles.Size(total), file);
            })); } catch (InvalidOperationException) { }
        }

        void SetStatus(Row r, string text) { if (!IsDisposed) r.Lvi.SubItems[4].Text = text; }

        void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(line))); } catch (InvalidOperationException) { } return; }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
        }

        /// <summary>The Documents folder – whether it came as "Documents" or as a custom folder pointing there.</summary>
        static bool IsDocuments(string key, string target)
        {
            if (key == "Documents") return true;
            try { return string.Equals(Path.GetFullPath(target).TrimEnd('\\'), (PersonalFiles.KnownPath("Documents") ?? "").TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Unattended restore: copy everything to the default places, keeping both copies on conflicts.</summary>
        public static CopyResult CopyAllUnattended(FilesPack pack, string filesDir, Action<string> log, CancellationToken ct)
        {
            var total = new CopyResult();
            foreach (var f in pack.Folders)
            {
                // Nobody reviews the destinations in unattended mode, so custom folders always go to "From <PC>".
                var target = PersonalFiles.DefaultTarget(f, pack.SourceComputer, allowSamePath: false);
                ct.ThrowIfCancellationRequested();
                log?.Invoke(F("{0} -> {1}", f.DisplayName, target));
                CopyResult r;
                try { r = PersonalFiles.Copy(Path.Combine(filesDir, f.SubDir), target, ExistingFile.KeepBoth, F("from {0}", PersonalFiles.SafeName(pack.SourceComputer)), 0, null, log, ct, IsDocuments(f.Key, target) ? PersonalFiles.ScriptFolders : null); }
                catch (OperationCanceledException) { log?.Invoke(T("Stopped.")); break; } // keep what was counted so far for the report
                catch (Exception ex) { log?.Invoke(F("   could not copy {0}: {1}", f.DisplayName, ex.Message)); total.Failed++; continue; }
                total.Copied += r.Copied; total.Skipped += r.Skipped; total.Renamed += r.Renamed; total.Failed += r.Failed; total.Bytes += r.Bytes;
            }
            return total;
        }
    }
}
