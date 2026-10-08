using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

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

        readonly PackageSecrets _secrets;

        /// <summary>New PC: the result of the last Apply (for the migration report).</summary>
        public ApplyResult LastResult { get; private set; }
        public List<string> AppliedIds { get; } = new List<string>();

        /// <summary>Old PC: the ticked group ids.</summary>
        public List<string> SelectedIds { get; } = new List<string>();

        /// <summary>Old PC: choose what to take along.</summary>
        public SettingsForm(IEnumerable<string> preselected, bool protectedPackage = false)
        {
            var pre = new HashSet<string>(preselected);
            Build(T("Windows settings to take along"), 980, 600);
            _lv.Columns.Add(T("Setting"));
            _lv.Columns.Add(T("What's included"));
            _lv.Columns.Add(T("Found on this PC"));
            Ui.AutoSizeColumns(_lv, 230, 560, 130);
            foreach (var d in WinSettings.Catalog)
            {
                var it = new ListViewItem(T(d.Name)) { Tag = d, Checked = pre.Contains(d.Id), ToolTipText = T(d.Description) };
                it.SubItems.Add(T(d.Description));
                it.SubItems.Add(T("checking…"));
                if (d.Sensitive) it.ForeColor = Theme.Warn;
                _lv.Items.Add(it);
            }
            _lv.ItemCheck += (s, e) =>
            {
                if (_loading) return;
                var d = (SettingDef)_lv.Items[e.Index].Tag;
                // Wi-Fi passwords only travel encrypted – like SSH keys.
                if (d.Sensitive && e.NewValue == CheckState.Checked && !protectedPackage)
                {
                    e.NewValue = CheckState.Unchecked;
                    Ui.Info(this, T("Wi-Fi networks (with their passwords) are only taken along in a password-protected package. Tick \"Protect with password\" in the main window first."));
                }
            };

            var bottom = Ui.Flow(DockStyle.Bottom);
            bottom.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button(T("Cancel"), (s, e) => DialogResult = DialogResult.Cancel);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(Ui.Button(T("Use these settings"), (s, e) =>
            {
                SelectedIds.AddRange(_lv.CheckedItems.Cast<ListViewItem>().Select(i => ((SettingDef)i.Tag).Id));
                DialogResult = DialogResult.OK;
            }, true));
            bottom.Controls.Add(Ui.Button(T("None"), (s, e) => SetAll(false)));
            bottom.Controls.Add(Ui.Button(T("All (except Wi-Fi)"), (s, e) => SetAll(true)));
            bottom.Controls.Add(_status);
            CancelButton = cancel;
            Controls.Add(_lv);
            Controls.Add(bottom);
            AddInfo(T("These personal Windows settings are copied into the migration package and can be applied on the new PC (OrclWAMP makes a backup there first, so they can be undone). Only per-user settings from this list are ever written."));
            Controls.Add(Ui.HeaderBar(T("Windows settings")));
            Theme.Attach(this, () => { foreach (ListViewItem i in _lv.Items) if (((SettingDef)i.Tag).Sensitive) i.ForeColor = Theme.Warn; });
            Shown += async (s, e) => { _loading = false; await CountAsync(); };
        }

        /// <summary>New PC: apply the settings from the package.</summary>
        public SettingsForm(SettingsPack pack, string dir, PackageSecrets secrets = null)
        {
            _secrets = secrets;
            _pack = pack;
            _dir = dir;
            Build(T("Apply Windows settings"), 1060, 680);
            _lv.Columns.Add(T("Setting"));
            _lv.Columns.Add(T("Items"));
            _lv.Columns.Add(T("What's included"));
            _lv.Columns.Add(T("Note"));
            Ui.AutoSizeColumns(_lv, 230, 60, 470, 230);
            foreach (var g in pack.Groups)
            {
                var d = WinSettings.Find(g.Id);
                if (d == null) continue;
                var it = new ListViewItem(T(d.Name)) { Tag = g, Checked = true, ToolTipText = T(d.Description) };
                it.SubItems.Add(g.Count.ToString());
                it.SubItems.Add(T(d.Description));
                it.SubItems.Add(d.ApplyNote.Length > 0 ? T(d.ApplyNote) : d.RestartsExplorer ? T("Restarts File Explorer") : "");
                _lv.Items.Add(it);
            }

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            split.Panel2.Controls.Add(_log);
            Controls.Add(split);

            var bottom = Ui.Flow(DockStyle.Bottom);
            _applyBtn = Ui.Button(T("Apply ticked settings"), async (s, e) => await ApplyAsync(), true);
            _undoBtn = Ui.Button(T("Undo last apply"), async (s, e) => await UndoAsync());
            _undoBtn.Enabled = WinSettings.LatestBackup() != null;
            bottom.Controls.Add(_applyBtn);
            bottom.Controls.Add(_undoBtn);
            bottom.Controls.Add(Ui.Button(T("Close"), (s, e) => Close()));
            bottom.Controls.Add(Ui.LogPaneButton(split));
            bottom.Controls.Add(_status);
            Controls.Add(bottom);

            var text = F("Settings from \"{0}\" – {1}, saved {2}.  This PC: {3}.", pack.SourceComputer, pack.SourceOs, PackageWriter.FormatDate(pack.CreatedUtc), WinSettings.OsName());
            bool srcEleven = pack.SourceBuild >= 22000, dstEleven = WinSettings.OsBuild() >= 22000;
            if (pack.SourceBuild > 0 && srcEleven != dstEleven)
                text += "\r\n⚠ " + F("The settings come from {0} – a few taskbar/Start options may not exist on this Windows version (they are simply ignored).", srcEleven ? "Windows 11" : "Windows 10");
            text += "\r\n" + T("A backup of your current settings is made before anything is changed – \"Undo last apply\" puts them back.");
            AddInfo(text);
            Controls.Add(Ui.HeaderBar(T("Windows settings")));
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
            _status.Text = T("Reading settings on this PC…");
            var ids = WinSettings.Catalog.Select(d => d.Id).ToList();
            SettingsPack preview = null;
            try { preview = await Task.Run(() => WinSettings.Capture(ids, null, null)); } catch { }
            if (IsDisposed) return;
            foreach (ListViewItem i in _lv.Items)
            {
                var d = (SettingDef)i.Tag;
                var g = preview?.Groups.FirstOrDefault(x => x.Id == d.Id);
                i.SubItems[2].Text = g == null ? T("nothing found") :
                    d.Id == "keyboard" ? F("{0} values + languages", g.Values.Count) :
                    d.Id == "power" ? T("power plan values") :
                    d.Id == "wallpaper" ? (g.Extra.Any() ? T("picture + fit") : F("{0} values", g.Count)) :
                    d.Id == "fonts" ? F("{0} font(s)", g.Extra.Count) :
                    d.Id == "wifi" ? T("saved networks") :
                    F("{0} values", g.Count);
            }
            _status.Text = "";
        }

        async Task ApplyAsync()
        {
            if (_busy) return;
            var ids = _lv.CheckedItems.Cast<ListViewItem>().Select(i => ((SettingsGroupData)i.Tag).Id).ToList();
            if (ids.Count == 0) { Ui.Info(this, T("No settings are ticked.")); return; }
            if (ids.Contains("wifi") && WinSettings.WifiNeedsPassword(_pack, _dir))
            {
                if (_secrets != null && _secrets.UnlockInteractive(this)) WinSettings.SecretsRoot = _secrets.Root;
                else { ids.Remove("wifi"); Log(T("Wi-Fi networks skipped – the package password was not entered.")); }
            }
            SetBusy(true, T("Applying settings…"));
            Log("=== " + F("Applying {0} setting group(s) – {1}", ids.Count, DateTime.Now.ToString("yyyy-MM-dd HH:mm")) + " ===");
            ApplyResult r = null;
            try { r = await Task.Run(() => WinSettings.Apply(_pack, _dir, ids, Log)); }
            catch (Exception ex) { Log(F("Failed: {0}", ex.Message)); }
            finally { SetBusy(false, ""); }
            if (r == null) return;
            LastResult = r;
            foreach (var id in ids) if (!AppliedIds.Contains(id)) AppliedIds.Add(id);
            Log("=== " + F("{0} applied, {1} skipped.", r.Applied, r.Skipped) + (r.BackupFile != null ? " " + F("Backup: {0}", r.BackupFile) : "") + " ===");
            _status.Text = F("{0} settings applied, {1} skipped.", r.Applied, r.Skipped);
            _undoBtn.Enabled = r.BackupFile != null;
            await FollowUpAsync(r);
        }

        async Task UndoAsync()
        {
            var file = WinSettings.LatestBackup();
            if (_busy || file == null) return;
            if (!Ui.Ask(this, T("Put back the settings as they were before the last \"Apply\"?\r\n\r\n(Fonts and Wi-Fi networks that were added stay installed.)"))) return;
            SetBusy(true, T("Restoring previous settings…"));
            ApplyResult r = null;
            try { r = await Task.Run(() => WinSettings.Undo(file, Log)); }
            catch (Exception ex) { Log(F("Undo failed: {0}", ex.Message)); }
            finally { SetBusy(false, ""); }
            if (r == null) return;
            try { File.Move(file, file + ".undone"); } catch { }
            _undoBtn.Enabled = WinSettings.LatestBackup() != null;
            Log("=== " + F("Undo: {0} values restored.", r.Applied) + " ===");
            _status.Text = T("Previous settings restored.");
            await FollowUpAsync(r);
        }

        async Task FollowUpAsync(ApplyResult r)
        {
            if (r.RestartExplorer && Ui.Ask(this, T("Restart File Explorer now so the taskbar, Start and colour settings show up?\r\n\r\n(Open Explorer windows will close.)")))
            {
                SetBusy(true, T("Restarting File Explorer…"));
                try { await Task.Run(() => WinSettings.RestartExplorer()); }
                finally { SetBusy(false, ""); }
                Log(T("File Explorer restarted."));
            }
            if (r.SignOut)
                Ui.Info(this, T("Some settings (touchpad, keyboard, languages) take full effect after you sign out and back in – or restart the PC when you're done."));
        }

        IDisposable _busyToken;
        void SetBusy(bool busy, string status)
        {
            if (busy && _busyToken == null) _busyToken = Ui.Busy();
            else if (!busy) { _busyToken?.Dispose(); _busyToken = null; }
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
