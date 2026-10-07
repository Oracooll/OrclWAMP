using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace OrclWAMP
{
    /// <summary>Search the winget catalog and pick extra packages to add to the list.</summary>
    internal sealed class SearchForm : Form
    {
        readonly TextBox _query = new TextBox();
        readonly ListView _lv = Ui.ListView();
        readonly Label _status = Ui.Label("Type a name (e.g. \"firefox\") and press Enter.");
        readonly Button _searchBtn, _addBtn;
        CancellationTokenSource _cts;

        public List<AppEntry> Result { get; } = new List<AppEntry>();

        public SearchForm()
        {
            Ui.Init(this, "Add apps from winget", 860, 560);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            _lv.Columns.Add("Name");
            _lv.Columns.Add("Package ID");
            _lv.Columns.Add("Version");
            _lv.Columns.Add("Source");
            Ui.AutoSizeColumns(_lv, 300, 300, 110, 80);
            _lv.ItemActivate += (s, e) => { foreach (ListViewItem it in _lv.SelectedItems) it.Checked = !it.Checked; };
            Controls.Add(_lv);

            var bottom = Ui.Flow(DockStyle.Bottom);
            bottom.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button("Cancel", (s, e) => { DialogResult = DialogResult.Cancel; });
            _addBtn = Ui.Button("Add ticked apps", (s, e) => Accept(), true);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(_addBtn);
            bottom.Controls.Add(_status);
            Controls.Add(bottom);

            var top = Ui.Flow(DockStyle.Top);
            top.Controls.Add(Ui.Label("Search winget:"));
            _query.Width = Ui.S(360);
            _query.Anchor = AnchorStyles.Left;
            top.Controls.Add(_query);
            _searchBtn = Ui.Button("Search", async (s, e) => await SearchAsync(), true);
            top.Controls.Add(_searchBtn);
            Controls.Add(top);

            AcceptButton = _searchBtn;
            CancelButton = cancel;
            FormClosing += (s, e) => _cts?.Cancel();
            Shown += (s, e) => _query.Focus();
        }

        async System.Threading.Tasks.Task SearchAsync()
        {
            var q = _query.Text.Trim();
            if (q.Length < 2) { _status.Text = "Type at least 2 characters."; return; }
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _searchBtn.Enabled = false;
            UseWaitCursor = true;
            _status.Text = "Searching…";
            try
            {
                var r = await Winget.RunAsync("search " + Winget.Quote(q) + " --accept-source-agreements" + Winget.NoInteract, null, ct, TimeSpan.FromMinutes(3));
                if (ct.IsCancellationRequested || IsDisposed) return;
                var all = TableParser.Parse(r.Output);
                // A cut-off ID ("Mozilla.Firefox.Develo…") can't be installed with --exact – don't offer it.
                var rows = all.Where(x => !x.Id.EndsWith("…")).ToList();
                int hidden = all.Count - rows.Count;
                _lv.BeginUpdate();
                _lv.Items.Clear();
                foreach (var row in rows)
                {
                    var it = new ListViewItem(row.Name) { Tag = row };
                    it.SubItems.Add(row.Id);
                    it.SubItems.Add(row.Version);
                    it.SubItems.Add(row.Source.Length > 0 ? row.Source : "winget");
                    _lv.Items.Add(it);
                }
                _lv.EndUpdate();
                _status.Text = (rows.Count == 0 ? "No packages found." : rows.Count + " result(s). Tick the apps to add (double-click toggles).")
                               + (hidden > 0 ? $"  {hidden} result(s) hidden – search more specifically." : "");
            }
            finally
            {
                if (!IsDisposed) { _searchBtn.Enabled = true; UseWaitCursor = false; }
            }
        }

        void Accept()
        {
            foreach (ListViewItem it in _lv.CheckedItems)
            {
                var row = (TableParser.Row)it.Tag;
                var src = row.Source.Length > 0 ? row.Source : "winget";
                Result.Add(new AppEntry
                {
                    Name = row.Name, Id = row.Id, Version = row.Version, Source = src,
                    Category = src.Equals("msstore", StringComparison.OrdinalIgnoreCase) ? AppCategory.Store : AppCategory.Winget
                });
            }
            if (Result.Count == 0) { _status.Text = "Tick at least one app first."; return; }
            DialogResult = DialogResult.OK;
        }
    }
}
