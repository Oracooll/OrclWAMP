using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>Upgrade mode: list apps with a newer version in winget and update the ticked ones.</summary>
    internal sealed class UpgradeForm : Form
    {
        sealed class Item
        {
            public TableParser.Row Row;
            public ListViewItem Lvi;
            public InstallOutcome? Outcome;
            public Func<Color> Color = () => Theme.Text;
        }

        readonly List<Item> _items = new List<Item>();
        readonly ListView _lv = Ui.ListView();
        readonly TextBox _log = new TextBox();
        readonly ProgressBar _bar = new ProgressBar { Dock = DockStyle.Bottom };
        readonly Label _status = Ui.Label("");
        readonly Button _refreshBtn, _updateBtn, _stopBtn;
        CancellationTokenSource _cts;
        bool _running, _updating; // _updating: ticks are locked while installers run

        public UpgradeForm()
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – " + T("App updates"), 1100, 700);
            _lv.Columns.Add(T("Name"));
            _lv.Columns.Add(T("Package ID"));
            _lv.Columns.Add(T("Installed"));
            _lv.Columns.Add(T("Available"));
            _lv.Columns.Add(T("Source"));
            _lv.Columns.Add(T("Status"));
            Ui.AutoSizeColumns(_lv, 260, 260, 110, 110, 70, 260);
            _lv.ItemCheck += (s, e) => { if (_updating) e.NewValue = e.CurrentValue; };

            var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
            split.Panel1.Controls.Add(_lv);
            _log.Multiline = true; _log.ReadOnly = true; _log.ScrollBars = ScrollBars.Both; _log.WordWrap = false;
            _log.Dock = DockStyle.Fill; _log.Font = new Font("Consolas", 9F);
            split.Panel2.Controls.Add(_log);
            Controls.Add(split);

            var bottom = Ui.Flow(DockStyle.Bottom);
            _updateBtn = Ui.Button(T("Update ticked apps"), async (s, e) => await UpdateAsync(), true);
            _stopBtn = Ui.Button(T("Stop"), (s, e) => _cts?.Cancel());
            _stopBtn.Enabled = false;
            _refreshBtn = Ui.Button(T("Check again"), async (s, e) => await LoadAsync());
            bottom.Controls.Add(_updateBtn);
            bottom.Controls.Add(_stopBtn);
            bottom.Controls.Add(_refreshBtn);
            bottom.Controls.Add(Ui.Button(T("All"), (s, e) => SetAll(true)));
            bottom.Controls.Add(Ui.Button(T("None"), (s, e) => SetAll(false)));
            bottom.Controls.Add(Ui.LogPaneButton(split));
            bottom.Controls.Add(_status);
            Controls.Add(bottom);
            _bar.Height = Ui.S(14);
            Controls.Add(_bar);
            Controls.Add(Ui.InfoLabel(this, T("Apps on this PC that have a newer version in winget. Tick the ones to update – installers run silently, one after another.")));
            Controls.Add(Ui.HeaderBar(T("App updates")));

            Theme.Attach(this, () => { foreach (var i in _items) i.Lvi.ForeColor = i.Color(); });
            Shown += async (s, e) => await LoadAsync();
            FormClosing += (s, e) =>
            {
                if (!_updating) { _cts?.Cancel(); return; }   // only the initial check is running – just close
                if (e.CloseReason == CloseReason.UserClosing && !Ui.Ask(this, T("Updates are still running. Stop and close?"))) { e.Cancel = true; return; }
                _cts?.Cancel();
            };
        }

        async Task LoadAsync()
        {
            if (_running) return;
            if (!Winget.IsAvailable) { _status.Text = T("winget was not found on this PC."); return; }
            _running = true;
            _cts = null;
            SetButtons();
            _bar.Style = ProgressBarStyle.Marquee;
            _status.Text = T("Looking for updates…");
            _lv.Items.Clear();
            _items.Clear();
            try
            {
                var r = await Winget.RunAsync("upgrade --accept-source-agreements" + Winget.NoInteract, null, CancellationToken.None, TimeSpan.FromMinutes(5));
                foreach (var row in TableParser.Parse(r.Output).Where(x => x.Source.Length > 0 && !x.Id.EndsWith("…")))
                {
                    var it = new ListViewItem(row.Name) { Checked = !Scanner.IsRuntime(row.Id) };
                    it.SubItems.Add(row.Id);
                    it.SubItems.Add(row.Version);
                    it.SubItems.Add(row.Available);
                    it.SubItems.Add(row.Source);
                    it.SubItems.Add("");
                    var item = new Item { Row = row, Lvi = it };
                    it.Tag = item;
                    _items.Add(item);
                    _lv.Items.Add(it);
                }
                _status.Text = _items.Count == 0 ? T("Everything is up to date.") : F("{0} update(s) available.", _items.Count);
                Log(F("{0} update(s) available.", _items.Count));
            }
            catch (Exception ex) { _status.Text = ex.Message; }
            finally
            {
                _bar.Style = ProgressBarStyle.Continuous;
                _running = false;
                SetButtons();
            }
        }

        async Task UpdateAsync()
        {
            if (_running) return;
            var queue = _items.Where(i => i.Lvi.Checked).ToList();
            if (queue.Count == 0) { Ui.Info(this, T("No apps are ticked.")); return; }
            using var busy = Ui.Busy();
            _running = _updating = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            SetButtons();
            Ui.KeepAwake(true);
            _bar.Maximum = queue.Count;
            _bar.Value = 0;
            int ok = 0, failed = 0;
            try
            {
                foreach (var i in queue)
                {
                    if (ct.IsCancellationRequested) { SetItem(i, T("Skipped (stopped)"), () => Theme.Muted); continue; }
                    SetItem(i, T("Updating…"), () => Theme.Busy);
                    i.Lvi.EnsureVisible();
                    _status.Text = F("Updating {0} of {1}: {2}", _bar.Value + 1, queue.Count, i.Row.Name);
                    Log("");
                    Log($"{i.Row.Name} ({i.Row.Id}) {i.Row.Version} -> {i.Row.Available}");
                    var r = await Winget.RunAsync(Winget.UpgradeArgs(i.Row.Id, i.Row.Source), line =>
                    {
                        if (!Winget.IsNoise(line, out _)) Log("   " + line.Trim());
                    }, ct, TimeSpan.FromMinutes(60));
                    i.Outcome = Winget.Classify(r);
                    var text = i.Outcome == InstallOutcome.AlreadyInstalled ? T("Up to date") : Winget.Describe(r);
                    Log("   => " + text);
                    bool good = i.Outcome == InstallOutcome.Installed || i.Outcome == InstallOutcome.AlreadyInstalled || i.Outcome == InstallOutcome.RebootRequired;
                    if (good) { ok++; SetItem(i, text, () => Theme.Ok); _updating = false; i.Lvi.Checked = false; _updating = true; }
                    else { failed++; SetItem(i, text, i.Outcome == InstallOutcome.Cancelled ? (Func<Color>)(() => Theme.Muted) : () => Theme.Fail); }
                    _bar.Value = Math.Min(_bar.Maximum, _bar.Value + 1);
                }
                _status.Text = F("{0} updated, {1} failed or skipped.", ok, failed);
                Log("=== " + _status.Text + " ===");
            }
            finally
            {
                Ui.KeepAwake(false);
                _running = _updating = false;
                SetButtons();
            }
        }

        void SetButtons()
        {
            _updateBtn.Enabled = _refreshBtn.Enabled = !_running;
            _stopBtn.Enabled = _running && _cts != null && !_cts.IsCancellationRequested;
        }

        void SetAll(bool on) { if (!_running) foreach (var i in _items) i.Lvi.Checked = on; }

        static void SetItem(Item i, string text, Func<Color> color)
        {
            i.Lvi.SubItems[5].Text = text;
            i.Color = color;
            i.Lvi.ForeColor = color();
        }

        void Log(string line)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { try { BeginInvoke(new Action(() => Log(line))); } catch (InvalidOperationException) { } return; }
            _log.AppendText((line.Length == 0 ? "" : DateTime.Now.ToString("HH:mm:ss") + "  " + line) + "\r\n");
        }
    }
}
