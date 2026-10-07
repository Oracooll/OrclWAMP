using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OrclWAMP
{
    /// <summary>
    /// Windows settings. Old PC: choose which groups go into the package.
    /// New PC: apply them (with a backup and Undo).
    /// </summary>
    internal sealed class SettingsForm : Form
    {
        readonly SettingsPack _pack;
        readonly string _dir;
        readonly ListView _lv = Ui.ListView();
        readonly TextBox _log = new TextBox();
        readonly Label _status = Ui.Label("");
        Button _applyBtn, _undoBtn;
        bool _busy, _loading = true;

        /// <summary>Old PC: the ticked group ids.</summary>
        public List<string> SelectedIds { get; } = new List<string>();

        /// <summary>Old PC: choose what to take along.</summary>
        public SettingsForm(IEnumerable<string> preselected)
        {
            var pre = new HashSet<string>(preselected);
            Build("Windows settings to take along", 980, 600);
            _lv.Columns.Add("Setting");
            _lv.Columns.Add("What's included");
            _lv.Columns.Add("Found on this PC");
            Ui.AutoSizeColumns(_lv, 230, 560, 130);
            foreach (var d in WinSettings.Catalog)
            {
                var it = new ListViewItem(d.Name) { Tag = d, Checked = pre.Contains(d.Id), ToolTipText = d.Description };
                it.SubItems.Add(d.Description);
                it.SubItems.Add("checking…");
                if (d.Sensitive) it.ForeColor = Theme.Warn;
                _lv.Items.Add(it);
            }
            _lv.ItemCheck += (s, e) =>
            {
                if (_loading) return;
                var d = (SettingDef)_lv.Items[e.Index].Tag;
                if (d.Sensitive && e.NewValue == CheckState.Checked &&
                    !Ui.Ask(this, d.Name + "\r\n\r\nThe Wi-Fi passwords will be saved READABLE in the migration package on the USB stick. " +
                                  "Anyone with the stick can read them.\r\n\r\nInclude them anyway?"))
                    e.NewValue = CheckState.Unchecked;
            };

            var bottom = Ui.Flow(DockStyle.Bottom);
            bottom.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button("Cancel", (s, e) => DialogResult = DialogResult.Cancel);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(Ui.Button("Use these settings", (s, e) =>
            {
                SelectedIds.AddRange(_lv.CheckedItems.Cast<ListViewItem>().Select(i => ((SettingDef)i.Tag).Id));
                DialogResult = DialogResult.OK;
            }, true));
            bottom.Controls.Add(Ui.Button("None", (s, e) => SetAll(false)));
            bottom.Controls.Add(Ui.Button("All (except Wi-Fi)", (s, e) => SetAll(true)));
            bottom.Controls.Add(_status);
            CancelButton = cancel;
            Controls.Add(_lv);
            Controls.Add(bottom);
            AddInfo("These personal Windows settings are copied into the migration package and can be applied on the new PC " +
                    "(OrclWAMP makes a backup there first, so they can be undone). Only per-user settings from this list are ever written.");
            Controls.Add(Ui.HeaderBar("Windows settings"));
            Theme.Attach(this, () => { foreach (ListViewItem i in _lv.Items) if (((SettingDef)i.Tag).Sensitive) i.ForeColor = Theme.Warn; });
            Shown += async (s, e) => { _loading = false; await CountAsync(); };
        }

        /// <summary>New PC: apply the settings from the package.</summary>
        public SettingsForm(SettingsPack pack, string dir)
        {
            _pack = pack;
            _dir = dir;
            Build("Apply Windows settings", 1060, 680);
            _lv.Columns.Add("Setting");
            _lv.Columns.Add("Items");
            _lv.Columns.Add("What's included");
            _lv.Columns.Add("Note");
            Ui.AutoSizeColumns(_lv, 230, 60, 470, 230);
            foreach (var g in pack.Groups)
            {
                var d = WinSettings.Find(g.Id);
                if (d == null) continue;
                var it = new ListViewItem(d.Name) { Tag = g, Checked = true, ToolTipText = d.Description };
                it.SubItems.Add(g.Count.ToString());
                it.SubItems.Add(d.Description);
                it.SubItems.Add(d.ApplyNote.Length > 0 ? d.ApplyNote : d.RestartsExplorer ? "Restarts File Explorer" : "");
                _lv.Items.Add(it);
            }

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            split.Panel2.Controls.Add(_log);
            Controls.Add(split);

            var bottom = Ui.Flow(DockStyle.Bottom);
            _applyBtn = Ui.Button("Apply ticked settings", async (s, e) => await ApplyAsync(), true);
            _undoBtn = Ui.Button("Undo last apply", async (s, e) => await UndoAsync());
            _undoBtn.Enabled = WinSettings.LatestBackup() != null;
            bottom.Controls.Add(_applyBtn);
            bottom.Controls.Add(_undoBtn);
            bottom.Controls.Add(Ui.Button("Close", (s, e) => Close()));
            bottom.Controls.Add(Ui.LogPaneButton(split));
            bottom.Controls.Add(_status);
            Controls.Add(bottom);

            var text = $"Settings from \"{pack.SourceComputer}\" – {pack.SourceOs}, saved {PackageWriter.FormatDate(pack.CreatedUtc)}.  This PC: {WinSettings.OsName()}.";
            bool srcEleven = pack.SourceBuild >= 22000, dstEleven = WinSettings.OsBuild() >= 22000;
            if (pack.SourceBuild > 0 && srcEleven != dstEleven)
                text += "\r\n⚠ The settings come from " + (srcEleven ? "Windows 11" : "Windows 10") + " – a few taskbar/Start options may not exist on this Windows version (they are simply ignored).";
            text += "\r\nA backup of your current settings is made before anything is changed – \"Undo last apply\" puts them back.";
            AddInfo(text);
            Controls.Add(Ui.HeaderBar("Windows settings"));
            Theme.Attach(this);
            _loading = false;
            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        void Build(string title, int w, int h)
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – " + title, w, h);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
        }

        void AddInfo(string text) =>
            Controls.Add(Ui.InfoLabel(this, text));

        void SetAll(bool on)
        {
            _loading = true; // no Wi-Fi question for bulk changes – Wi-Fi is never included by "All"
            foreach (ListViewItem i in _lv.Items) i.Checked = on && !((SettingDef)i.Tag).Sensitive;
            _loading = false;
        }

        async Task CountAsync()
        {
            _status.Text = "Reading settings on this PC…";
            var ids = WinSettings.Catalog.Select(d => d.Id).ToList();
            SettingsPack preview = null;
            try { preview = await Task.Run(() => WinSettings.Capture(ids, null, null)); } catch { }
            if (IsDisposed) return;
            foreach (ListViewItem i in _lv.Items)
            {
                var d = (SettingDef)i.Tag;
                var g = preview?.Groups.FirstOrDefault(x => x.Id == d.Id);
                i.SubItems[2].Text = g == null ? "nothing found" :
                    d.Id == "keyboard" ? $"{g.Values.Count} values + languages" :
                    d.Id == "power" ? "power plan values" :
                    d.Id == "wallpaper" ? (g.Extra.Any() ? "picture + fit" : $"{g.Count} values") :
                    d.Id == "fonts" ? $"{g.Extra.Count} font(s)" :
                    d.Id == "wifi" ? "saved networks" :
                    $"{g.Count} values";
            }
            _status.Text = "";
        }

        async Task ApplyAsync()
        {
            if (_busy) return;
            var ids = _lv.CheckedItems.Cast<ListViewItem>().Select(i => ((SettingsGroupData)i.Tag).Id).ToList();
            if (ids.Count == 0) { Ui.Info(this, "No settings are ticked."); return; }
            SetBusy(true, "Applying settings…");
            Log($"=== Applying {ids.Count} setting group(s) – {DateTime.Now:yyyy-MM-dd HH:mm} ===");
            ApplyResult r = null;
            try { r = await Task.Run(() => WinSettings.Apply(_pack, _dir, ids, Log)); }
            catch (Exception ex) { Log("Failed: " + ex.Message); }
            finally { SetBusy(false, ""); }
            if (r == null) return;
            Log($"=== {r.Applied} applied, {r.Skipped} skipped." + (r.BackupFile != null ? " Backup: " + r.BackupFile : "") + " ===");
            _status.Text = $"{r.Applied} settings applied, {r.Skipped} skipped.";
            _undoBtn.Enabled = r.BackupFile != null;
            await FollowUpAsync(r);
        }

        async Task UndoAsync()
        {
            var file = WinSettings.LatestBackup();
            if (_busy || file == null) return;
            if (!Ui.Ask(this, "Put back the settings as they were before the last \"Apply\"?\r\n\r\n(Fonts and Wi-Fi networks that were added stay installed.)")) return;
            SetBusy(true, "Restoring previous settings…");
            ApplyResult r = null;
            try { r = await Task.Run(() => WinSettings.Undo(file, Log)); }
            catch (Exception ex) { Log("Undo failed: " + ex.Message); }
            finally { SetBusy(false, ""); }
            if (r == null) return;
            try { File.Move(file, file + ".undone"); } catch { }
            _undoBtn.Enabled = WinSettings.LatestBackup() != null;
            Log($"=== Undo: {r.Applied} values restored. ===");
            _status.Text = "Previous settings restored.";
            await FollowUpAsync(r);
        }

        async Task FollowUpAsync(ApplyResult r)
        {
            if (r.RestartExplorer && Ui.Ask(this, "Restart File Explorer now so the taskbar, Start and colour settings show up?\r\n\r\n(Open Explorer windows will close.)"))
            {
                SetBusy(true, "Restarting File Explorer…");
                try { await Task.Run(() => WinSettings.RestartExplorer()); }
                finally { SetBusy(false, ""); }
                Log("File Explorer restarted.");
            }
            if (r.SignOut)
                Ui.Info(this, "Some settings (touchpad, keyboard, languages) take full effect after you sign out and back in – or restart the PC when you're done.");
        }

        void SetBusy(bool busy, string status)
        {
            _busy = busy;
            _applyBtn.Enabled = !busy;
            _undoBtn.Enabled = !busy && WinSettings.LatestBackup() != null;
            UseWaitCursor = busy;
            _status.Text = status;
        }

        void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(line))); } catch (InvalidOperationException) { } return; }
            _log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + line + "\r\n");
        }
    }
}
