using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>Lets the user confirm winget packages found by name for "manual" apps.</summary>
    internal sealed class MatchesForm : Form
    {
        readonly ListView _lv = Ui.ListView();
        public List<Scanner.WingetMatch> Result { get; } = new List<Scanner.WingetMatch>();

        public MatchesForm(List<Scanner.WingetMatch> matches)
        {
            Ui.Init(this, T("winget packages found for manual apps"), 980, 560);
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            MinimizeBox = false;

            _lv.Columns.Add(T("App on this PC"));
            _lv.Columns.Add(T("winget package"));
            _lv.Columns.Add(T("Package ID"));
            _lv.Columns.Add(T("Source"));
            Ui.AutoSizeColumns(_lv, 260, 240, 260, 80);
            foreach (var m in matches)
            {
                var it = new ListViewItem(m.Entry.Name) { Tag = m, Checked = m.Unique };
                it.SubItems.Add(m.Row.Name);
                it.SubItems.Add(m.Row.Id);
                it.SubItems.Add(m.Row.Source.Length > 0 ? m.Row.Source : "winget");
                it.ToolTipText = m.Unique ? T("Only package with exactly this name") : T("Several packages have this name – tick the right one (or none)");
                _lv.Items.Add(it);
            }
            Controls.Add(_lv);

            var bottom = Ui.Flow(DockStyle.Bottom);
            bottom.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button(T("Cancel"), (s, e) => DialogResult = DialogResult.Cancel);
            bottom.Controls.Add(cancel);
            bottom.Controls.Add(Ui.Button(T("Install these with winget"), (s, e) => Accept(), true));
            Controls.Add(bottom);

            var info = Ui.InfoLabel(this, F("winget has packages with exactly the same name for {0} app(s). Ticked ones move to the automatic install list.", matches.Select(m => m.Entry).Distinct().Count()) + "\r\n" +
                       T("Packages are matched by name only – check the publisher/ID before using them. Names with several matches start unticked."));
            Controls.Add(info);
            CancelButton = cancel;
            Theme.Attach(this);
        }

        void Accept()
        {
            // One package per app: the first ticked row wins.
            foreach (ListViewItem it in _lv.CheckedItems)
            {
                var m = (Scanner.WingetMatch)it.Tag;
                if (Result.All(r => r.Entry != m.Entry)) Result.Add(m);
            }
            DialogResult = DialogResult.OK;
        }
    }
}
