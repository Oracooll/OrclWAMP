using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using static OrclWAMP.Lang;

namespace OrclWAMP
{
    [DataContract]
    internal sealed class FilesPack
    {
        [DataMember(Order = 1)] public string Tool = "OrclWAMP";
        [DataMember(Order = 2)] public string CreatedUtc = "";
        [DataMember(Order = 3)] public string SourceComputer = "";
        [DataMember(Order = 4)] public List<FolderData> Folders = new List<FolderData>();
        [OnDeserializing] void OnDeserializing(StreamingContext c) { Tool = CreatedUtc = SourceComputer = ""; Folders = new List<FolderData>(); }
    }

    [DataContract]
    internal sealed class FolderData
    {
        [DataMember(Order = 1)] public string Key = "";        // Desktop, Documents, … or Custom
        [DataMember(Order = 2)] public string Name = "";
        [DataMember(Order = 3)] public string SourcePath = "";
        [DataMember(Order = 4)] public string SubDir = "";      // folder inside Files\ in the package
        [DataMember(Order = 5)] public long Bytes;
        [DataMember(Order = 6)] public int FileCount;
        /// <summary>Known folders are stored in English and translated only for display (old and new PC may use different languages).</summary>
        public string DisplayName => Key == "Custom" ? Name : T(Name);
        [OnDeserializing] void OnDeserializing(StreamingContext c) { Key = Name = SourcePath = SubDir = ""; }
    }

    internal sealed class CopyResult
    {
        public int Copied, Skipped, Renamed, Failed;
        public long Bytes;
    }

    internal enum ExistingFile { KeepBoth, Skip, Overwrite }

    /// <summary>Copies the user's personal folders into the package and back onto the new PC.</summary>
    internal static class PersonalFiles
    {
        public const string FolderName = "Files";
        public const string FileName = "files.json";

        public static readonly (string Key, string Name, Guid Id)[] Known =
        {
            ("Desktop", "Desktop", new Guid("B4BFCC3A-DB2C-424C-B029-7FE99A87C641")),
            ("Documents", "Documents", new Guid("FDD39AD0-238F-46AF-ADB4-6C85480369C7")),
            ("Pictures", "Pictures", new Guid("33E28130-4E1E-4676-835A-98395C3BC3BB")),
            ("Music", "Music", new Guid("4BD8D571-6D19-48D3-BE97-422220080E43")),
            ("Videos", "Videos", new Guid("18989B1D-99B5-455B-841C-AB7C74E4DDFC")),
            ("Downloads", "Downloads", new Guid("374DE290-123F-4565-9164-39C4925E467B")),
        };

        public static string KnownPath(string key)
        {
            var k = Known.FirstOrDefault(x => x.Key == key);
            if (k.Key == null) return null;
            try
            {
                var id = k.Id;
                if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = Marshal.PtrToStringUni(p);
                    Marshal.FreeCoTaskMem(p);
                    return s;
                }
            }
            catch { }
            return null;
        }

        /// <summary>Folders kept in OneDrive come back by themselves when you sign in – no need to copy them.</summary>
        public static bool IsOneDrive(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
            {
                var root = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrEmpty(root) && path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>True if the folder contains links (junctions / symlinks) or sub-folders that can't be read.</summary>
        public static bool HasSpecialEntries(string dir)
        {
            var stack = new Stack<DirectoryInfo>();
            stack.Push(new DirectoryInfo(dir));
            while (stack.Count > 0)
            {
                FileSystemInfo[] entries;
                try { entries = stack.Pop().GetFileSystemInfos(); } catch { return true; }
                foreach (var e in entries)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) return true;
                    if (e is DirectoryInfo d) stack.Push(d);
                }
            }
            return false;
        }

        /// <summary>Total size and number of files (junctions / symbolic links are not followed).</summary>
        public static (long Bytes, int Files) Measure(string dir, CancellationToken ct)
        {
            long bytes = 0; int files = 0;
            foreach (var f in Walk(dir, ct)) { bytes += f.Length; files++; }
            return (bytes, files);
        }

