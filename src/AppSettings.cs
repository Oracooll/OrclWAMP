using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace OrclWAMP
{
    /// <summary>
    /// Small per-user preferences file: %APPDATA%\OrclWAMP\settings.ini (Key=Value lines).
    /// The App updates window and a scheduled update run may use it at the same time, so changes are made
    /// under a named lock and swapped in atomically.
    /// </summary>
    internal static class AppSettings
    {
        static readonly object Gate = new object();
        const string LockName = @"Local\OrclWAMP-Settings";
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrclWAMP", "settings.ini");

        public static string Get(string key)
        {
            lock (Gate) { return (Read() ?? new Dictionary<string, string>()).TryGetValue(key, out var v) ? v : null; }
        }

        /// <summary>All settings at once; null if the file exists but can't be read.</summary>
        public static Dictionary<string, string> Snapshot()
        {
            lock (Gate) return Read();
        }

        /// <summary>Saves one setting. False if it could not be saved.</summary>
        public static bool Set(string key, string value) => Set(new Dictionary<string, string> { { key, value } });

        /// <summary>Saves several settings in one go. False if they could not be saved.</summary>
        public static bool Set(IDictionary<string, string> values)
        {
            lock (Gate)
            {
                Mutex m = null;
                bool owned = false;
                try
                {
                    try
                    {
                        m = new Mutex(false, LockName);
                        try { owned = m.WaitOne(5000); } catch (AbandonedMutexException) { owned = true; }
                        if (!owned) return false; // someone else is writing right now
                    }
                    catch (UnauthorizedAccessException) { } // made by an elevated OrclWAMP: carry on without it
                    catch (WaitHandleCannotBeOpenedException) { }

                    var all = Read();
                    if (all == null) return false; // never overwrite settings we couldn't read
                    foreach (var kv in values) all[kv.Key] = kv.Value;
                    var dir = Path.GetDirectoryName(FilePath);
                    Directory.CreateDirectory(dir);
                    CleanTempFiles(dir);
                    // Write a temp file and swap it in, so nobody ever reads half a file.
                    var tmp = FilePath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
                    File.WriteAllLines(tmp, all.Select(kv => kv.Key + "=" + kv.Value));
                    try
                    {
                        for (int attempt = 0; ; attempt++)
                        {
                            try
                            {
                                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null, true);
                                else File.Move(tmp, FilePath);
                                return true;
                            }
                            catch (IOException) when (attempt < 40) { Thread.Sleep(50); }
                            catch (UnauthorizedAccessException) when (attempt < 40) { Thread.Sleep(50); }
                        }
                    }
                    finally { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
                }
                catch { return false; }
                finally
                {
                    if (owned) try { m.ReleaseMutex(); } catch { }
                    m?.Dispose();
                }
            }
        }

        /// <summary>Temp files left behind by a crash or a killed process.</summary>
        static void CleanTempFiles(string dir)
        {
            try
            {
                foreach (var f in new DirectoryInfo(dir).GetFiles("settings.ini.*.tmp"))
                    if ((DateTime.UtcNow - f.LastWriteTimeUtc).TotalMinutes > 5) try { f.Delete(); } catch { }
            }
            catch { }
        }

        /// <summary>All settings; empty if there is no file yet, null if it exists but stays unreadable.</summary>
        static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (!File.Exists(FilePath)) return d;
                    foreach (var line in File.ReadAllLines(FilePath))
                    {
                        int i = line.IndexOf('=');
                        if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                    }
                    return d;
                }
                catch (FileNotFoundException) { return d; }
                catch (DirectoryNotFoundException) { return d; }
                catch (IOException) when (attempt < 40) { d.Clear(); Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 40) { d.Clear(); Thread.Sleep(50); }
                catch { return null; }
            }
        }
    }
}
