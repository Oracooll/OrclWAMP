using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows.Forms;
using System.Xml;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    /// <summary>
    /// The weekly "OrclWAMP /upgrade /unattended" task in Windows Task Scheduler (one per user). It runs only while
    /// the user is signed in, so installers and the tray icon appear on their desktop, and catches up after a missed run.
    /// <para>
    /// It always runs with the user's normal rights. A task that starts elevated without a prompt would let any
    /// program running as the user get admin rights (through winget on the user's PATH, profiler variables…),
    /// so installers that need admin rights ask for them as usual.
    /// </para>
    /// </summary>
    internal sealed class UpdateSchedule
    {
        const string BaseName = "OrclWAMP weekly app updates";
        const string TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        const int TaskCreateOrUpdate = 6, LogonInteractiveToken = 3;

        public DayOfWeek Day = DayOfWeek.Sunday;
        public int Hour = 12, Minute = 0;
        public bool Elevated; // read only: tasks are always saved with normal rights
        public bool Enabled = true;
        public string ExePath;

        public string Describe() =>
            F("Every {0} at {1:00}:{2:00}", Culture().DateTimeFormat.GetDayName(Day), Hour, Minute);

        /// <summary>Day names in the UI language (not the Windows format language).</summary>
        public static System.Globalization.CultureInfo Culture() =>
            IsBg ? new System.Globalization.CultureInfo("bg-BG") : System.Globalization.CultureInfo.InvariantCulture;

        /// <summary>A copy in %LOCALAPPDATA% for a normal task when OrclWAMP runs from a USB stick or network drive.</summary>
        public static string LocalCopyPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OrclWAMP", "OrclWAMP.exe");

        /// <summary>One task per user, so two people on the same PC don't replace each other's schedule.</summary>
        public static string TaskNameFor(string sid)
        {
            string user = null;
            try { user = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value; } catch { }
            if (string.IsNullOrEmpty(user)) user = sid;
            // "PC\john" and "DOMAIN\john" are different people: keep the domain.
            user = user.Replace('\\', '-');
            var clean = new string(user.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '.' || c == '-' || c == '_').ToArray()).Trim();
            return BaseName + " (" + (clean.Length > 0 ? clean : "user") + ")";
        }

        public static string TaskName => TaskNameFor(Ui.CurrentSid());

        /// <summary>The exe the task would start if it were saved from this copy of OrclWAMP right now.</summary>
        public static string IntendedExe() => ExeOnRemovableDrive() ? LocalCopyPath : Application.ExecutablePath;

        // ---- Task Scheduler COM (late bound; nothing depends on the console code page) ----
        static object Call(object o, string name, params object[] args) => o.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, o, args);
        static object Get(object o, string name) => o.GetType().InvokeMember(name, BindingFlags.GetProperty, null, o, null);

        static void Release(params object[] objs)
        {
            foreach (var o in objs)
                if (o != null && Marshal.IsComObject(o)) try { Marshal.ReleaseComObject(o); } catch { }
        }

        /// <summary>Runs an action against the root task folder; COM errors come out unwrapped.</summary>
        static T WithRoot<T>(Func<object, T> action)
        {
            object svc = null, folder = null;
            try
            {
                svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service", true));
                Call(svc, "Connect");
                folder = Call(svc, "GetFolder", @"\");
                return action(folder);
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
            finally { Release(folder, svc); }
        }

        static bool IsNotFound(Exception ex) =>
            ex is FileNotFoundException || ex is DirectoryNotFoundException || (ex as COMException)?.ErrorCode == unchecked((int)0x80070002);

        static bool IsAccessDenied(Exception ex) =>
            ex is UnauthorizedAccessException || (ex as COMException)?.ErrorCode == unchecked((int)0x80070005);

        /// <summary>The current user's schedule, or null if there is none. Throws if Task Scheduler can't be read.</summary>
        public static UpdateSchedule Query() => Query(TaskName);

        static UpdateSchedule Query(string name) => WithRoot(folder =>
        {
            object task = null;
            try
            {
                try { task = Call(folder, "GetTask", @"\" + name); }
                catch (TargetInvocationException ex) when (ex.InnerException != null && IsNotFound(ex.InnerException)) { return null; }
                var s = Parse((string)Get(task, "Xml"));
                s.Enabled = s.Enabled && (bool)Get(task, "Enabled");
                return s;
            }
            finally { Release(task); }
        });

        internal static UpdateSchedule Parse(string xml)
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml);
            var ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("t", TaskNs);
            var s = new UpdateSchedule();
            var start = doc.SelectSingleNode("/t:Task/t:Triggers/t:CalendarTrigger/t:StartBoundary", ns)?.InnerText?.Trim();
            if (start != null && start.Length >= 16)
            {
                // "…T12:00:00" is local time; with "Z" or an offset ("Synchronize across time zones") convert it.
                bool hasZone = start.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || start.LastIndexOfAny(new[] { '+', '-' }) > 10;
                if (hasZone && DateTimeOffset.TryParse(start, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dto))
                {
                    var local = dto.ToLocalTime();
                    s.Hour = local.Hour; s.Minute = local.Minute;
                }
                else if (int.TryParse(start.Substring(11, 2), out var h) && int.TryParse(start.Substring(14, 2), out var m)) { s.Hour = h; s.Minute = m; }
            }
            var day = doc.SelectSingleNode("/t:Task/t:Triggers/t:CalendarTrigger/t:ScheduleByWeek/t:DaysOfWeek/*", ns)?.LocalName;
            if (day != null && Enum.TryParse(day, out DayOfWeek d)) s.Day = d;
            s.Elevated = doc.SelectSingleNode("/t:Task/t:Principals/t:Principal/t:RunLevel", ns)?.InnerText == "HighestAvailable";
            s.ExePath = doc.SelectSingleNode("/t:Task/t:Actions/t:Exec/t:Command", ns)?.InnerText?.Trim().Trim('"');
            var en = doc.SelectSingleNode("/t:Task/t:Settings/t:Enabled", ns)?.InnerText;
            s.Enabled = en == null || en == "true";
            return s;
        }

        internal string ToXml(string exePath, string sid)
        {
            var sb = new StringBuilder();
            using (var w = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true }))
            {
                w.WriteStartDocument();
                w.WriteStartElement("Task", TaskNs);
                w.WriteAttributeString("version", "1.2");
                w.WriteStartElement("RegistrationInfo", TaskNs);
                w.WriteElementString("Author", TaskNs, "OrclWAMP");
                w.WriteElementString("Description", TaskNs, "Updates apps with winget once a week (OrclWAMP /upgrade /unattended). Change or remove it in OrclWAMP > App updates > Weekly schedule.");
                w.WriteEndElement();

                w.WriteStartElement("Triggers", TaskNs);
                w.WriteStartElement("CalendarTrigger", TaskNs);
                w.WriteElementString("StartBoundary", TaskNs, NextRun().ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture)); // local time
                w.WriteElementString("Enabled", TaskNs, "true");
                w.WriteStartElement("ScheduleByWeek", TaskNs);
                w.WriteStartElement("DaysOfWeek", TaskNs);
                w.WriteElementString(Day.ToString(), TaskNs, "");
                w.WriteEndElement();
                w.WriteElementString("WeeksInterval", TaskNs, "1");
                w.WriteEndElement();
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("Principals", TaskNs);
                w.WriteStartElement("Principal", TaskNs);
                w.WriteAttributeString("id", "Author");
                w.WriteElementString("UserId", TaskNs, sid);
                w.WriteElementString("LogonType", TaskNs, "InteractiveToken");
                w.WriteElementString("RunLevel", TaskNs, "LeastPrivilege");
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteStartElement("Settings", TaskNs);
                w.WriteElementString("MultipleInstancesPolicy", TaskNs, "IgnoreNew");
                w.WriteElementString("DisallowStartIfOnBatteries", TaskNs, "false");
                w.WriteElementString("StopIfGoingOnBatteries", TaskNs, "false");
                w.WriteElementString("AllowHardTerminate", TaskNs, "true");
                w.WriteElementString("StartWhenAvailable", TaskNs, "true"); // PC was off or asleep: run at the next chance
                w.WriteElementString("RunOnlyIfNetworkAvailable", TaskNs, "false"); // OrclWAMP waits for the network itself
                w.WriteStartElement("IdleSettings", TaskNs);
                w.WriteElementString("StopOnIdleEnd", TaskNs, "false");
                w.WriteElementString("RestartOnIdle", TaskNs, "false");
                w.WriteEndElement();
                w.WriteElementString("AllowStartOnDemand", TaskNs, "true");
                w.WriteElementString("Enabled", TaskNs, "true");
                w.WriteElementString("Hidden", TaskNs, "false");
                w.WriteElementString("RunOnlyIfIdle", TaskNs, "false");
                w.WriteElementString("WakeToRun", TaskNs, "false");
                w.WriteElementString("ExecutionTimeLimit", TaskNs, "PT24H");
                w.WriteElementString("Priority", TaskNs, "7");
                w.WriteEndElement();

                w.WriteStartElement("Actions", TaskNs);
                w.WriteAttributeString("Context", "Author");
                w.WriteStartElement("Exec", TaskNs);
                w.WriteElementString("Command", TaskNs, "\"" + exePath + "\"");
                w.WriteElementString("Arguments", TaskNs, "/upgrade /unattended");
                w.WriteElementString("WorkingDirectory", TaskNs, Path.GetDirectoryName(exePath));
                w.WriteEndElement();
                w.WriteEndElement();

                w.WriteEndElement();
                w.WriteEndDocument();
            }
            return sb.ToString();
        }

        /// <summary>The next time this schedule is due (a start date in the past could count as a missed run).</summary>
        internal DateTime NextRun()
        {
            var now = DateTime.Now;
            var t = now.Date.AddHours(Hour).AddMinutes(Minute);
            while (t.DayOfWeek != Day || t <= now) t = t.AddDays(1);
            return t;
        }

        static void Register(string name, string xml) => WithRoot(folder =>
        {
            Release(Call(folder, "RegisterTask", @"\" + name, xml, TaskCreateOrUpdate, null, null, LogonInteractiveToken, null));
            return 0;
        });

        /// <summary>Deletes the task; false if there was none.</summary>
        static bool Delete(string name) => WithRoot(folder =>
        {
            try { Call(folder, "DeleteTask", @"\" + name, 0); return true; }
            catch (TargetInvocationException ex) when (ex.InnerException != null && IsNotFound(ex.InnerException)) { return false; }
        });

        // ---- what the dialog calls (on a background thread) ----
        /// <summary>Creates or replaces the current user's task. Returns null on success, else an error text.</summary>
        public string Save()
        {
            var sid = Ui.CurrentSid();
            try
            {
                UpdateSchedule previous = null;
                try { previous = Query(); } catch { }
                var exe = Application.ExecutablePath;
                if (ExeOnRemovableDrive()) { CopySelf(LocalCopyPath); exe = LocalCopyPath; }
                Register(TaskNameFor(sid), ToXml(exe, sid));
                if (previous?.ExePath != null && !string.Equals(exe, LocalCopyPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(previous.ExePath, LocalCopyPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(LocalCopyPath); } catch { }
                return null;
            }
            catch (Exception ex) when (IsAccessDenied(ex)) { return T("Windows did not allow OrclWAMP to change this task. Remove it in Task Scheduler, then try again."); }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Removes the current user's task. Returns null on success (or if there was none), else an error text.</summary>
        public static string Remove()
        {
            try
            {
                var current = Query();
                if (current == null) return null;
                Delete(TaskName);
                if (current.ExePath != null && string.Equals(current.ExePath, LocalCopyPath, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(LocalCopyPath); } catch { }
                return null;
            }
            catch (Exception ex) when (IsAccessDenied(ex)) { return T("Windows did not allow OrclWAMP to change this task. Remove it in Task Scheduler, then try again."); }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>Starts the task now (for testing).</summary>
        public static string RunNow()
        {
            try
            {
                return WithRoot(folder =>
                {
                    object task = null, running = null;
                    try
                    {
                        task = Call(folder, "GetTask", @"\" + TaskName);
                        running = Call(task, "Run", new object[] { null });
                        return (string)null;
                    }
                    finally { Release(running, task); }
                });
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>
        /// Copies the running exe to <paramref name="target"/>. A copy that is running right now (a scheduled
        /// update in progress) is renamed out of the way instead of overwritten.
        /// </summary>
        static void CopySelf(string target)
        {
            var self = Application.ExecutablePath;
            if (string.Equals(Path.GetFullPath(self), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) return;
            var dir = Path.GetDirectoryName(target);
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, Path.GetFileName(target) + ".old-*")) try { File.Delete(old); } catch { }
            var tmp = target + ".new-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.Copy(self, tmp, true);
            try
            {
                if (File.Exists(target))
                {
                    try { File.Delete(target); }
                    catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                    { File.Move(target, target + ".old-" + DateTime.Now.Ticks); }
                }
                File.Move(tmp, target);
            }
            catch { try { File.Delete(tmp); } catch { } throw; }
        }

        /// <summary>A removable or network drive, or a temp folder (an exe started from inside a zip), may be gone when the task runs.</summary>
        public static bool ExeOnRemovableDrive()
        {
            try
            {
                var exe = Application.ExecutablePath;
                if (Ui.IsNetworkPath(exe)) return true;
                var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\') + "\\";
                if (Path.GetFullPath(exe).StartsWith(temp, StringComparison.OrdinalIgnoreCase)) return true;
                var t = new DriveInfo(Path.GetPathRoot(exe)).DriveType;
                return t == DriveType.Removable || t == DriveType.CDRom;
            }
            catch { return false; }
        }

        /// <summary>The file version of an exe, or null.</summary>
        public static Version FileVersionOf(string path)
        {
            try { var v = FileVersionInfo.GetVersionInfo(path); return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart); }
            catch { return null; }
        }
    }
}