        static IEnumerable<FileInfo> Walk(string dir, CancellationToken ct)
        {
            var stack = new Stack<DirectoryInfo>();
            try { stack.Push(new DirectoryInfo(dir)); } catch { yield break; }
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var d = stack.Pop();
                FileSystemInfo[] entries;
                try { entries = d.GetFileSystemInfos(); } catch { continue; }
                foreach (var e in entries)
                {
                    if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue; // junctions like "My Music", OneDrive placeholders
                    if (e is DirectoryInfo sub) stack.Push(sub);
                    else if (e is FileInfo fi) yield return fi;
                }
            }
        }

        /// <summary>Copies a folder tree. <paramref name="progress"/> gets (bytes done so far, current file).</summary>
        /// <summary>Script folders that would run code on the new PC (PowerShell profiles/modules inside Documents).</summary>
        public static readonly string[] ScriptFolders = { "WindowsPowerShell", "PowerShell" };

        public static CopyResult Copy(string src, string dst, ExistingFile mode, string renameTag, long sizeLimit,
                                      Action<long, string> progress, Action<string> log, CancellationToken ct, string[] skipTopFolders = null, bool exact = false)
        {
            var skipped = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new CopyResult();
            var srcRoot = new DirectoryInfo(src).FullName.TrimEnd('\\');
            var dstRoot = Path.GetFullPath(dst).TrimEnd('\\') + "\\";
            var buffer = new byte[1 << 20];
            foreach (var f in Walk(srcRoot, ct))
            {
                ct.ThrowIfCancellationRequested();
                var rel = f.FullName.Substring(srcRoot.Length).TrimStart('\\');
                var top = rel.IndexOf('\\') > 0 ? rel.Substring(0, rel.IndexOf('\\')) : null;
                if (top != null && skipTopFolders != null && skipTopFolders.Contains(top, StringComparer.OrdinalIgnoreCase))
                {
                    res.Skipped++;
                    if (skipped.Add(top)) log?.Invoke("   " + F("skipped the \"{0}\" folder – it contains scripts that would run on this PC. Copy it by hand if you need it.", top));
                    continue;
                }
                var target = Path.GetFullPath(Path.Combine(dst, rel));
                if (!target.StartsWith(dstRoot, StringComparison.OrdinalIgnoreCase)) { res.Failed++; continue; } // never write outside the destination
                var name = Path.GetFileName(rel);
                // Files that make Explorer contact other computers just by showing the folder (sign-in hash leaks).
                if (!exact && (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".scf", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase))) { res.Skipped++; continue; } // (not for backups: exact)
                try
                {
                    if (sizeLimit > 0 && f.Length >= sizeLimit)
                    {
                        res.Skipped++;
                        log?.Invoke(F("   skipped (too big for this drive's FAT32 format): {0}", rel));
                        continue;
                    }
                    if (File.Exists(target))
                    {
                        var t = new FileInfo(target);
                        if (t.Length == f.Length && Math.Abs((t.LastWriteTimeUtc - f.LastWriteTimeUtc).TotalSeconds) < 2) { res.Skipped++; continue; } // identical
                        if (mode == ExistingFile.Skip) { res.Skipped++; continue; }
                        if (mode == ExistingFile.KeepBoth)
                        {
                            target = UniqueName(target, renameTag);
                            res.Renamed++;
                        }
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    CopyFile(f, target, buffer, n => { res.Bytes += n; progress?.Invoke(res.Bytes, rel); }, ct);
                    res.Copied++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    res.Failed++;
                    log?.Invoke(F("   could not copy {0}: {1}", rel, ex.Message));
                }
            }
            return res;
        }

        static void CopyFile(FileInfo f, string target, byte[] buffer, Action<long> advance, CancellationToken ct)
        {
            var part = target + ".orclwamp-part";
            try
            {
                using (var src = new FileStream(f.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, buffer.Length))
                using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
                {
                    int n;
                    while ((n = src.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        dst.Write(buffer, 0, n);
                        advance(n);
                    }
                }
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
                var t = new FileInfo(target);
                t.CreationTimeUtc = f.CreationTimeUtc;
                t.LastWriteTimeUtc = f.LastWriteTimeUtc;
            }
            catch
            {
                try { File.Delete(part); } catch { }
                throw;
            }
        }

        static string UniqueName(string path, string tag)
        {
            var dir = Path.GetDirectoryName(path);
            var stem = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            var p = Path.Combine(dir, $"{stem} ({tag}){ext}");
            for (int i = 2; File.Exists(p); i++) p = Path.Combine(dir, $"{stem} ({tag} {i}){ext}");
            return p;
        }

        /// <summary>FAT32 can't hold files of 4 GB or more.</summary>
        public static long SizeLimitFor(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).DriveFormat.StartsWith("FAT", StringComparison.OrdinalIgnoreCase) ? 4L * 1024 * 1024 * 1024 - 1 : 0; }
            catch { return 0; }
        }

        public static long FreeSpace(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; } catch { return long.MaxValue; }
        }

        /// <summary>Where a folder from the old PC goes on this PC.</summary>
        public static string DefaultTarget(FolderData f, string sourceComputer, bool allowSamePath = true)
        {
            var known = Known.Any(k => k.Key == f.Key) ? KnownPath(f.Key) : null;
            if (known != null) return known;
            // Custom folder: the same place only if that is clearly a normal user folder (the package could have been edited),
            // otherwise "<profile>\From <PC>\<name>".
            if (allowSamePath && IsSafeCustomTarget(f.SourcePath)) return Path.GetFullPath(f.SourcePath);
            var name = SafeName(Path.GetFileName((f.SourcePath ?? "").TrimEnd('\\')));
            if (name.Trim('.', ' ').Length == 0) name = "Folder";          // ".." would climb out of the "From <PC>" folder
            var pc = SafeName(sourceComputer);
            if (pc.Trim('.', ' ').Length == 0) pc = "old PC";
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "From " + pc, name);
        }

        /// <summary>
        /// A path that may receive files from a package: rooted, local, not a drive root, not inside Windows / program /
        /// app-data / start-up / .ssh folders, and either inside the user's profile or on a fixed non-system drive.
        /// </summary>
        public static bool IsSafeCustomTarget(string path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || path.StartsWith(@"\\") || path.Length < 4 || path[1] != ':' || path[2] != '\\') return false;
                var full = Path.GetFullPath(path).TrimEnd('\\');
                if (full.Length <= 3) return false;                                   // a whole drive
                bool Under(string root) => !string.IsNullOrEmpty(root) && (full + "\\").StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
                foreach (var sys in new[]
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                })
                    if (Under(sys)) return false;
                var lower = full.ToLowerInvariant() + "\\";
                if (lower.Contains("\\appdata\\") || lower.Contains("\\.ssh\\") || lower.Contains("\\start menu\\") || lower.Contains("\\startup\\")) return false;
                // Dot-folders (.config, .ipython…) and the OneDrive root hold tool settings / redirected Documents.
                if (full.Split('\\').Any(seg => seg.StartsWith("."))) return false;
                foreach (var v in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
                {
                    var od = Environment.GetEnvironmentVariable(v);
                    if (!string.IsNullOrEmpty(od) && string.Equals(full, od.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                }
                if (lower.Contains("\\windowspowershell\\") || lower.Contains("\\powershell\\") || lower.Contains("\\.vscode\\") || lower.Contains("\\.git\\")) return false;
                // Old junctions like "Application Data" or "Local Settings" lead into AppData without saying so.
                for (var d = new DirectoryInfo(full); d != null; d = d.Parent)
                    if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.Equals(full, profile.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false; // the profile root itself (.gitconfig…)
                if (Under(profile)) return true;
                var root = Path.GetPathRoot(full);
                var sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
                if (string.Equals(root, sysRoot, StringComparison.OrdinalIgnoreCase)) return false;   // e.g. C:\Users\Other, C:\Something
                return Directory.Exists(root) && new DriveInfo(root).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }

        public static string SafeName(string s) =>
            new string((s ?? "").Where(ch => Path.GetInvalidFileNameChars().All(bad => bad != ch)).ToArray()).Trim();

        // ---------------------------------------------------------------- package side

        /// <summary>Old PC: copy the chosen folders into &lt;package&gt;\Files.</summary>
        public static FilesPack Capture(IList<FolderData> folders, string filesDir, Action<long, long, string> progress, Action<string> log, CancellationToken ct)
        {
            var pack = new FilesPack { CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = Environment.MachineName };
            long total = folders.Sum(f => f.Bytes), done = 0;
            long limit = SizeLimitFor(filesDir);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in folders)
            {
                ct.ThrowIfCancellationRequested();
                var sub = SafeName(f.Key == "Custom" ? Path.GetFileName(f.SourcePath.TrimEnd('\\')) : f.Key);
                if (sub.Length == 0) sub = "Folder";
                var unique = sub;
                for (int i = 2; !used.Add(unique); i++) unique = sub + " " + i;
                // Never copy the package into itself (e.g. package saved on the Desktop while the Desktop is being copied).
                var srcFull = Path.GetFullPath(f.SourcePath).TrimEnd('\\') + "\\";
                if ((Path.GetFullPath(filesDir).TrimEnd('\\') + "\\").StartsWith(srcFull, StringComparison.OrdinalIgnoreCase))
                    throw new IOException(F("The package folder is inside \"{0}\", which is being copied. Save the package somewhere else (e.g. a USB drive).", f.DisplayName));
                log?.Invoke(F("Copying {0}…", f.DisplayName));
                long before = done;
                var r = Copy(f.SourcePath, Path.Combine(filesDir, unique), ExistingFile.Overwrite, "", limit,
                             (b, file) => { done = before + b; progress?.Invoke(done, total, file); }, log, ct);
                done = before + r.Bytes;
                log?.Invoke(F("   {0} files copied, {1} failed.", r.Copied, r.Failed));
                pack.Folders.Add(new FolderData { Key = f.Key, Name = f.Name, SourcePath = f.SourcePath, SubDir = unique, Bytes = r.Bytes, FileCount = r.Copied });
            }
            File.WriteAllText(Path.Combine(filesDir, FileName), Json.Serialize(pack), new UTF8Encoding(false));
            return pack;
        }

        public static FilesPack Load(string filesDir)
        {
            try
            {
                var file = Path.Combine(filesDir, FileName);
                if (!File.Exists(file)) return null;
                var p = Json.Parse<FilesPack>(File.ReadAllText(file, Encoding.UTF8));
                if (p == null) return null;
                // Only folders that really are inside Files\ (a tampered file must not point elsewhere).
                p.Folders = (p.Folders ?? new List<FolderData>())
                    .Where(f => f != null && !string.IsNullOrEmpty(f.SubDir) && f.SubDir == SafeName(f.SubDir) && f.SubDir.Trim('.', ' ').Length > 0 &&
                                (f.SourcePath ?? "").Length > 3 && Path.IsPathRooted(f.SourcePath) && !f.SourcePath.Replace('/', '\\').Split('\\').Any(seg => seg.Trim().Trim('.').Length == 0 && seg.Length > 0) &&
                                Directory.Exists(Path.Combine(filesDir, f.SubDir)))
                    .ToList();
                foreach (var f in p.Folders) { f.Name = f.Name ?? f.SubDir; f.Key = f.Key ?? "Custom"; f.SourcePath = f.SourcePath ?? ""; }
                p.SourceComputer = p.SourceComputer ?? "";
                return p;
            }
            catch { return null; }
        }

        public static string Size(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            return Math.Max(1, bytes / 1024) + " KB";
        }

        [DllImport("shell32.dll")] static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    }
}
