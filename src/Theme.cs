using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OrclWAMP
{
    internal enum ThemeMode { System, Light, Dark }

    /// <summary>Light / dark / follow-Windows theming for all OrclWAMP windows.</summary>
    internal static class Theme
    {
        static ThemeMode _mode = LoadMode();

        /// <summary>Raised (on any thread) when the effective colours change.</summary>
        public static event Action Changed;

        public static ThemeMode Mode
        {
            get => _mode;
            set
            {
                if (_mode == value) return;
                _mode = value;
                SaveMode(value);
                Changed?.Invoke();
            }
        }

        public static bool IsDark => _mode == ThemeMode.Dark || (_mode == ThemeMode.System && WindowsPrefersDark());

        static Theme()
        {
            // Follow Windows live while in "System" mode.
            try
            {
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (_mode == ThemeMode.System && e.Category == UserPreferenceCategory.General) Changed?.Invoke();
                };
            }
            catch { }
        }

        // ---------------------------------------------------------------- palette

        public static Color Back => IsDark ? Color.FromArgb(32, 32, 32) : SystemColors.Control;
        public static Color Surface => IsDark ? Color.FromArgb(43, 43, 43) : SystemColors.Window;
        public static Color HeaderBack => IsDark ? Color.FromArgb(50, 50, 50) : Color.FromArgb(243, 243, 243);
        public static Color Text => IsDark ? Color.FromArgb(240, 240, 240) : SystemColors.ControlText;
        public static Color Muted => IsDark ? Color.FromArgb(150, 150, 150) : SystemColors.GrayText;
        public static Color Border => IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(213, 213, 213);
        public static Color ButtonBack => IsDark ? Color.FromArgb(55, 55, 55) : SystemColors.Control;
        public static Color ButtonHover => IsDark ? Color.FromArgb(70, 70, 70) : Color.FromArgb(229, 241, 251);
        public static Color ToggleOn => IsDark ? Color.FromArgb(110, 60, 20) : Color.FromArgb(255, 222, 190);
        public static Color Accent => IsDark ? Color.FromArgb(255, 140, 40) : Color.FromArgb(214, 90, 10);

        public static Color Ok => IsDark ? Color.FromArgb(110, 205, 125) : Color.FromArgb(0, 120, 40);
        public static Color Fail => IsDark ? Color.FromArgb(255, 115, 115) : Color.FromArgb(190, 30, 30);
        public static Color Busy => IsDark ? Color.FromArgb(115, 175, 255) : Color.FromArgb(0, 90, 180);
        public static Color Warn => IsDark ? Color.FromArgb(255, 175, 85) : Color.FromArgb(170, 95, 0);
        public static Color Manual => IsDark ? Color.FromArgb(235, 165, 85) : Color.FromArgb(156, 92, 0);

        // ---------------------------------------------------------------- applying

        /// <summary>Themes a form now and whenever the theme changes. <paramref name="refresh"/> re-colours custom content.</summary>
        public static void Attach(Form form, Action refresh = null)
        {
            ToolStripManager.Renderer = Renderer;
            Apply(form);
            form.HandleCreated += (s, e) => ApplyTitleBar(form);
            form.Load += (s, e) => ApplyNativeTree(form); // child handles exist only from here on
            Action handler = () =>
            {
                if (form.IsDisposed || !form.IsHandleCreated) return;
                try
                {
                    form.BeginInvoke(new Action(() =>
                    {
                        if (form.IsDisposed) return;
                        form.SuspendLayout();
                        Apply(form);
                        refresh?.Invoke();
                        form.ResumeLayout(true);
                        ApplyTitleBar(form);
                        form.Invalidate(true);
                    }));
                }
                catch (InvalidOperationException) { }
            };
            Changed += handler;
            form.FormClosed += (s, e) => Changed -= handler;
        }

        public static void Apply(Control root)
        {
            if (root is Form f) { f.BackColor = Back; f.ForeColor = Text; }
            ApplyOne(root);
            foreach (Control c in root.Controls) Apply(c);
        }

        static void ApplyOne(Control c)
        {
            bool dark = IsDark;
            switch (c)
            {
                case ListView lv:
                    lv.BackColor = Surface; lv.ForeColor = Text;
                    ApplyNative(lv);
                    break;
                case TextBox tb:
                    tb.BackColor = Surface; tb.ForeColor = Text;
                    if (tb.Multiline) { tb.BorderStyle = dark ? BorderStyle.FixedSingle : BorderStyle.Fixed3D; ApplyNative(tb); }
                    break;
                case NumericUpDown nud:
                    nud.BackColor = Surface; nud.ForeColor = Text;
                    break;
                case ComboBox cb:
                    cb.BackColor = Surface; cb.ForeColor = Text;
                    cb.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                    break;
                case RadioButton rb when rb.Appearance == Appearance.Button: // theme toggle segments
                    rb.FlatStyle = FlatStyle.Flat;
                    rb.BackColor = ButtonBack; rb.ForeColor = Text;
                    rb.FlatAppearance.BorderColor = Border;
                    rb.FlatAppearance.CheckedBackColor = ToggleOn;
                    rb.FlatAppearance.MouseOverBackColor = ButtonHover;
                    break;
                case Button b:
                    if (dark)
                    {
                        b.FlatStyle = FlatStyle.Flat;
                        b.BackColor = ButtonBack; b.ForeColor = Text;
                        b.FlatAppearance.BorderColor = Border;
                        b.FlatAppearance.MouseOverBackColor = ButtonHover;
                        b.FlatAppearance.MouseDownBackColor = Border;
                    }
                    else
                    {
                        b.FlatStyle = FlatStyle.Standard;
                        b.BackColor = SystemColors.Control; b.ForeColor = SystemColors.ControlText;
                        b.UseVisualStyleBackColor = true;
                    }
                    break;
                case ToolStrip ts:
                    ts.RenderMode = ToolStripRenderMode.ManagerRenderMode;
                    ts.BackColor = Back; ts.ForeColor = Text;
                    ts.Invalidate();
                    break;
                case Label l when "muted".Equals(l.Tag):
                    l.ForeColor = Muted;
                    break;
                case Label l when "accent".Equals(l.Tag):
                    l.ForeColor = Accent;
                    break;
            }
            if (c.ContextMenuStrip != null) { c.ContextMenuStrip.RenderMode = ToolStripRenderMode.ManagerRenderMode; c.ContextMenuStrip.BackColor = Back; c.ContextMenuStrip.ForeColor = Text; }
        }

        static void ApplyNativeTree(Control root)
        {
            foreach (Control c in root.Controls)
            {
                if (c is ListView || (c is TextBox tb && tb.Multiline)) ApplyNative(c);
                ApplyNativeTree(c);
            }
        }

        /// <summary>Dark scrollbars / list headers via the Explorer dark theme (Windows 10 1809+).</summary>
        public static void ApplyNative(Control c)
        {
            if (!c.IsHandleCreated) return;
            try
            {
                SetWindowTheme(c.Handle, IsDark ? "DarkMode_Explorer" : "Explorer", null);
                if (c is ListView)
                {
                    var header = SendMessage(c.Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
                    if (header != IntPtr.Zero) SetWindowTheme(header, IsDark ? "DarkMode_ItemsView" : "ItemsView", null);
                }
                c.Invalidate();
            }
            catch { }
        }

        public static void ApplyTitleBar(Form f)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int on = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0)   // Windows 10 20H1+ / 11
                    DwmSetWindowAttribute(f.Handle, 19, ref on, 4);       // Windows 10 1809–1909
                SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020); // redraw frame
            }
            catch { }
        }

        /// <summary>Draws a themed column header (used by every ListView in the app).</summary>
        public static void DrawHeader(ListView lv, DrawListViewColumnHeaderEventArgs e)
        {
            using (var b = new SolidBrush(HeaderBack)) e.Graphics.FillRectangle(b, e.Bounds);
            using (var p = new Pen(Border))
            {
                e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + Ui.S(4), e.Bounds.Right - 1, e.Bounds.Bottom - Ui.S(5));
                e.Graphics.DrawLine(p, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            }
            var r = e.Bounds;
            r.Inflate(-Ui.S(6), 0);
            TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, r, Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        // ---------------------------------------------------------------- settings

        static ThemeMode LoadMode() => Enum.TryParse(AppSettings.Get("Theme"), true, out ThemeMode m) ? m : ThemeMode.System;

        static void SaveMode(ThemeMode m) => AppSettings.Set("Theme", m.ToString());

        static bool WindowsPrefersDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
            }
            catch { return false; }
        }

        // ---------------------------------------------------------------- menus / status bar

        static readonly ToolStripProfessionalRenderer Renderer = new ThemedRenderer();

        sealed class ThemedRenderer : ToolStripProfessionalRenderer
        {
            public ThemedRenderer() : base(new ThemedColors()) { RoundedEdges = false; }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                if (IsDark) e.TextColor = e.Item.Enabled ? Text : Muted;
                base.OnRenderItemText(e);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                if (IsDark) e.ArrowColor = Text;
                base.OnRenderArrow(e);
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                if (IsDark && !(e.ToolStrip is ToolStripDropDown))
                {
                    using (var b = new SolidBrush(Back)) e.Graphics.FillRectangle(b, e.AffectedBounds);
                    return;
                }
                base.OnRenderToolStripBackground(e);
            }
        }

        sealed class ThemedColors : ProfessionalColorTable
        {
            public ThemedColors() { UseSystemColors = false; }
            static Color D(Color dark, Color light) => IsDark ? dark : light;
            static readonly Color Menu = Color.FromArgb(43, 43, 43), Sel = Color.FromArgb(65, 65, 65), Line = Color.FromArgb(75, 75, 75);

            public override Color MenuStripGradientBegin => D(Back, base.MenuStripGradientBegin);
            public override Color MenuStripGradientEnd => D(Back, base.MenuStripGradientEnd);
            public override Color StatusStripGradientBegin => D(Back, base.StatusStripGradientBegin);
            public override Color StatusStripGradientEnd => D(Back, base.StatusStripGradientEnd);
            public override Color ToolStripDropDownBackground => D(Menu, base.ToolStripDropDownBackground);
            public override Color ImageMarginGradientBegin => D(Menu, base.ImageMarginGradientBegin);
            public override Color ImageMarginGradientMiddle => D(Menu, base.ImageMarginGradientMiddle);
            public override Color ImageMarginGradientEnd => D(Menu, base.ImageMarginGradientEnd);
            public override Color MenuItemSelected => D(Sel, base.MenuItemSelected);
            public override Color MenuItemSelectedGradientBegin => D(Sel, base.MenuItemSelectedGradientBegin);
            public override Color MenuItemSelectedGradientEnd => D(Sel, base.MenuItemSelectedGradientEnd);
            public override Color MenuItemPressedGradientBegin => D(Menu, base.MenuItemPressedGradientBegin);
            public override Color MenuItemPressedGradientMiddle => D(Menu, base.MenuItemPressedGradientMiddle);
            public override Color MenuItemPressedGradientEnd => D(Menu, base.MenuItemPressedGradientEnd);
            public override Color MenuItemBorder => D(Line, base.MenuItemBorder);
            public override Color MenuBorder => D(Line, base.MenuBorder);
            public override Color SeparatorDark => D(Line, base.SeparatorDark);
            public override Color SeparatorLight => D(Line, base.SeparatorLight);
            public override Color ToolStripBorder => D(Back, base.ToolStripBorder);
        }

        // ---------------------------------------------------------------- native

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
