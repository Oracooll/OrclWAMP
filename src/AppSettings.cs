using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OrclWAMP
{
    /// <summary>Small per-user preferences file: %APPDATA%\OrclWAMP\settings.ini (Key=Value lines).</summary>
    internal static class AppSettings
    {
        static readonly object Gate = new object();
        static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OrclWAMP", "settings.ini");

        public static string Get(string key)
        {
            lock (Gate) { return Read().TryGetValue(key, out var v) ? v : null; }
        }

        public static void Set(string key, string value)
        {
            lock (Gate)
            {
                try
                {
                    var all = Read();
                    all[key] = value;
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                    File.WriteAllLines(FilePath, all.Select(kv => kv.Key + "=" + kv.Value));
                }
                catch { }
            }
        }

        static Dictionary<string, string> Read()
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
            }
            catch { }
            return d;
        }
    }
}
