using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OrclWAMP
{
    /// <summary>
    /// Only links OrclWAMP is sure about are offered: checked official links, the user's own link,
    /// or a web app's own address. Anything else gets no link at all (None).
    /// </summary>
    internal enum LinkKind { Direct, Page, WebApp, Custom, None }

    /// <summary>Where to get an app that winget can't install, and how to download it.</summary>
    internal static class Downloads
    {
        sealed class Rule
        {
            public Regex Match;
            public Regex Publisher; // when set, the app's publisher must match too
            public string Url;
            public bool Direct;
            public string Note;
        }

        // Official links only, each one checked: a direct installer where a stable "latest" link exists,
        // otherwise the vendor's download page. Don't add links that haven't been verified.
        static readonly Rule[] Known =
        {
            // Publisher check: the GOG / other editions of these games must not get the Battle.net installer.
            new Rule { Match = new Regex(@"^(Battle\.net|Diablo|StarCraft|Warcraft|World of Warcraft|Overwatch|Hearthstone|Heroes of the Storm)\b", RegexOptions.IgnoreCase),
                       Publisher = new Regex(@"Blizzard", RegexOptions.IgnoreCase),
                       Url = "https://www.battle.net/download/getInstallerForGame?os=win&gameProgram=BATTLENET_APP&version=Live", Direct = true,
                       Note = "Install Battle.net, then install the game from it" },
            new Rule { Match = new Regex(@"^NVIDIA (Graphics Driver|Control Panel|App|HD Audio|PhysX|FrameView)", RegexOptions.IgnoreCase),
                       Publisher = new Regex(@"NVIDIA", RegexOptions.IgnoreCase),
                       Url = "https://www.nvidia.com/en-us/software/nvidia-app/", Note = "The NVIDIA app installs the latest driver" },
            new Rule { Match = new Regex(@"^AMD (Software|Radeon|Chipset)", RegexOptions.IgnoreCase),
                       Publisher = new Regex(@"Advanced Micro Devices|AMD", RegexOptions.IgnoreCase),
                       Url = "https://www.amd.com/en/support/download/drivers.html", Note = "Driver – AMD auto-detect tool" },
            new Rule { Match = new Regex(@"^(Microsoft 365|Microsoft Office|Microsoft OneNote|Office 16 Click-to-Run)", RegexOptions.IgnoreCase),
                       Publisher = new Regex(@"^Microsoft", RegexOptions.IgnoreCase),
                       Url = "https://www.microsoft365.com/", Note = "Sign in and choose \"Install apps\"" },
            new Rule { Match = new Regex(@"^Samsung Magician", RegexOptions.IgnoreCase),
                       Publisher = new Regex(@"Samsung", RegexOptions.IgnoreCase),
                       Url = "https://semiconductor.samsung.com/consumer-storage/support/tools/", Note = "" },
        };

        /// <summary>Fills in the best link for an app and says what kind of link it is.</summary>
        public static LinkKind Resolve(ManualApp a, out string url, out string note)
        {
            note = a.Note ?? "";
            // The user's own link – but only a real web address (a hand-edited package could contain anything).
            if (IsWebUrl(a.DownloadUrl)) { url = a.DownloadUrl.Trim(); return LinkKind.Custom; }
            var rule = Known.FirstOrDefault(r => r.Match.IsMatch(a.Name ?? "") && (r.Publisher == null || r.Publisher.IsMatch(a.Publisher ?? "")));
            if (rule != null)
            {
                url = rule.Url;
                if (rule.Note.Length > 0) note = rule.Note;
                return rule.Direct ? LinkKind.Direct : LinkKind.Page;
            }
            // A web app's own address – derived from its package name, so it IS the app (never a registry URL).
            var web = Scanner.WebAppAddress(a.Id);
            if (web != null && string.Equals((a.Homepage ?? "").Trim(), web, StringComparison.OrdinalIgnoreCase))
            {
                url = web;
                return LinkKind.WebApp;
            }
            url = "";
            return LinkKind.None;
        }

        public static string SearchUrl(string name) =>
            "https://www.bing.com/search?q=" + Uri.EscapeDataString(CleanName(name) + " download official");

        public static bool IsWebUrl(string s) =>
            !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s.Trim(), UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

        public static string KindText(LinkKind k)
        {
            switch (k)
            {
                case LinkKind.Direct: return "Direct download";
                case LinkKind.Custom: return "Your link";
                case LinkKind.Page: return "Official download page";
                case LinkKind.WebApp: return "Web app address";
                default: return "No link";
            }
        }

        /// <summary>"7-Zip 26.03 (x64)" -> "7-Zip": drops versions, bitness and bracketed suffixes.</summary>
        public static string CleanName(string name)
        {
            var s = (name ?? "").Replace("²", "2");
            s = Regex.Replace(s, @"\s*\((all languages|x64|x86|64-bit|32-bit|64 bit|32 bit|remove only|preview)\)", "", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\s+v?\d+(\.\d+)+.*$", "");
            s = Regex.Replace(s, @"\s+(x64|x86|64-bit|32-bit|64 bit|32 bit)$", "", RegexOptions.IgnoreCase);
            return s.Trim();
        }

        // ---------------------------------------------------------------- downloading

        public static string DownloadFolder
        {
            get
            {
                string root = null;
                try
                {
                    var id = new Guid("374DE290-123F-4565-9164-39C4925E467B"); // FOLDERID_Downloads
                    if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
                    {
                        root = Marshal.PtrToStringUni(p);
                        Marshal.FreeCoTaskMem(p);
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(root))
                    root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
                return Path.Combine(root, "OrclWAMP");
            }
        }

        /// <summary>
        /// Downloads a file. Returns the saved path, or null when the link turned out to be a web page
        /// (the caller then opens it in the browser instead).
        /// </summary>
        public static async Task<string> DownloadAsync(string url, string appName, Action<string> progress, CancellationToken ct)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) OrclWAMP/" + Program.VersionText;
            req.AllowAutoRedirect = true;
            req.MaximumAutomaticRedirections = 15;
            req.Timeout = 30000;
            req.ReadWriteTimeout = 60000;
            using (ct.Register(() => { try { req.Abort(); } catch { } }))
            using (var resp = (HttpWebResponse)await WithStallTimeout(req, req.GetResponseAsync(), 30, ct))
            {
                var type = (resp.ContentType ?? "").ToLowerInvariant();
                if (type.StartsWith("text/html") || type.StartsWith("application/xhtml")) return null;

                Directory.CreateDirectory(DownloadFolder);
                var name = FileNameFor(resp, appName, type);
                var final = UniquePath(Path.Combine(DownloadFolder, name));
                var part = final + ".part";
                long total = resp.ContentLength, done = 0;
                var buffer = new byte[81920];
                try
                {
                    using (var src = resp.GetResponseStream())
                    using (var dst = File.Create(part))
                    {
                        int n, lastPct = -1;
                        while ((n = await WithStallTimeout(req, src.ReadAsync(buffer, 0, buffer.Length, ct), 60, ct)) > 0)
                        {
                            await dst.WriteAsync(buffer, 0, n, ct);
                            done += n;
                            int pct = total > 0 ? (int)(done * 100 / total) : -1;
                            if (pct != lastPct) { lastPct = pct; progress?.Invoke(total > 0 ? pct + " %" : (done / 1048576.0).ToString("0.0") + " MB"); }
                        }
                    }
                    if (total > 0 && done < total) throw new IOException("The download was incomplete.");
                    File.Move(part, final);
                    return final;
                }
                catch
                {
                    try { File.Delete(part); } catch { } // no half-finished files left behind
                    throw;
                }
            }
        }

        static string FileNameFor(HttpWebResponse resp, string appName, string type)
        {
            string name = null;
            var cd = resp.Headers["Content-Disposition"] ?? "";
            var m = Regex.Match(cd, @"filename\*=UTF-8''([^;]+)", RegexOptions.IgnoreCase);
            if (m.Success) name = Uri.UnescapeDataString(m.Groups[1].Value);
            else
            {
                m = Regex.Match(cd, @"filename=""?([^"";]+)""?", RegexOptions.IgnoreCase);
                if (m.Success) name = m.Groups[1].Value;
            }
            if (string.IsNullOrWhiteSpace(name)) name = Uri.UnescapeDataString(Path.GetFileName(resp.ResponseUri.AbsolutePath) ?? "");
            name = new string(name.Where(ch => Path.GetInvalidFileNameChars().All(bad => bad != ch)).ToArray()).Trim();

            var ext = Path.GetExtension(name).ToLowerInvariant();
            bool script = new[] { ".php", ".asp", ".aspx", ".jsp", ".cgi", ".ashx", ".html", ".htm" }.Contains(ext); // "download.php" serving an installer
            if (name.Length > 0 && ext.Length > 1 && ext.Length <= 11 && !script) return name; // has a real extension: keep it as is

            // No usable file name (e.g. ".../getInstallerForGame"): name it after the app, with an extension
            // that matches what the server says it is – only programs get ".exe".
            var safeApp = new string(CleanName(appName).Where(ch => Path.GetInvalidFileNameChars().All(bad => bad != ch)).ToArray()).Trim();
            if (safeApp.Length == 0) safeApp = "download";
            if (type.Contains("msdos") || type.Contains("msdownload") || type.Contains("x-dosexec") || type.Contains("octet-stream") || type.Contains("x-executable")) ext = ".exe";
            else if (type.Contains("x-msi") || type.Contains("ms-installer")) ext = ".msi";
            else if (type.Contains("zip")) ext = ".zip";
            else if (type.Contains("pdf")) ext = ".pdf";
            else ext = ".download";
            return safeApp + " setup" + ext;
        }

        /// <summary>Async network calls ignore HttpWebRequest timeouts – give up if nothing arrives for <paramref name="seconds"/>.</summary>
        static async Task<T> WithStallTimeout<T>(HttpWebRequest req, Task<T> work, int seconds, CancellationToken ct)
        {
            if (await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(seconds), ct)) != work)
            {
                try { req.Abort(); } catch { }
                ct.ThrowIfCancellationRequested();
                throw new IOException("The server stopped responding.");
            }
            return await work;
        }

        static string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (int i = 2; ; i++)
            {
                var p = Path.Combine(dir, $"{stem} ({i}){ext}");
                if (!File.Exists(p)) return p;
            }
        }

        [DllImport("shell32.dll")]
        static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    }
}
