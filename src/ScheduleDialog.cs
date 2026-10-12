using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>Turns the weekly unattended app updates on or off and sets the day and time.</summary>
    internal sealed class ScheduleDialog : Form
    {
        readonly CheckBox _on = Ui.Check("", false);
        readonly ComboBox _day = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = Ui.S(160) };
        readonly DateTimePicker _time = new DateTimePicker { Format = DateTimePickerFormat.Custom, CustomFormat = "HH:mm", ShowUpDown = true, Width = Ui.S(90) };
        readonly CheckBox _runtimes = Ui.Check("", false);
        readonly Label _state = new Label { AutoSize = true, UseMnemonic = false, Tag = "keepcolor" };
        readonly Label _error = new Label { AutoSize = true, UseMnemonic = false, ForeColor = Theme.Fail, Tag = "keepcolor" };
        readonly Button _save, _runNow;
        UpdateSchedule _current;
        bool _readFailed, _blocked, _busy, _stateOk = true, _runtimesKnown, _runtimesLoaded;

        public ScheduleDialog()
        {
            Ui.Init(this, Program.AppName + " " + Program.VersionText + " – " + T("Weekly app updates"), 640, 410);
            MinimumSize = new Size(Ui.S(560), Ui.S(380));
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            int wrap = Ui.S(580);

            _on.Text = T("Update apps automatically once a week");
            _on.Font = Ui.BoldFont;
            _runtimes.Text = T("Also update runtimes (Visual C++, .NET, Windows App Runtime…)");
            var rules = AppUpdater.LoadRules();
            _runtimesKnown = rules != null;
            _runtimes.Checked = _runtimesLoaded = rules?.Runtimes ?? false;
            var culture = UpdateSchedule.Culture();
            // Monday first, Sunday last.
            for (int i = 1; i <= 7; i++) _day.Items.Add(culture.DateTimeFormat.GetDayName((DayOfWeek)(i % 7)));

            var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(Ui.S(12)), AutoScroll = true };
            body.Controls.Add(_state);
            body.Controls.Add(_on);
            var when = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(Ui.S(22), 0, 0, 0) };
            when.Controls.Add(Ui.Label(T("Day:")));
            when.Controls.Add(_day);
            when.Controls.Add(Ui.Label(T("Time:")));
            when.Controls.Add(_time);
            body.Controls.Add(when);
            _runtimes.Margin = new Padding(Ui.S(24), Ui.S(4), Ui.S(6), Ui.S(4));
            body.Controls.Add(_runtimes);
            body.Controls.Add(new Label
            {
                AutoSize = true, UseMnemonic = false, MaximumSize = new Size(wrap, 0), Margin = new Padding(0, Ui.S(10), 0, Ui.S(6)),
                Text = T("OrclWAMP then updates every app that is ticked by default in the App updates window – right-click an app there to leave it out. It runs in the background with a tray icon while you are signed in, with your normal rights – apps that need administrator rights to update may ask for them. If the PC is off or asleep at that time, it runs at the next chance. Each run writes a log.")
            });
            var last = new Label { AutoSize = true, UseMnemonic = false, MaximumSize = new Size(wrap, 0), Text = T("Last automatic run:") + " " + AppUpdater.LastRunText() };
            body.Controls.Add(last);
            body.Controls.Add(Ui.Button(T("Open the log folder"), (s, e) => { Directory.CreateDirectory(AppUpdater.LogDir); Ui.Open(AppUpdater.LogDir); }));
            body.Controls.Add(_error);

            _on.CheckedChanged += (s, e) => SyncEnabled();

            var buttons = Ui.Flow(DockStyle.Bottom);
            buttons.FlowDirection = FlowDirection.RightToLeft;
            var cancel = Ui.Button(T("Cancel"), null); cancel.DialogResult = DialogResult.Cancel;
            _save = Ui.Button(T("Save"), (s, e) => Save(), true);
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(_save);
            _runNow = Ui.Button(T("Run now"), (s, e) => RunNow());
            buttons.Controls.Add(_runNow);

            Controls.Add(body);
            Controls.Add(buttons);
            AcceptButton = _save;
            CancelButton = cancel;
            Theme.Attach(this, () => { _state.ForeColor = _stateOk ? Theme.Text : Theme.Fail; _error.ForeColor = Theme.Fail; });
            _state.MaximumSize = _error.MaximumSize = new Size(wrap, 0);
            Load += (s, e) => LoadCurrent();
            FormClosing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        void LoadCurrent()
        {
            _readFailed = false;
            try { _current = UpdateSchedule.Query(); }
            catch (Exception ex)
            {
                // Don't pretend it's off: saving "off" would then leave the task in place.
                _readFailed = true;
                _error.Text = T("Task Scheduler could not be read:") + " " + ex.Message;
            }
            var s = _current ?? new UpdateSchedule();
            _on.Checked = _current != null;
            _day.SelectedIndex = ((int)s.Day + 6) % 7;
            _time.Value = DateTime.Today.AddHours(s.Hour).AddMinutes(s.Minute);

            if (_readFailed) _state.Text = "";
            else if (_current == null) _state.Text = T("Automatic updates are off.");
            else
            {
                _state.Text = T("Automatic updates:") + " " + _current.Describe();
                bool ok = _current.Enabled;
                if (!_current.Enabled) _state.Text += "\r\n" + T("The task is disabled in Task Scheduler – click Save to turn it on again.");
                var intended = UpdateSchedule.IntendedExe();
                if (_current.ExePath == null || !File.Exists(_current.ExePath))
                {
                    ok = false;
                    _state.Text += "\r\n" + F("It points to a copy of OrclWAMP that no longer exists: {0}. Click Save to fix it.", _current.ExePath ?? "?");
                }
                else if (_current.Elevated)
                    _state.Text += "\r\n" + T("It is set to run as administrator. Click Save to run it with your normal rights.");
                else if (!SamePath(_current.ExePath, intended))
                    _state.Text += "\r\n" + F("It runs another copy of OrclWAMP: {0}. Save to use this copy instead.", _current.ExePath);
                else if (!SamePath(_current.ExePath, Application.ExecutablePath))
                {
                    var theirs = UpdateSchedule.FileVersionOf(_current.ExePath);
                    var mine = UpdateSchedule.FileVersionOf(Application.ExecutablePath);
                    if (theirs != null && mine != null && theirs < mine)
                        _state.Text += "\r\n" + F("The scheduled copy of OrclWAMP is older ({0}). Click Save to update it.", theirs.ToString(3));
                }
                _stateOk = ok;
            }
            _state.ForeColor = _stateOk ? Theme.Text : Theme.Fail;
            if (!Ui.RunsAsSessionUser())
            {
                _error.Text = T("OrclWAMP is running as a different user than the one signed in. Start it normally (not with another account) to set up your weekly updates.");
                _blocked = true;
            }
            SyncEnabled();
        }

        static bool SamePath(string a, string b)
        {
            try { return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        void SyncEnabled()
        {
            _day.Enabled = _time.Enabled = _on.Checked && !_busy;
            _on.Enabled = !_readFailed && !_blocked && !_busy;
            _runtimes.Enabled = _runtimesKnown && !_blocked && !_busy;
            _save.Enabled = !_readFailed && !_blocked && !_busy;
            _runNow.Enabled = _current != null && !_blocked && !_busy;
        }

        async void Save()
        {
            if (_busy || _readFailed || _blocked) return;
            _error.Text = "";
            bool turnOn = _on.Checked;
            var s = new UpdateSchedule
            {
                Day = (DayOfWeek)((_day.SelectedIndex + 1) % 7),
                Hour = _time.Value.Hour,
                Minute = _time.Value.Minute
            };
            if (turnOn && UpdateSchedule.ExeOnRemovableDrive() &&
                !Ui.Ask(this, F("OrclWAMP is running from a removable or network drive, which may not be connected when the update runs.\r\n\r\nCopy OrclWAMP to {0} and schedule that copy?", UpdateSchedule.LocalCopyPath)))
                return;
            bool wasOn = _current != null;
            string err;
            SetBusy(true);
            try { err = await System.Threading.Tasks.Task.Run(() => turnOn ? s.Save() : wasOn ? UpdateSchedule.Remove() : null); }
            catch (Exception ex) { err = ex.Message; }
            finally { SetBusy(false); }
            if (IsDisposed) return;
            if (!string.IsNullOrEmpty(err)) { _error.Text = err; return; }
            if (_runtimesKnown && _runtimesLoaded != _runtimes.Checked && !AppSettings.Set("UpdateRuntimes", _runtimes.Checked ? "1" : "0"))
            {
                _error.Text = T("The runtimes setting could not be saved (settings.ini is in use). Try again.");
                return;
            }
            DialogResult = DialogResult.OK;
        }

        async void RunNow()
        {
            if (_busy) return;
            if (HasUnsavedChanges()) { _error.Text = T("Save your changes first – Run now uses the saved schedule."); return; }
            if (!Ui.Ask(this, T("Start the automatic update now? It runs in the background – watch for the OrclWAMP tray icon."))) return;
            string err;
            SetBusy(true);
            try { err = await System.Threading.Tasks.Task.Run(() => UpdateSchedule.RunNow()); }
            finally { SetBusy(false); }
            if (IsDisposed) return;
            if (!string.IsNullOrEmpty(err)) _error.Text = err;
            else DialogResult = DialogResult.OK;
        }

        bool HasUnsavedChanges()
        {
            if (_current == null) return _on.Checked;
            return !_on.Checked || _current.Day != (DayOfWeek)((_day.SelectedIndex + 1) % 7) || _current.Hour != _time.Value.Hour ||
                   _current.Minute != _time.Value.Minute || (_runtimesKnown && _runtimes.Checked != _runtimesLoaded);
        }

        void SetBusy(bool on)
        {
            _busy = on;
            UseWaitCursor = on;
            SyncEnabled();
        }
    }
}
