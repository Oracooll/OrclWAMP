using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>App settings &amp; bookmarks. Old PC: choose apps. New PC: apply (with backup and Undo).</summary>
    internal sealed class AppConfigsForm : Form
    {
        readonly AppConfigPack _pack;
        readonly string _dir;
        readonly PackageSecrets _secrets;
        readonly bool _protected;
        readonly ListView _lv = Ui.ListView();
        readonly TextBox _log = new TextBox();
        readonly Label _status = Ui.Label("");
        Button _applyBtn, _undoBtn;
        bool _busy;

        public List<string> SelectedIds { get; } = new List<string>();
        public ApplyResult Result { get; private set; }
        /// <summary>New PC: the apps that were applied (for the report).</summary>
        public List<string> AppliedIds { get; } = new List<string>();

        /// <summary>Old PC: choose which apps' settings go into the package.</summary>
        public AppConfigsForm(IEnumerable<string> preselected, bool protectedPackage)
        {
            _protected = protectedPackage;
            Build(T("App settings & bookmarks to take along"), 940, 560);
            _lv.Columns.Add(T("App"));
            _lv.Columns.Add(T("What's included"));
            _lv.Columns.Add(T("Note"));
            Ui.AutoSizeColumns(_lv, 230, 430, 240);
            var pre = preselected == null ? null : new HashSet<string>(preselected);
            var found = AppConfigs.Detect();
            foreach (var d in found)
            {
                bool blocked = d.Sensitive && !_protected;
                var it = new ListViewItem(T(d.Name)) { Tag = d, Checked = !blocked && (pre == null ? !d.Sensitive : pre.Contains(d.Id)) };
                it.SubItems.Add(T(d.Description));
                it.SubItems.Add(blocked ? T("Needs a password-protected package") : T(d.Note));
                if (blocked) it.ForeColor = Theme.Muted;
                _lv.Items.Add(it);
            }
            _lv.ItemCheck += (s, e) =>
            {
                var d = (AppConfigDef)_lv.Items[e.Index].Tag;
                if (d.Sensitive && !_protected && e.NewValue == CheckState.Checked)
                {
                    e.NewValue = CheckState.Unchecked;
                    Ui.Info(this, T("SSH keys are only taken along in a password-protected package. Tick \"Protect with password\" in the main window first."));
                }
            };
            Controls.Add(_lv);
            var bottom = Ui.Flow(DockStyle.Bottom);
            bottom.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button(T("Cancel"), (s, e) => DialogResult = DialogResult.Cancel);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(Ui.Button(T("Use these apps"), (s, e) =>
            {
                SelectedIds.AddRange(_lv.CheckedItems.Cast<ListViewItem>().Select(i => ((AppConfigDef)i.Tag).Id));
                DialogResult = DialogResult.OK;
            }, true));
            bottom.Controls.Add(_status);
            CancelButton = cancel;
            Controls.Add(bottom);
            Controls.Add(Ui.InfoLabel(this, found.Count == 0
                ? T("None of the supported apps were found on this PC.")
                : T("Settings of these apps (and your browser bookmarks) are copied into the package. On the new PC they are put in place after the apps are installed – with a backup first.")));
            Controls.Add(Ui.HeaderBar(T("App settings & bookmarks")));
            Theme.Attach(this, () =>
            {
                foreach (ListViewItem i in _lv.Items) i.ForeColor = ((AppConfigDef)i.Tag).Sensitive && !_protected ? Theme.Muted : Theme.Text;
            });
        }

        /// <summary>New PC: apply.</summary>
        public AppConfigsForm(AppConfigPack pack, string dir, PackageSecrets secrets)
        {
            _pack = pack;
            _dir = dir;
            _secrets = secrets;
            Build(T("Apply app settings & bookmarks"), 1040, 640);
            _lv.Columns.Add(T("App"));
            _lv.Columns.Add(T("Items"));
            _lv.Columns.Add(T("What's included"));
            _lv.Columns.Add(T("Note"));
            Ui.AutoSizeColumns(_lv, 230, 60, 430, 260);
            foreach (var a in pack.Apps)
            {
                var d = AppConfigs.Find(a.Id);
                var it = new ListViewItem(T(d.Name)) { Tag = a, Checked = true };
                it.SubItems.Add((a.Entries.Count + a.Extra.Count).ToString());
                it.SubItems.Add(T(d.Description));
                it.SubItems.Add(d.Sensitive ? T("Asks for the package password") : T(d.Note));
                _lv.Items.Add(it);
            }
            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            split.Panel2.Controls.Add(_log);
            Controls.Add(split);
            var bottom = Ui.Flow(DockStyle.Bottom);
            _applyBtn = Ui.Button(T("Apply ticked"), async (s, e) => await ApplyAsync(), true);
            _undoBtn = Ui.Button(T("Undo last apply"), async (s, e) => await UndoAsync());
            _undoBtn.Enabled = AppConfigs.LatestBackup() != null;
            bottom.Controls.Add(_applyBtn);
            bottom.Controls.Add(_undoBtn);
            bottom.Controls.Add(Ui.Button(T("Close"), (s, e) => Close()));
            bottom.Controls.Add(Ui.LogPaneButton(split));
            bottom.Controls.Add(_status);
            Controls.Add(bottom);
            Controls.Add(Ui.InfoLabel(this, F("App settings and bookmarks from \"{0}\". Install the apps first, then apply. Files that get replaced are backed up – \"Undo last apply\" puts them back.", pack.SourceComputer)));
            Controls.Add(Ui.HeaderBar(T("App settings & bookmarks")));
            Theme.Attach(this);
            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        void Build(string title, int w, int h)
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – " + title, w, h);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
        }

        async Task ApplyAsync()
        {
            if (_busy) return;
            var ids = _lv.CheckedItems.Cast<ListViewItem>().Select(i => ((AppConfigData)i.Tag).Id).ToList();
            if (ids.Count == 0) { Ui.Info(this, T("Nothing is ticked.")); return; }
            var running = AppConfigs.Running(ids);
            if (running.Count > 0 && !Ui.Ask(this, F("Please close these apps first, otherwise they may overwrite the settings when they exit:\r\n\r\n{0}\r\n\r\nContinue anyway?", string.Join("\r\n", running))))
                return;
            // Some configs contain commands that run on this PC (PowerShell profiles, Git hooks/commands, VS Code extensions).
            var code = ids.Where(id => AppConfigs.Find(id).RunsCode).Select(id => T(AppConfigs.Find(id).Name)).ToList();
            if (code.Count > 0 && !Ui.Ask(this, F("These items can run commands on this PC:\r\n\r\n{0}\r\n\r\nOnly apply them if the package was made by you. Continue?", string.Join("\r\n", code))))
                return;
            string secretsRoot = null;
            if (ids.Any(id => AppConfigs.Find(id).Sensitive) && _secrets != null && _secrets.UnlockInteractive(this)) secretsRoot = _secrets.Root;
            SetBusy(true);
            Log("=== " + F("Applying {0} app(s)", ids.Count) + " ===");
            try
            {
                Result = await Task.Run(() => AppConfigs.Apply(_pack, _dir, ids, secretsRoot, Log));
                foreach (var id in ids) if (!AppliedIds.Contains(id)) AppliedIds.Add(id);
            }
            catch (Exception ex) { Log(ex.Message); }
            finally { SetBusy(false); }
            if (Result != null)
            {
                _status.Text = F("{0} applied, {1} skipped.", Result.Applied, Result.Skipped);
                Log("=== " + _status.Text + " ===");
            }
        }

        async Task UndoAsync()
        {
            var file = AppConfigs.LatestBackup();
            if (_busy || file == null || !Ui.Ask(this, T("Put back the app settings as they were before the last \"Apply\"?"))) return;
            SetBusy(true);
            try
            {
                var r = await Task.Run(() => AppConfigs.Undo(file, Log));
                try { File.Move(file, file + ".undone"); } catch { }
                _status.Text = F("{0} restored.", r.Applied);
                Log("=== " + _status.Text + " ===");
            }
            catch (Exception ex) { Log(ex.Message); }
            finally { SetBusy(false); }
        }

        IDisposable _busyToken;
        void SetBusy(bool busy)
        {
            if (busy && _busyToken == null) _busyToken = Ui.Busy();
            else if (!busy) { _busyToken?.Dispose(); _busyToken = null; }
            _busy = busy;
            _applyBtn.Enabled = !busy;
            _undoBtn.Enabled = !busy && AppConfigs.LatestBackup() != null;
            UseWaitCursor = busy;
        }

        void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(line))); } catch (InvalidOperationException) { } return; }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
        }
    }
}
