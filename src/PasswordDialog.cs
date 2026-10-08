using System.Drawing;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>Asks for a password; with <c>confirm</c> it must be typed twice (used when protecting a package).</summary>
    internal static class PasswordDialog
    {
        public const int MinLength = 8;

        public static string Ask(IWin32Window owner, string prompt, bool confirm)
        {
            using (var f = new Form())
            {
                Ui.Init(f, Program.AppName + " – " + T("Password"), 520, confirm ? 300 : 230);
                f.MinimumSize = new Size(Ui.S(420), Ui.S(confirm ? 280 : 210));
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;

                var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Ui.S(12)), AutoSize = true };
                body.Controls.Add(new Label { Text = prompt, AutoSize = true, MaximumSize = new Size(Ui.S(470), 0), Margin = new Padding(0, 0, 0, Ui.S(8)) });
                var pw1 = new TextBox { UseSystemPasswordChar = true, Width = Ui.S(460) };
                var pw2 = new TextBox { UseSystemPasswordChar = true, Width = Ui.S(460) };
                var show = new CheckBox { Text = T("Show password"), AutoSize = true };
                show.CheckedChanged += (s, e) => pw1.UseSystemPasswordChar = pw2.UseSystemPasswordChar = !show.Checked;
                body.Controls.Add(pw1);
                if (confirm)
                {
                    body.Controls.Add(new Label { Text = T("Type it again:"), AutoSize = true, Margin = new Padding(0, Ui.S(8), 0, Ui.S(2)) });
                    body.Controls.Add(pw2);
                }
                body.Controls.Add(show);
                var error = new Label { AutoSize = true, ForeColor = Theme.Fail, Tag = "keepcolor" };
                body.Controls.Add(error);

                var buttons = Ui.Flow(DockStyle.Bottom);
                buttons.FlowDirection = FlowDirection.RightToLeft;
                var cancel = Ui.Button(T("Cancel"), null); cancel.DialogResult = DialogResult.Cancel;
                var ok = Ui.Button(T("OK"), null, true);
                ok.Click += (s, e) =>
                {
                    if (confirm && pw1.Text.Length < MinLength) { error.Text = F("Use at least {0} characters.", MinLength); return; }
                    if (confirm && pw1.Text != pw2.Text) { error.Text = T("The two passwords are different."); return; }
                    if (pw1.Text.Length == 0) { error.Text = T("Please enter the password."); return; }
                    f.DialogResult = DialogResult.OK;
                };
                buttons.Controls.Add(cancel);
                buttons.Controls.Add(ok);
                f.Controls.Add(body);
                f.Controls.Add(buttons);
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                Theme.Attach(f);
                f.Shown += (s, e) => pw1.Focus();
                return f.ShowDialog(owner) == DialogResult.OK ? pw1.Text : null;
            }
        }
    }
}
