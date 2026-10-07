using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("OrclWAMP")]
[assembly: AssemblyDescription("Oracooll Winget App Migration Tool")]
[assembly: AssemblyCompany("Oracooll")]
[assembly: AssemblyProduct("OrclWAMP - Oracooll Winget App Migration Tool")]
[assembly: AssemblyCopyright("Copyright © Oracooll 2026 - MIT License")]
[assembly: AssemblyVersion("1.1.1.0")]
[assembly: AssemblyFileVersion("1.1.1.0")]
[assembly: AssemblyInformationalVersion("1.1.001")]
[assembly: ComVisible(false)]

namespace OrclWAMP
{
    internal sealed class Options
    {
        public bool Restore, Scan, Unattended, Elevated, Help;
        public string ManifestPath, CsvPath, PackageDir, LauncherSid;
        public string Error;

        public static Options Parse(string[] args)
        {
            var o = new Options();
            for (int i = 0; i < args.Length; i++)
            {
                var a = args[i];
                string key = a.TrimStart('/', '-').ToLowerInvariant();
                bool isSwitch = a.StartsWith("/") || a.StartsWith("-");
                string Next() => i + 1 < args.Length && !args[i + 1].StartsWith("/") && !args[i + 1].StartsWith("-") ? args[++i] : null;

                if (!isSwitch) { o.ManifestPath = a; o.Restore = true; continue; }
                switch (key)
                {
                    case "restore": o.Restore = true; o.ManifestPath = Next() ?? o.ManifestPath; break;
                    case "scan": o.Scan = true; break;
                    case "unattended": case "auto": case "quiet": o.Unattended = true; break;
                    case "elevated": o.Elevated = true; break;
                    case "launcher": o.LauncherSid = Next(); break;
                    case "csv": o.CsvPath = Next(); if (o.CsvPath == null) o.Error = "/csv needs a file name."; break;
                    case "package": o.PackageDir = Next(); if (o.PackageDir == null) o.Error = "/package needs a folder."; break;
                    case "?": case "h": case "help": o.Help = true; break;
                    default: o.Error = "Unknown option: " + a; break;
                }
            }
            return o;
        }

        public const string HelpText =
            "OrclWAMP - Oracooll Winget App Migration Tool\r\n\r\n" +
            "OrclWAMP.exe                       Scan this PC (or restore, if a package file is next to the exe)\r\n" +
            "OrclWAMP.exe /scan                 Always open the scanner\r\n" +
            "OrclWAMP.exe /restore [file]       Install apps from a migration package\r\n" +
            "OrclWAMP.exe /restore /unattended  Install everything without asking\r\n" +
            "OrclWAMP.exe /package <folder>     Scan and write a migration package without UI\r\n" +
            "OrclWAMP.exe /csv <file>           Scan and save the app list as CSV without UI\r\n";
    }

    internal static class Program
    {
        public const string AppName = "OrclWAMP";
        public const string ManifestFileName = "OrclWAMP-packages.json";
        public const string RepoUrl = "https://github.com/Oracooll/OrclWAMP";

        public static string AppDir => Path.GetDirectoryName(Application.ExecutablePath);
        public const string VersionText = "1.1.001";

        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            System.Net.ServicePointManager.SecurityProtocol |= System.Net.SecurityProtocolType.Tls12;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);

            var o = Options.Parse(args);
            if (o.Error != null) { MessageBox.Show(o.Error + "\r\n\r\n" + Options.HelpText, AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return 2; }
            if (o.Help) { MessageBox.Show(Options.HelpText, AppName, MessageBoxButtons.OK, MessageBoxIcon.Information); return 0; }

            if (o.CsvPath != null || o.PackageDir != null) return Headless(o);

            string manifest = o.ManifestPath;
            if (manifest == null && !o.Scan)
            {
                var candidate = Path.Combine(AppDir, ManifestFileName);
                if (File.Exists(candidate)) manifest = candidate;
            }
            if (manifest == null && o.Restore)
            {
                using (var dlg = new OpenFileDialog { Title = "Open migration package", Filter = "OrclWAMP package (*.json)|*.json|All files|*.*" })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return 0;
                    manifest = dlg.FileName;
                }
            }

            if (manifest != null)
            {
                Manifest m;
                try { m = Manifest.Load(manifest); }
                catch (Exception ex)
                {
                    MessageBox.Show("Could not read the package file:\r\n" + manifest + "\r\n\r\n" + ex.Message, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return 1;
                }
                var form = new RestoreForm(m, Path.GetFullPath(manifest), o.Unattended, o.Elevated, o.LauncherSid);
                Application.Run(form);
                return form.ExitCode;
            }
            else
            {
                Application.Run(new MainForm());
            }
            return 0;
        }

        /// <summary>/csv and /package: scan without showing a window (for scripts / IT use).</summary>
        static int Headless(Options o)
        {
            try
            {
                if (!Winget.IsAvailable) return 3;
                var entries = Scanner.ScanAsync(null, CancellationToken.None).GetAwaiter().GetResult();
                if (o.CsvPath != null) PackageWriter.WriteCsv(Path.GetFullPath(o.CsvPath), entries);
                if (o.PackageDir != null)
                {
                    var m = MainForm.BuildManifest(entries, false, false, false, Manifest.DefaultTimeout);
                    PackageWriter.Write(Path.Combine(Path.GetFullPath(o.PackageDir), PackageWriter.FolderName), m, Application.ExecutablePath);
                }
                return 0;
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "OrclWAMP-error.log"), ex.ToString()); } catch { }
                return 1;
            }
        }

        static int _showingError;
        static void ShowError(Exception ex)
        {
            if (ex == null || Interlocked.Exchange(ref _showingError, 1) == 1) return;
            try { MessageBox.Show("Unexpected error:\r\n\r\n" + ex.Message + "\r\n\r\n" + ex.GetType().Name, AppName, MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { _showingError = 0; }
        }
    }
}
