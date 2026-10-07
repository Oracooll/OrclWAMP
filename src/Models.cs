using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace OrclWAMP
{
    internal enum AppCategory
    {
        Winget,       // installable from the winget community source (or another configured source)
        Store,        // installable from the msstore source
        NotAvailable, // installed, but no winget package found -> manual install list
        System        // Windows components, drivers, runtimes without a package (hidden by default)
    }

    internal sealed class AppEntry
    {
        public string Name = "";
        public string Id = "";
        public string Version = "";
        public string Source = "";
        public AppCategory Category;
        public bool Selected;
        public string Note = "";
        public bool ManuallyAdded;
        // Only for apps without a winget package:
        public string Publisher = "";
        public string Homepage = "";     // from the uninstall registry entry / web-app address
        public string DownloadUrl = "";  // set by the user (or a known official link)

        public bool IsInstallable => Category == AppCategory.Winget || Category == AppCategory.Store;

        public ManualApp ToManual() => new ManualApp
        {
            Name = Name, Version = Version, Id = Id, Publisher = Publisher, Homepage = Homepage, DownloadUrl = DownloadUrl,
            Note = Note.StartsWith("Not in winget") ? "" : Note
        };
    }

    [DataContract]
    internal sealed class PackageRef
    {
        [DataMember(Order = 1)] public string Id = "";
        [DataMember(Order = 2)] public string Name = "";
        [DataMember(Order = 3)] public string Version = "";
        [DataMember(Order = 4)] public string Source = "winget";
        [OnDeserializing] void OnDeserializing(StreamingContext c) { Id = ""; Name = ""; Version = ""; Source = "winget"; }
    }

    [DataContract]
    internal sealed class ManualApp
    {
        [DataMember(Order = 1)] public string Name = "";
        [DataMember(Order = 2)] public string Version = "";
        [DataMember(Order = 3)] public string Id = "";
        [DataMember(Order = 4)] public string Publisher = "";
        [DataMember(Order = 5)] public string Homepage = "";
        [DataMember(Order = 6)] public string DownloadUrl = "";
        [DataMember(Order = 7)] public string Note = "";

        [OnDeserializing] void OnDeserializing(StreamingContext c) { Name = Version = Id = Publisher = Homepage = DownloadUrl = Note = ""; }
    }

    /// <summary>The migration package / profile file (OrclWAMP-packages.json).</summary>
    [DataContract]
    internal sealed class Manifest
    {
        [DataMember(Order = 1)] public string Tool = "OrclWAMP";
        [DataMember(Order = 2)] public int FormatVersion = 1;
        [DataMember(Order = 3)] public string CreatedUtc = "";
        [DataMember(Order = 4)] public string SourceComputer = "";
        [DataMember(Order = 5)] public bool PinVersions;
        [DataMember(Order = 6)] public bool RequestAdmin;
        [DataMember(Order = 7)] public bool RestartWhenDone;
        [DataMember(Order = 8)] public int TimeoutMinutes = DefaultTimeout;
        [DataMember(Order = 9)] public List<PackageRef> Packages = new List<PackageRef>();
        [DataMember(Order = 10)] public List<ManualApp> ManualApps = new List<ManualApp>();

        public const int DefaultTimeout = 60;

        // DataContractJsonSerializer does not run field initializers – set the defaults here.
        [OnDeserializing]
        void OnDeserializing(StreamingContext c)
        {
            Tool = "OrclWAMP"; FormatVersion = 1; TimeoutMinutes = DefaultTimeout;
            Packages = new List<PackageRef>(); ManualApps = new List<ManualApp>();
        }

        // A package ID / source name never contains whitespace or quotes; anything else is a damaged or tampered file.
        static readonly System.Text.RegularExpressions.Regex SafeToken = new System.Text.RegularExpressions.Regex(@"^[^\s""]+$");

        public static Manifest Load(string path)
        {
            string text = File.ReadAllText(path, Encoding.UTF8);
            Manifest m;
            if (text.IndexOf("\"PackageIdentifier\"", StringComparison.Ordinal) >= 0)
                m = FromWingetExport(Json.Parse<WingetExportFile>(text));
            else
                m = Json.Parse<Manifest>(text);
            if (m == null) throw new InvalidDataException("The file is empty or not a valid OrclWAMP package.");
            m.Packages = m.Packages ?? new List<PackageRef>();
            m.ManualApps = m.ManualApps ?? new List<ManualApp>();
            if (m.TimeoutMinutes <= 0) m.TimeoutMinutes = DefaultTimeout;
            m.Packages.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Id));
            foreach (var p in m.Packages)
            {
                p.Id = p.Id.Trim();
                if (string.IsNullOrWhiteSpace(p.Source)) p.Source = "winget";
                if (string.IsNullOrWhiteSpace(p.Name)) p.Name = p.Id;
                p.Version = p.Version ?? "";
            }
            var bad = m.Packages.Where(p => !SafeToken.IsMatch(p.Id) || !SafeToken.IsMatch(p.Source)).Select(p => p.Id).ToList();
            if (bad.Count > 0) throw new InvalidDataException("The file contains invalid package IDs: " + string.Join(", ", bad.Take(5)));
            m.ManualApps.RemoveAll(a => a == null || string.IsNullOrWhiteSpace(a.Name));
            foreach (var a in m.ManualApps)
            {
                a.Version = a.Version ?? ""; a.Id = a.Id ?? ""; a.Publisher = a.Publisher ?? "";
                a.Homepage = a.Homepage ?? ""; a.DownloadUrl = a.DownloadUrl ?? ""; a.Note = a.Note ?? "";
            }
            return m;
        }

        public void Save(string path) => File.WriteAllText(path, Json.Serialize(this), new UTF8Encoding(false));

        static Manifest FromWingetExport(WingetExportFile f)
        {
            var m = new Manifest { Tool = "winget export", CreatedUtc = DateTime.UtcNow.ToString("o"), SourceComputer = "(winget export)" };
            foreach (var s in f?.Sources ?? new List<WingetExportSource>())
            {
                string src = s.SourceDetails?.Name;
                if (string.IsNullOrEmpty(src)) src = "winget";
                foreach (var p in s.Packages ?? new List<WingetExportPackage>())
                    m.Packages.Add(new PackageRef { Id = p.PackageIdentifier, Name = p.PackageIdentifier, Version = p.Version ?? "", Source = src });
            }
            return m;
        }
    }

    // ---- winget export / import file format ----
    [DataContract] internal sealed class WingetExportFile { [DataMember] public List<WingetExportSource> Sources; }
    [DataContract] internal sealed class WingetExportSource { [DataMember] public List<WingetExportPackage> Packages; [DataMember] public WingetSourceDetails SourceDetails; }
    [DataContract] internal sealed class WingetExportPackage { [DataMember] public string PackageIdentifier; [DataMember] public string Version; }
    [DataContract] internal sealed class WingetSourceDetails { [DataMember] public string Name; }

    internal static class Json
    {
        public static T Parse<T>(string text)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text.TrimStart('﻿'))))
                return (T)ser.ReadObject(ms);
        }

        public static string Serialize<T>(T obj)
        {
            var ser = new DataContractJsonSerializer(typeof(T));
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  "))
                {
                    ser.WriteObject(w, obj);
                    w.Flush();
                }
                // DataContractJsonSerializer escapes '/' as '\/', which is valid but ugly.
                return Encoding.UTF8.GetString(ms.ToArray()).Replace("\\/", "/");
            }
        }

        /// <summary>Minimal JSON string escaping for hand-written JSON.</summary>
        public static string Str(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }
    }
}
