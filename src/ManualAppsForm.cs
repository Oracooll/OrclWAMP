using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace OrclWAMP
{
    /// <summary>
    /// Apps winget can't install: shows a download link only where OrclWAMP is sure of it,
    /// and downloads them one by one or all at once into Downloads\OrclWAMP.
    /// </summary>
    internal sealed class ManualAppsForm : Form
    {
        sealed class Row
        {
            public ManualApp App;
            public LinkKind Kind;
            public string Url, Note, File;
            public ListViewItem Lvi;
            public Func<Color> Color = () => Theme.Text;
            public bool CanDownload => Kind == LinkKind.Direct || Kind == LinkKind.Custom;
            public bool HasLink => Kind != LinkKind.None;
        }

        readonly List<Row> _rows = new List<Row>();
        readonly ListView _lv = Ui.ListView();
        readonly Label _status = Ui.Label("");
        readonly Action _changed, _openChecklist;
        readonly Button _dlTicked, _dlAll, _stop;
        CancellationTokenSource _cts;
        bool _running;

        /// <param name="apps">The apps; link edits are written into these objects.</param>
        /// <param name="changed">Called after the user changed a link (caller persists it).</param>
        /// <param name="openChecklist">Optional: opens the printable HTML checklist.</param>
        public ManualAppsForm(IList<ManualApp> apps, Action changed, Action openChecklist)
        {
            _changed = changed;
            _openChecklist = openChecklist;
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – Manual apps & downloads", 1150, 640);
            StartPosition = FormStartPosition.CenterParent;

            _lv.Columns.Add("App");
            _lv.Columns.Add("Version");
            _lv.Columns.Add("Publisher");
            _lv.Columns.Add("Download link");
            _lv.Columns.Add("Status");
            Ui.AutoSizeColumns(_lv, 280, 110, 170, 170, 360);
            foreach (var a in apps.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                var r = new Row { App = a };
                r.Lvi = new ListViewItem(a.Name) { Tag = r, Checked = true };
                r.Lvi.SubItems.Add(a.Version);
                r.Lvi.SubItems.Add(a.Publisher);
                r.Lvi.SubItems.Add("");
                r.Lvi.SubItems.Add("");
                _rows.Add(r);
                _lv.Items.Add(r.Lvi);
                Resolve(r);
            }
            _lv.ItemActivate += async (s, e) => { var r = Selected().FirstOrDefault(); if (r != null) await ActivateAsync(r); };
            _lv.ContextMenuStrip = BuildMenu();
            Controls.Add(_lv);

            var bottom = Ui.Flow(DockStyle.Bottom);
            _dlTicked = Ui.Button("Download ticked", async (s, e) => await DownloadManyAsync(_rows.Where(r => r.Lvi.Checked).ToList()), true);
            _dlAll = Ui.Button("Download all", async (s, e) => await DownloadManyAsync(_rows.ToList()));
            _stop = Ui.Button("Stop", (s, e) => _cts?.Cancel());
            _stop.Enabled = false;
            bottom.Controls.Add(_dlTicked);
            bottom.Controls.Add(_dlAll);
            bottom.Controls.Add(_stop);
            bottom.Controls.Add(Ui.Button("Open downloads folder", (s, e) => OpenFolder()));
            if (openChecklist != null) bottom.Controls.Add(Ui.Button("Printable checklist", (s, e) => _openChecklist()));
            bottom.Controls.Add(_status);
            Controls.Add(bottom);

            int direct = _rows.Count(r => r.CanDownload), pages = _rows.Count(r => r.HasLink && !r.CanDownload), none = _rows.Count(r => !r.HasLink);
            var info = Ui.InfoLabel(this,$"{_rows.Count} apps can't be installed by winget.  {direct} can be downloaded directly, {pages} have an official download page, " +
                       $"{none} have no link OrclWAMP can be sure of.\r\n" +
                       "Double-click an app to download it (or open its page). Right-click to set your own link. Files are saved to: " + Downloads.DownloadFolder);
            Controls.Add(info);
            Controls.Add(Ui.HeaderBar("Manual apps & downloads"));

            Theme.Attach(this, () => { foreach (var r in _rows) r.Lvi.ForeColor = r.Color(); });
            FormClosing += (s, e) =>
            {
                if (_running) { _cts?.Cancel(); e.Cancel = true; _closeAfterStop = true; }
            };
        }

        bool _closeAfterStop;

        ContextMenuStrip BuildMenu()
        {
            var cm = new ContextMenuStrip();
            cm.Items.Add("Download / open link", null, async (s, e) => { foreach (var r in Selected()) await ActivateAsync(r, runIfDownloaded: false); });
            cm.Items.Add("Run downloaded installer", null, (s, e) => { var r = Selected().FirstOrDefault(x => x.File != null); if (r != null) Run(r); });
            cm.Items.Add("Show file in folder", null, (s, e) =>
            {
                var r = Selected().FirstOrDefault(x => x.File != null && File.Exists(x.File));
                if (r != null) Process.Start("explorer.exe", "/select,\"" + r.File + "\"")?.Dispose();
            });
            cm.Items.Add(new ToolStripSeparator());
            cm.Items.Add("Set download link…", null, (s, e) => { var r = Selected().FirstOrDefault(); if (r != null) EditLink(r); });
            cm.Items.Add("Copy link", null, (s, e) => { var r = Selected().FirstOrDefault(x => x.HasLink); if (r != null) Clipboard.SetText(r.Url); });
            cm.Items.Add("Open publisher website", null, (s, e) =>
            {
                var r = Selected().FirstOrDefault(x => Downloads.IsWebUrl(x.App.Homepage));
                if (r != null) Ui.Open(r.App.Homepage); else Ui.Info(this, "No publisher website is known for this app.");
            });
            cm.Items.Add("Search the web for this app", null, (s, e) => { var r = Selected().FirstOrDefault(); if (r != null) Ui.Open(Downloads.SearchUrl(r.App.Name)); });
            return cm;
        }

        List<Row> Selected() => _lv.SelectedItems.Cast<ListViewItem>().Select(i => (Row)i.Tag).ToList();

        void Resolve(Row r)
        {
            r.Kind = Downloads.Resolve(r.App, out r.Url, out r.Note);
            r.Lvi.SubItems[3].Text = Downloads.KindText(r.Kind);
            r.Lvi.ToolTipText = r.HasLink ? r.Url : "No download link OrclWAMP can be sure of – right-click > Set download link…";
            if (r.File == null)
                SetStatus(r, r.Note.Length > 0 ? r.Note : r.HasLink ? "" : "Install manually (right-click for options)", r.HasLink ? (Func<Color>)(() => Theme.Text) : () => Theme.Muted);
        }

        void SetStatus(Row r, string text, Func<Color> color)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(r, text, color))); return; }
            r.Lvi.SubItems[4].Text = text;
            r.Color = color;
            r.Lvi.ForeColor = color();
        }

        async Task ActivateAsync(Row r, bool runIfDownloaded = true)
        {
            if (_running) return;
            if (runIfDownloaded && r.File != null && File.Exists(r.File)) { Run(r); return; }
            if (!r.HasLink) { EditLink(r); return; }
            await RunBatchAsync(new List<Row> { r }, askForPages: false);
        }

        async Task DownloadManyAsync(List<Row> rows)
        {
            if (_running) return;
            if (rows.Count == 0) { Ui.Info(this, "No apps are ticked."); return; }
            await RunBatchAsync(rows, askForPages: true);
        }

        async Task RunBatchAsync(List<Row> rows, bool askForPages)
        {
            _running = true;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _dlTicked.Enabled = _dlAll.Enabled = false;
            _stop.Enabled = true;
            int downloaded = 0, opened = 0, failed = 0;
            try
            {
                var files = rows.Where(r => r.CanDownload).ToList();
                for (int i = 0; i < files.Count && !ct.IsCancellationRequested; i++)
                {
                    var r = files[i];
                    _status.Text = $"Downloading {i + 1} of {files.Count}: {r.App.Name}";
                    var res = await DownloadOneAsync(r, ct);
                    if (res == 1) downloaded++; else if (res == 2) opened++; else if (res == 0) failed++;
                }

                var pages = rows.Where(r => r.HasLink && !r.CanDownload).ToList();
                if (pages.Count > 0 && !ct.IsCancellationRequested &&
                    (!askForPages || Ui.Ask(this, $"{pages.Count} app(s) can't be downloaded directly – they have an official download page instead.\r\n\r\nOpen these pages in your browser now?")))
                {
                    foreach (var r in pages)
                    {
                        Ui.Open(r.Url);
                        SetStatus(r, "Opened " + (r.Kind == LinkKind.WebApp ? "web app – install it from the browser menu" : "download page"), () => Theme.Busy);
                        opened++;
                        await Task.Delay(400);
                    }
                }

                int none = rows.Count(r => !r.HasLink);
                _status.Text = ct.IsCancellationRequested ? "Stopped."
                    : $"{downloaded} downloaded, {opened} page(s) opened, {failed} failed" + (none > 0 ? $", {none} without a link." : ".");
                if (askForPages && !ct.IsCancellationRequested && (downloaded > 0 || none > 0))
                {
                    var msg = downloaded > 0 ? $"{downloaded} installer(s) saved to:\r\n{Downloads.DownloadFolder}\r\n\r\nDouble-click an app in the list to run its installer." : "";
                    if (none > 0) msg += (msg.Length > 0 ? "\r\n\r\n" : "") + $"{none} app(s) have no link OrclWAMP can be sure of. Right-click them to search the web or set your own link.";
                    Ui.Info(this, msg);
                }
            }
            finally
            {
                _running = false;
                _stop.Enabled = false;
                _dlTicked.Enabled = _dlAll.Enabled = true;
                if (_closeAfterStop) BeginInvoke(new Action(Close));
            }
        }

        /// <returns>1 = downloaded, 2 = link was a web page (opened), 0 = failed, -1 = stopped</returns>
        async Task<int> DownloadOneAsync(Row r, CancellationToken ct)
        {
            SetStatus(r, "Downloading…", () => Theme.Busy);
            try
            {
                var path = await Downloads.DownloadAsync(r.Url, r.App.Name, p => SetStatus(r, "Downloading… " + p, () => Theme.Busy), ct);
                if (path == null)
                {
                    Ui.Open(r.Url);
                    SetStatus(r, "Link is a web page – opened it in the browser", () => Theme.Warn);
                    return 2;
                }
                r.File = path;
                SetStatus(r, "Downloaded: " + Path.GetFileName(path) + "  (double-click to install)", () => Theme.Ok);
                return 1;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                SetStatus(r, "Stopped", () => Theme.Muted);
                return -1;
            }
            catch (Exception ex)
            {
                SetStatus(r, "Download failed: " + ex.Message, () => Theme.Fail);
                return 0;
            }
        }

        void Run(Row r)
        {
            if (!Ui.Ask(this, $"Run the installer for {r.App.Name}?\r\n\r\n{r.File}")) return;
            try { Process.Start(new ProcessStartInfo(r.File) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(r.File) })?.Dispose(); }
            catch (Exception ex) { Ui.Warn(this, "Could not start the installer:\r\n" + ex.Message); }
        }

        void EditLink(Row r)
        {
            var current = r.Kind == LinkKind.Custom ? r.Url : "";
            var url = Prompt("Download link for " + r.App.Name,
                "Paste a download link (a direct installer link or the official download page).\r\nLeave empty to remove your link.", current);
            if (url == null) return;
            url = url.Trim();
            if (url.Length > 0 && !Downloads.IsWebUrl(url)) { Ui.Warn(this, "That isn't a web address (it must start with https:// or http://)."); return; }
            r.App.DownloadUrl = url;
            Resolve(r);
            _changed?.Invoke();
        }

        string Prompt(string title, string text, string value)
        {
            using (var f = new Form())
            {
                Ui.Init(f, title, 640, 220);
                f.MinimumSize = new Size(Ui.S(420), Ui.S(200));
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                var tb = new TextBox { Text = value, Dock = DockStyle.Top };
                var lbl = new Label { Text = text, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, Ui.S(6)) };
                var buttons = Ui.Flow(DockStyle.Bottom);
                buttons.FlowDirection = FlowDirection.RightToLeft;
                var cancel = Ui.Button("Cancel", null); cancel.DialogResult = DialogResult.Cancel;
                var ok = Ui.Button("OK", null, true); ok.DialogResult = DialogResult.OK;
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(Ui.S(12)) };
                body.Controls.Add(tb);
                body.Controls.Add(lbl);
                f.Controls.Add(body);
                f.Controls.Add(buttons);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                Theme.Attach(f);
                return f.ShowDialog(this) == DialogResult.OK ? tb.Text : null;
            }
        }

        static void OpenFolder()
        {
            try { Directory.CreateDirectory(Downloads.DownloadFolder); } catch { }
            Ui.Open(Downloads.DownloadFolder);
        }
    }
}
