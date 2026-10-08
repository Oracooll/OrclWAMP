using System;
using System.Linq;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace OrclWAMP
{
    /// <summary>Looks for a newer OrclWAMP release on GitHub (at most once a day, never downloads anything by itself).</summary>
    internal static class UpdateCheck
    {
        [DataContract]
        sealed class Release
        {
            [DataMember(Name = "tag_name")] public string Tag;
            [DataMember(Name = "html_url")] public string Url;
        }

        public sealed class Result { public string Version, Url; }

        public static bool Enabled
        {
            get => AppSettings.Get("UpdateCheck") != "off";
            set => AppSettings.Set("UpdateCheck", value ? "on" : "off");
        }

        /// <summary>Returns the newer release, or null (none, offline, disabled or checked recently).</summary>
        public static async Task<Result> CheckAsync(bool force = false)
        {
            if (!force)
            {
                if (!Enabled) return null;
                if (DateTime.TryParse(AppSettings.Get("UpdateCheckLast"), null, System.Globalization.DateTimeStyles.RoundtripKind, out var last) &&
                    (DateTime.UtcNow - last).TotalHours < 20)
                {
                    // Re-show a newer version found earlier without asking GitHub again.
                    var known = AppSettings.Get("UpdateCheckFound");
                    return IsNewer(known) ? new Result { Version = known, Url = Program.RepoUrl + "/releases/latest" } : null;
                }
            }
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://api.github.com/repos/Oracooll/OrclWAMP/releases/latest");
                req.UserAgent = "OrclWAMP/" + Program.VersionText;
                req.Accept = "application/vnd.github+json";
                var work = Task.Run(() => req.GetResponseAsync()); // proxy detection / DNS can block – keep it off the UI thread
                if (await Task.WhenAny(work, Task.Delay(10000)) != work) { try { req.Abort(); } catch { } return null; }
                string json;
                using (var resp = await work)
                using (var r = new System.IO.StreamReader(resp.GetResponseStream()))
                    json = await r.ReadToEndAsync();
                var rel = Json.Parse<Release>(json);
                var version = (rel?.Tag ?? "").TrimStart('v', 'V');
                AppSettings.Set("UpdateCheckLast", DateTime.UtcNow.ToString("o"));
                AppSettings.Set("UpdateCheckFound", version);
                if (!IsNewer(version)) return null;
                var url = Downloads.IsWebUrl(rel.Url) && rel.Url.StartsWith(Program.RepoUrl, StringComparison.OrdinalIgnoreCase) ? rel.Url : Program.RepoUrl + "/releases/latest";
                return new Result { Version = version, Url = url };
            }
            catch { return null; }
        }

        /// <summary>"1.4.001" &gt; "1.3.002"? Compares each number part.</summary>
        public static bool IsNewer(string version)
        {
            if (string.IsNullOrWhiteSpace(version)) return false;
            int[] Parts(string v) => v.Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
            var a = Parts(version);
            var b = Parts(Program.VersionText);
            for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
            {
                int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
                if (x != y) return x > y;
            }
            return false;
        }
    }
}
