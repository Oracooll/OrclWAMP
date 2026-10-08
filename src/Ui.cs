using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading.Tasks;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    internal static class Ui
    {
        public static readonly float Dpi = GetDpi();
        public static readonly Font BaseFont = new Font("Segoe UI", 9F);
        public static readonly Font BoldFont = new Font("Segoe UI", 9F, FontStyle.Bold);
        public static readonly Font TitleFont = new Font("Segoe UI Semibold", 12F);
        public static readonly Font HeaderFont = new Font("Segoe UI", 18F, FontStyle.Bold);
        public static readonly Image Logo = LoadLogo();
        static Icon _appIcon;
        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null) try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
                return _appIcon;
            }
        }

        static Image LoadLogo()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("OrclWAMP.logo.png"))
                {
                    if (s == null) return null;
                    using (var tmp = new Bitmap(s)) return new Bitmap(tmp); // copy: GDI+ needs the stream alive otherwise
                }
            }
            catch { return null; }
        }

        static float GetDpi()
        {
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX; } catch { return 96f; }
        }

        public static int S(int px) => (int)Math.Round(px * Dpi / 96f);

        public static Button Button(string text, EventHandler click, bool primary = false)
        {
            var b = new Button
            {
                Text = text,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(S(8), S(3), S(8), S(3)),
                Margin = new Padding(S(3)),
                UseVisualStyleBackColor = true
            };
            if (primary) b.Font = BoldFont;
            if (click != null) b.Click += click;
            return b;
        }

        public static Label Label(string text) =>
            new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(S(6), S(3), S(3), S(3)) };

        public static CheckBox Check(string text, bool value) =>
            new CheckBox { Text = text, Checked = value, AutoSize = true, UseMnemonic = false, Anchor = AnchorStyles.Left, Margin = new Padding(S(6), S(4), S(6), S(4)) };

        public static FlowLayoutPanel Flow(DockStyle dock) =>
            new FlowLayoutPanel { Dock = dock, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Padding = new Padding(S(4)) };

        public static ListView ListView()
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                CheckBoxes = true,
                FullRowSelect = true,
                HideSelection = false,
                ShowItemToolTips = true,
                AllowColumnReorder = true
            };
            DoubleBuffer(lv);
            // Owner-drawn header so it follows the theme; rows/checkboxes keep the native drawing.
            lv.OwnerDraw = true;
            lv.DrawColumnHeader += (s, e) => Theme.DrawHeader(lv, e);
            lv.DrawItem += (s, e) => e.DrawDefault = true;
            lv.DrawSubItem += (s, e) => e.DrawDefault = true;
            lv.HandleCreated += (s, e) => Theme.ApplyNative(lv);
            return lv;
        }

        public static void DoubleBuffer(Control c)
        {
            try { typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(c, true, null); } catch { }
        }

        public static void Init(Form f, string title, int width, int height)
        {
            f.Text = title;
            f.Font = BaseFont;
            f.AutoScaleMode = AutoScaleMode.None;
            f.StartPosition = FormStartPosition.CenterScreen;
            var wa = Screen.PrimaryScreen.WorkingArea;
            f.Size = new Size(Math.Min(S(width), wa.Width), Math.Min(S(height), wa.Height));
            f.MinimumSize = new Size(Math.Min(S(720), wa.Width), Math.Min(S(480), wa.Height));
            if (AppIcon != null)
            {
                f.Icon = AppIcon;
                f.Load += (s, e) => f.Icon = AppIcon; // dialogs re-create their handle (ShowInTaskbar etc.) – set it again
            }
        }

        public static bool IsAdmin()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator); }
            catch { return false; }
        }

        /// <summary>Starts this exe elevated. Returns null if the user declined UAC.</summary>
        public static Process RelaunchElevated(string args)
        {
            try
            {
                return Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Program.AppDir });
            }
            catch (Win32Exception) { return null; }
        }

        public static string CurrentSid()
        {
            try { using (var id = WindowsIdentity.GetCurrent()) return id.User?.Value ?? ""; } catch { return ""; }
        }

        /// <summary>UNC paths and mapped network drives are invisible to an elevated process.</summary>
        public static bool IsNetworkPath(string path)
        {
            try
            {
                if (path.StartsWith(@"\\")) return true;
                var root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path));
                return new System.IO.DriveInfo(root).DriveType == System.IO.DriveType.Network;
            }
            catch { return false; }
        }

        /// <summary>The "OrclWAMP 1.4.001 – subtitle" header shown at the top-left of every window.</summary>
        public static Control AppHeader(string subtitle)
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0) };
            if (Logo != null)
                p.Controls.Add(new PictureBox
                {
                    Image = Logo, SizeMode = PictureBoxSizeMode.Zoom, Size = new Size(S(40), S(40)),
                    Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, S(6), 0)
                });
            p.Controls.Add(new Label { Text = Program.AppName, UseMnemonic = false, Tag = "accent", AutoSize = true, Font = HeaderFont, ForeColor = Theme.Accent, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(S(2), 0, 0, 0) });
            p.Controls.Add(new Label { Text = Program.VersionText, UseMnemonic = false, Tag = "muted", AutoSize = true, Font = TitleFont, ForeColor = Theme.Muted, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(S(2), 0, S(14), S(3)) });
            if (!string.IsNullOrEmpty(subtitle))
                p.Controls.Add(new Label { Text = subtitle, UseMnemonic = false, AutoSize = true, Font = TitleFont, Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Margin = new Padding(0, 0, 0, S(3)) });
            return p;
        }

        /// <summary>
        /// Button that moves the log pane of <paramref name="split"/> (Panel2) between the bottom and the right side.
        /// The choice is remembered for all windows.
        /// </summary>
        public static Button LogPaneButton(SplitContainer split)
        {
            var b = Button("", null);
            void Show(bool right)
            {
                split.Orientation = right ? Orientation.Vertical : Orientation.Horizontal;
                try
                {
                    int size = right ? split.Width : split.Height;
                    split.SplitterDistance = Math.Max(split.Panel1MinSize, Math.Min(size - split.Panel2MinSize - 1, size * (right ? 58 : 62) / 100));
                }
                catch { }
                b.Text = right ? T("Log: move to bottom ⤓") : T("Log: move to right ⇥");
            }
            bool startRight = string.Equals(AppSettings.Get("LogPane"), "Right", StringComparison.OrdinalIgnoreCase);
            b.Text = startRight ? T("Log: move to bottom ⤓") : T("Log: move to right ⇥");
            b.Click += (s, e) =>
            {
                bool right = split.Orientation != Orientation.Vertical;
                Show(right);
                AppSettings.Set("LogPane", right ? "Right" : "Bottom");
            };
            // Apply the saved layout once the window has its real size.
            split.HandleCreated += (s, e) => split.BeginInvoke(new Action(() => Show(startRight)));
            new ToolTip().SetToolTip(b, T("Show the log below the list or next to it"));
            return b;
        }

        /// <summary>A multi-line explanation label docked at the top that wraps to the window's width.</summary>
        public static Label InfoLabel(Form owner, string text)
        {
            var l = new Label { Text = text, Dock = DockStyle.Top, AutoSize = true, UseMnemonic = false, Padding = new Padding(S(8), S(6), S(8), S(6)) };
            void Fit() => l.MaximumSize = new Size(Math.Max(S(200), owner.ClientSize.Width - S(4)), 0);
            Fit();
            owner.Resize += (s, e) => Fit();
            return l;
        }

        /// <summary>Header row: app name/version on the left, Light / Dark / System toggle on the right.</summary>
        public static TableLayoutPanel HeaderBar(string subtitle)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var left = AppHeader(subtitle);
            left.Anchor = AnchorStyles.Left;
            var right = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0), Padding = new Padding(0), Anchor = AnchorStyles.Right };
            right.Controls.Add(UpdateLink());
            right.Controls.Add(LanguageToggle());
            right.Controls.Add(ThemeToggle());
            t.Controls.Add(left, 0, 0);
            t.Controls.Add(right, 1, 0);
            return t;
        }

        // ---- "busy" tracking (installing, copying, writing a package…) ----
        static int _busyCount;
        public static bool AnyBusy => System.Threading.Volatile.Read(ref _busyCount) > 0;

        /// <summary>Marks a long operation; dispose when it ends.</summary>
        public static IDisposable Busy()
        {
            System.Threading.Interlocked.Increment(ref _busyCount);
            return new Disposer(() => System.Threading.Interlocked.Decrement(ref _busyCount));
        }

        /// <summary>
        /// Restarts OrclWAMP with the same package, but never with /unattended or elevation arguments
        /// (a restart must not start installing on its own). Only if every window agrees to close.
        /// </summary>
        static void RestartApp()
        {
            var keep = new System.Collections.Generic.List<string>();
            var args = Environment.GetCommandLineArgs();
            for (int i = 1; i < args.Length; i++)
            {
                var a = args[i].TrimStart('/', '-').ToLowerInvariant();
                if (a == "unattended" || a == "auto" || a == "quiet" || a == "elevated") continue;
                if (a == "launcher") { if (i + 1 < args.Length && !args[i + 1].StartsWith("/")) i++; continue; }
                var arg = args[i];
                // A package path given relative to the old working folder must still work after the restart.
                if (!arg.StartsWith("/") && !arg.StartsWith("-")) try { arg = Path.GetFullPath(arg); } catch { }
                keep.Add(Winget.Quote(arg));
            }
            var ce = new CancelEventArgs();
            Application.Exit(ce);
            if (ce.Cancel) return; // a window refused to close (still busy)
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", keep)) { UseShellExecute = true, WorkingDirectory = Program.AppDir })?.Dispose(); }
            catch { }
        }

        static Task<UpdateCheck.Result> _updateTask;

        /// <summary>"New version x available" link – hidden unless GitHub has a newer release.</summary>
        static Control UpdateLink()
        {
            var link = new LinkLabel { AutoSize = true, Visible = false, Anchor = AnchorStyles.Left, Margin = new Padding(0, S(6), S(10), 0), LinkColor = Theme.Accent, ActiveLinkColor = Theme.Accent, Tag = "accent" };
            link.HandleCreated += async (s, e) =>
            {
                if (_updateTask == null) _updateTask = UpdateCheck.CheckAsync();
                var r = await _updateTask;
                if (r == null || link.IsDisposed) return;
                link.Text = Lang.F("New version {0} available", r.Version);
                link.Visible = true;
                link.LinkClicked += (s2, e2) => Open(r.Url);
                new ToolTip().SetToolTip(link, r.Url);
            };
            return link;
        }

        /// <summary>EN / BG switch. The new language is used after OrclWAMP restarts.</summary>
        static Control LanguageToggle()
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, 0, S(8), 0), Padding = new Padding(0) };
            foreach (var (code, text) in new[] { ("en", "EN"), ("bg", "BG") })
            {
                var rb = new RadioButton
                {
                    Text = text, Appearance = Appearance.Button, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter, FlatStyle = FlatStyle.Flat,
                    Padding = new Padding(S(4), S(1), S(4), S(1)), Margin = new Padding(0), Checked = Lang.Code == code, Cursor = Cursors.Hand
                };
                new ToolTip().SetToolTip(rb, code == "en" ? "English" : "Български");
                rb.CheckedChanged += (s, e) =>
                {
                    if (!rb.Checked || Lang.Code == code) return;
                    Lang.Save(code);
                    if (AnyBusy)
                    {
                        // Never restart in the middle of installing / copying – just use the language next time.
                        MessageBox.Show(p.FindForm(), code == "bg" ? "Езикът е запазен и ще се използва при следващото стартиране на OrclWAMP."
                                                                   : "The language is saved and will be used the next time OrclWAMP starts.",
                                        Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return;
                    }
                    var msg = code == "bg" ? "Езикът ще се смени след рестартиране на OrclWAMP. Рестартиране сега?"
                                           : "The language changes after OrclWAMP restarts. Restart now?";
                    if (MessageBox.Show(p.FindForm(), msg, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        RestartApp();
                };
                p.Controls.Add(rb);
            }
            return p;
        }

        /// <summary>Three-segment Light / Dark / System switch; all open windows stay in sync.</summary>
        public static Control ThemeToggle()
        {
            var p = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(S(8), 0, 0, 0), Padding = new Padding(0) };
            var tips = new ToolTip();
            bool syncing = false;
            var buttons = new System.Collections.Generic.List<RadioButton>();
            foreach (var (mode, text, tip) in new[]
            {
                (ThemeMode.Light, "☀  " + T("Light"), T("Light theme")),
                (ThemeMode.Dark, "☾  " + T("Dark"), T("Dark theme")),
                (ThemeMode.System, "◐  " + T("System"), T("Follow the Windows setting"))
            })
            {
                var rb = new RadioButton
                {
                    Text = text, Tag = mode, Appearance = Appearance.Button, AutoSize = true, TextAlign = ContentAlignment.MiddleCenter,
                    FlatStyle = FlatStyle.Flat, Padding = new Padding(S(6), S(1), S(6), S(1)), Margin = new Padding(0),
                    Checked = Theme.Mode == mode, Cursor = Cursors.Hand
                };
                tips.SetToolTip(rb, tip);
                rb.CheckedChanged += (s, e) => { if (rb.Checked && !syncing) Theme.Mode = mode; };
                buttons.Add(rb);
                p.Controls.Add(rb);
            }
            // Keep every window's toggle in step with the current mode.
            Action sync = () =>
            {
                if (p.IsDisposed) return;
                syncing = true;
                foreach (var b in buttons) b.Checked = (ThemeMode)b.Tag == Theme.Mode;
                syncing = false;
            };
            Action handler = () => { if (p.IsHandleCreated && !p.IsDisposed) try { p.BeginInvoke(sync); } catch (InvalidOperationException) { } };
            Theme.Changed += handler;
            p.Disposed += (s, e) => Theme.Changed -= handler;
            return p;
        }

        public static void Open(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public static void Launch(string exe, string args)
        {
            try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WorkingDirectory = Program.AppDir }); }
            catch (Exception ex) { MessageBox.Show(ex.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        }

        public static void Info(IWin32Window owner, string text) => MessageBox.Show(owner, text, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        public static void Warn(IWin32Window owner, string text) => MessageBox.Show(owner, text, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        public static void Error(IWin32Window owner, string text) => MessageBox.Show(owner, text, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
        public static bool Ask(IWin32Window owner, string text) => MessageBox.Show(owner, text, Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;

        // ---- keep the PC awake while installing ----
        [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint esFlags);
        const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;
        public static void KeepAwake(bool on)
        {
            try { SetThreadExecutionState(on ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS); } catch { }
        }

        public static void AutoSizeColumns(ListView lv, params int[] widths)
        {
            for (int i = 0; i < widths.Length && i < lv.Columns.Count; i++) lv.Columns[i].Width = S(widths[i]);
        }
    }

    /// <summary>Runs an action once when disposed.</summary>
    internal sealed class Disposer : IDisposable
    {
        Action _a;
        public Disposer(Action a) { _a = a; }
        public void Dispose() { var a = _a; _a = null; a?.Invoke(); }
    }
}
